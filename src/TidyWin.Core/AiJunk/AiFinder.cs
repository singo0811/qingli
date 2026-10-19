using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.AiJunk;

public enum FoundKind { Model, Environment }

/// <summary>
/// <see cref="Safe"/> is kept for the cases where there is proof, not a guess: a second copy that can be merged into one
/// without any path going away, and acceleration files the program makes again by itself. Everything else is the
/// person's call, and nothing is ever ticked for them.
/// </summary>
public enum FoundSafety { Safe, Careful, InUse }

/// <summary>What pressing the button does to this item.</summary>
public enum FoundAction
{
    /// <summary>Move to the Recycle Bin. Can be put back; the space returns once the bin is emptied.</summary>
    Recycle,

    /// <summary>Make this path a hard link to the other copy: both paths keep working, the space is freed at once.</summary>
    Merge,
}

/// <param name="Headline">The reason for the verdict, in a few words.</param>
/// <param name="IfDeleted">What would happen, in plain words.</param>
/// <param name="SameAs">The other copy, when this file looks like a duplicate.</param>
public sealed record FoundItem(
    FoundKind Kind, string Path, long Bytes, DateTime NewestWriteUtc, string Owner, string Format,
    FoundSafety Safety, string Headline, string IfDeleted, string? SameAs = null, FoundAction Action = FoundAction.Recycle);

public sealed class FinderOptions
{
    public long MinModelBytes { get; init; } = 200L * 1024 * 1024;
    public long MinEnvBytes { get; init; } = 300L * 1024 * 1024;

    /// <summary>Folders that other pages already manage (Ollama, LM Studio, Hugging Face). Left out so nothing shows twice.</summary>
    public IReadOnlyList<string> ExcludeUnder { get; init; } = [];

    /// <summary>True when a program that is running right now was started from inside this folder.</summary>
    public Func<string, bool> IsRunningUnder { get; init; } = _ => false;

    /// <summary>True when two paths are already the very same file on disk (hard links). Those take no extra space.</summary>
    public Func<string, string, bool> SameFile { get; init; } = (_, _) => false;

    /// <summary>Used to tell a person's own folders from the private folders programs keep in their profile.</summary>
    public string? UserProfile { get; init; }
}

/// <summary>
/// Finds the big things AI work leaves around outside the usual cache folders: model files somebody downloaded by hand and
/// Python environments that every project got its own copy of. It never decides for the person; it says what it found,
/// why it is or is not likely safe to remove, and what would happen.
/// </summary>
public static class AiFinder
{
    // Programs people run models with. Matched on whole folder names, so "jan" cannot match "january".
    private static readonly (string Key, string Name)[] Owners =
    [
        ("comfyui", "ComfyUI"), ("stable-diffusion-webui", "Stable Diffusion WebUI"), ("sd-webui", "Stable Diffusion WebUI"),
        ("automatic1111", "Stable Diffusion WebUI"), ("fooocus", "Fooocus"), ("invokeai", "InvokeAI"),
        ("text-generation-webui", "text-generation-webui"), ("oobabooga", "text-generation-webui"),
        ("koboldcpp", "KoboldCpp"), ("gpt4all", "GPT4All"), ("llama.cpp", "llama.cpp"), ("llamacpp", "llama.cpp"),
        ("kohya", "kohya_ss"), ("onetrainer", "OneTrainer"), ("ai-toolkit", "ai-toolkit"), ("sillytavern", "SillyTavern"),
        ("whisper", "Whisper"), ("jan", "Jan"), ("diffusers", "Diffusers"),
    ];

    private static readonly HashSet<string> ModelFolderHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "model", "checkpoints", "checkpoint", "lora", "loras", "vae", "unet", "clip", "controlnet", "embeddings",
        "diffusers", "weights", "huggingface", "gguf", "llm", "llms",
    };

    // Places that belong to installed programs and games, not to the person's own work.
    private static readonly HashSet<string> AppPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "appdata", "programdata", "wegameapps", "steamapps", "epic games", "riot games", "windowsapps", "program files", "program files (x86)",
    };

    // Dot-folders in the profile that are tool stores a person manages, not a program's private data.
    private static readonly HashSet<string> OwnDotFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cache", ".conda", ".virtualenvs", ".pyenv", ".local", ".ollama", ".lmstudio",
    };

    private static readonly string[] Manifests =
        ["requirements.txt", "requirements_versions.txt", "pyproject.toml", "environment.yml", "environment.yaml", "Pipfile", "poetry.lock", "uv.lock", "setup.py"];

    // Extensions that mean "AI model" on their own, and those that need a model-looking folder to say so.
    private static readonly HashSet<string> SureExt = new(StringComparer.OrdinalIgnoreCase) { ".gguf", ".safetensors", ".ckpt" };
    private static readonly HashSet<string> NeedHintExt = new(StringComparer.OrdinalIgnoreCase) { ".pt", ".pth", ".onnx", ".h5", ".bin" };
    private static readonly HashSet<string> EngineExt = new(StringComparer.OrdinalIgnoreCase) { ".engine", ".plan", ".trt" };

    public static IReadOnlyList<FoundItem> Find(
        IEnumerable<string> roots, IFileSystem fs, FinderOptions? options = null,
        IProgress<DiskProgress>? progress = null, CancellationToken ct = default)
    {
        var o = options ?? new FinderOptions();
        var exclude = o.ExcludeUnder.Select(WindowsPath.Normalize).Where(p => p is not null).Select(p => p!).ToList();

        var models = new List<FsFile>();
        var envRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long files = 0, bytes = 0;

        foreach (var root in roots)
        {
            foreach (var f in fs.EnumerateFilesRecursive(root))
            {
                if (ct.IsCancellationRequested) break;
                files++;
                var size = f.IsLink ? 0 : f.Length;
                bytes += size;
                if (progress is not null && (files & 0x3FFF) == 0) progress.Report(new DiskProgress(files, bytes, f.Path));

                var ext = Path.GetExtension(f.Path.AsSpan());
                if (ext.Length == 0 || f.IsLink) continue;

                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) { exeDirs.Add(DirOf(f.Path)); continue; }

                if (ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(f.Path.AsSpan()).Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase)) envRoots.Add(DirOf(f.Path));
                    continue;
                }

                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = DirOf(f.Path);
                    if (dir.EndsWith(@"\conda-meta", StringComparison.OrdinalIgnoreCase)) envRoots.Add(DirOf(dir));
                    continue;
                }

                if (size < o.MinModelBytes) continue;
                var e = ext.ToString();
                if (!SureExt.Contains(e) && !NeedHintExt.Contains(e) && !EngineExt.Contains(e)) continue;
                if (exclude.Any(x => WindowsPath.IsSameOrUnder(f.Path, x))) continue;
                models.Add(f);
            }
            if (ct.IsCancellationRequested) break;
        }

        var found = new List<FoundItem>();
        found.AddRange(BuildModels(models, exeDirs, o));
        found.AddRange(BuildEnvs(envRoots, exeDirs, fs, o, exclude, ct));
        // 有把握的排最前(它们才是能放心处理的),其次需要判断的,正在使用的垫底;同组内大的在前。
        return found.OrderBy(i => i.Safety == FoundSafety.InUse ? 2 : i.Safety == FoundSafety.Safe ? 0 : 1)
            .ThenByDescending(i => i.Bytes).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ models

    private static IEnumerable<FoundItem> BuildModels(List<FsFile> models, HashSet<string> exeDirs, FinderOptions o)
    {
        // Same file name and size in several places: copies of one thing. Paths that are already the very same file
        // (merged earlier) take no extra space and are not reported at all.
        var groups = new Dictionary<FsFile, List<FsFile>>();
        foreach (var g in models.GroupBy(m => (Path.GetFileName(m.Path).ToLowerInvariant(), m.Length)))
        {
            var kept = new List<FsFile>();
            foreach (var m in g.OrderBy(m => m.LastWriteUtc).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
                if (!kept.Any(k => o.SameFile(k.Path, m.Path))) kept.Add(m);
            foreach (var m in kept) groups[m] = kept;
        }

        foreach (var (m, group) in groups)
        {
            var (owner, ownerRoot) = OwnerOf(m.Path);
            var segments = m.Path.Split('\\');
            var ext = Path.GetExtension(m.Path);
            var hinted = segments.Take(segments.Length - 1).Any(s => ModelFolderHints.Contains(s));

            // A .pt or .onnx lying around a program's own folder is that program's data, not something to offer.
            if (!SureExt.Contains(ext) && !EngineExt.Contains(ext) && owner is null && !hinted) continue;

            var running = ownerRoot is not null && o.IsRunningUnder(ownerRoot);
            var format = ext.TrimStart('.').ToUpperInvariant();
            var original = group[0];
            var isLaterCopy = group.Count > 1 && !ReferenceEquals(original, m);
            var others = group.Count - 1;
            var ownerName = owner ?? Loc.T("未识别位置");

            FoundSafety safety;
            FoundAction action = FoundAction.Recycle;
            string headline, ifDeleted;
            string? sameAs = null;

            if (EngineExt.Contains(ext))
            {
                safety = FoundSafety.Safe;
                headline = Loc.T("TensorRT 加速文件,程序会自己重新生成");
                ifDeleted = Loc.T("它是由原模型转换出来的,和你的显卡绑定。删掉后程序需要用到时会重新生成,会慢几分钟。");
            }
            else if (isLaterCopy && SameVolume(m.Path, original.Path))
            {
                safety = FoundSafety.Safe;
                action = FoundAction.Merge;
                sameAs = original.Path;
                headline = Loc.T("和另一份内容一样,可以合并");
                ifDeleted = Loc.F("不会删除。合并后这个位置的文件还在,用它的程序照常能用,磁盘上只占一份(另一份在「{0}」)。合并前会逐字节核对两份完全一致。",DirOf(original.Path));
            }
            else if (isLaterCopy)
            {
                safety = FoundSafety.Careful;
                sameAs = original.Path;
                headline = Loc.T("另一个盘上有同名同大小的拷贝,无法合并");
                ifDeleted = Loc.F("另一份在「{0}」。删掉这份,凡是用到这个位置的程序都会找不到它;如果没有程序在用,才可以删。",DirOf(original.Path));
            }
            else if (owner is null && IsInsideApp(m.Path, exeDirs))
            {
                safety = FoundSafety.Careful;
                headline = Loc.T("旁边有程序文件,可能是软件自带的");
                ifDeleted = Loc.T("如果它是某个软件自带的数据,删掉后那个软件可能打不开或功能缺失。先确认它是你自己下载的。");
            }
            else if (LooksProgramOwned(m.Path, o))
            {
                safety = FoundSafety.Careful;
                headline = Loc.T("放在某个软件自己的数据目录里");
                ifDeleted = Loc.T("这是某个软件放在自己目录里的文件。删掉后那个软件可能无法运行,或需要重新安装、修复。除非你确定不再用那个软件,否则别动。");
            }
            else
            {
                safety = FoundSafety.Careful;
                headline = others > 0 ? Loc.F("你下载的模型(另有 {0} 份同名拷贝)",others) : Loc.T("你下载的模型");
                ifDeleted = owner is not null
                    ? Loc.F("{0} 里选到它时会找不到。想用时需要重新下载(需要知道原来的下载地址)。",ownerName)
                    : Loc.T("之后想用它得重新下载,需要你还记得它从哪来的。");
            }

            if (running)
            {
                safety = FoundSafety.InUse;
                action = FoundAction.Recycle;
                headline = Loc.F("{0} 正在运行",ownerName);
                ifDeleted = Loc.T("先关掉它再处理,否则可能删到一半或让它出错。");
            }

            yield return new FoundItem(FoundKind.Model, m.Path, m.Length, m.LastWriteUtc, ownerName, format, safety, headline, ifDeleted, sameAs, action);
        }
    }

    private static bool IsInsideApp(string path, HashSet<string> exeDirs)
    {
        // Up to three folders above the file: program next to its own data.
        var dir = DirOf(path);
        for (var i = 0; i < 3 && dir.Length > 3; i++, dir = DirOf(dir))
            if (exeDirs.Contains(dir)) return true;
        return false;
    }

    /// <summary>In a program's own data (AppData, a game launcher's folder, a private dot-folder in the profile).</summary>
    private static bool LooksProgramOwned(string path, FinderOptions o)
    {
        var parts = path.Split('\\');
        if (parts.Skip(1).Any(p => AppPlaces.Contains(p))) return true;

        if (o.UserProfile is { Length: > 0 } profile && WindowsPath.Normalize(profile) is { } home && WindowsPath.IsStrictlyUnder(path, home))
        {
            var first = path[(home.Length + 1)..].Split('\\')[0];
            if (first.StartsWith('.') && first.Length > 1 && !OwnDotFolders.Contains(first)) return true;
        }
        return false;
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ python environments

    private static IEnumerable<FoundItem> BuildEnvs(HashSet<string> envRoots, HashSet<string> exeDirs, IFileSystem fs, FinderOptions o,
        List<string> exclude, CancellationToken ct)
    {
        var accepted = new List<string>();
        foreach (var root in envRoots.OrderBy(r => r.Length))
        {
            if (ct.IsCancellationRequested) yield break;
            if (accepted.Any(a => WindowsPath.IsStrictlyUnder(root, a))) continue; // an environment inside another one
            if (exclude.Any(x => WindowsPath.IsSameOrUnder(root, x))) continue;
            if (IsCondaInstall(root, fs)) continue; // the conda that all other environments run on
            if (Path.GetFileName(root).Equals("site-packages", StringComparison.OrdinalIgnoreCase)) continue;

            long total = 0;
            var newest = DateTime.MinValue;
            foreach (var f in fs.EnumerateFilesRecursive(root))
            {
                if (f.IsLink) continue;
                total += f.Length;
                if (f.LastWriteUtc > newest) newest = f.LastWriteUtc;
            }
            if (total < o.MinEnvBytes) continue;
            accepted.Add(root);

            var (owner, ownerRoot) = OwnerOf(root);
            var parent = DirOf(root);
            var manifest = Manifests.Select(n => Path.Combine(parent, n)).FirstOrDefault(p => fs.ReadText(p, 4_000_000) is not null)
                           ?? Manifests.Select(n => Path.Combine(root, n)).FirstOrDefault(p => fs.ReadText(p, 4_000_000) is not null);
            var isConda = fs.DirectoryExists(Path.Combine(root, "conda-meta"));
            var name = owner ?? Loc.T("未识别位置");
            var kind = isConda ? Loc.T("conda 环境") : Loc.T("Python 虚拟环境");

            // An environment is never "safe": what is installed in it cannot be proven from the outside.
            // Whether it can be rebuilt is a hint, shown honestly as one.
            string headline, ifDeleted;
            var safety = FoundSafety.Careful;

            if (LooksProgramOwned(root, o) || IsInsideApp(root, exeDirs, root))
            {
                headline = Loc.T("疑似软件自己的运行环境");
                ifDeleted = Loc.T("它位于某个软件的数据目录或程序目录里,多半是那个软件自己创建、自己使用的。删掉后那个软件可能无法运行,需要重新安装或修复。除非你确定不再用它,否则别动。");
            }
            else if (manifest is not null)
            {
                headline = Loc.F("项目里有 {0},大概率能重建",Path.GetFileName(manifest));
                ifDeleted = Loc.F("项目代码不受影响。要再用时,新建环境并按 {0} 重新安装,需要联网重新下载这些包。注意:清单里没写的本地包、自己编译的组件、特定 CUDA 版本不会自动恢复。",Path.GetFileName(manifest));
            }
            else
            {
                headline = Loc.T("没找到依赖清单,不好原样重建");
                ifDeleted = Loc.T("环境里装了哪些包只有这个环境自己知道。删掉后得凭记忆重装,版本不一样可能让程序出错。如果还要保留重建的可能,先在环境里执行 pip freeze > requirements.txt 保存一份。");
            }

            if (o.IsRunningUnder(root) || ownerRoot is not null && o.IsRunningUnder(ownerRoot))
            {
                safety = FoundSafety.InUse;
                headline = Loc.T("有程序正在用这个环境");
                ifDeleted = Loc.T("先关掉用它的程序(终端、编辑器、Jupyter 等)再处理。");
            }

            yield return new FoundItem(FoundKind.Environment, root, total, newest == DateTime.MinValue ? default : newest, name, kind, safety, headline, ifDeleted);
        }
    }

    // A program next to the environment (not the environment's own python.exe).
    private static bool IsInsideApp(string path, HashSet<string> exeDirs, string excludeUnder)
    {
        var dir = DirOf(path);
        for (var i = 0; i < 3 && dir.Length > 3; i++, dir = DirOf(dir))
            if (exeDirs.Contains(dir) && !WindowsPath.IsSameOrUnder(dir, excludeUnder)) return true;
        return false;
    }

    private static bool IsCondaInstall(string root, IFileSystem fs) =>
        fs.DirectoryExists(Path.Combine(root, "condabin")) || fs.DirectoryExists(Path.Combine(root, "envs"));

    // ------------------------------------------------------------------ helpers

    private static (string? Name, string? Root) OwnerOf(string path)
    {
        var parts = path.Split('\\');
        for (var i = 1; i < parts.Length; i++)
        {
            var seg = parts[i].ToLowerInvariant();
            foreach (var (key, name) in Owners)
            {
                var hit = key.Length < 6 ? seg == key : seg == key || seg.StartsWith(key, StringComparison.Ordinal);
                if (hit) return (name, string.Join('\\', parts.Take(i + 1)));
            }
        }
        return (null, null);
    }

    private static string DirOf(string path)
    {
        var cut = path.LastIndexOf('\\');
        return cut <= 0 ? path : path[..cut];
    }

    /// <summary>What the items marked <see cref="FoundSafety.Safe"/> would give back.</summary>
    public static long SafeBytes(IEnumerable<FoundItem> items) => items.Where(i => i.Safety == FoundSafety.Safe).Sum(i => i.Bytes);
}
