using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.AiJunk;

public static class AiCatalog
{
    private static readonly AiOverride[] None = [];

    public static IReadOnlyList<AiTarget> All { get; } =
    [
        // ---- Rebuilt automatically ----
        new("pip.cache", "pip", Loc.T("pip 下载缓存"), AiCategory.PackageCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"pip\Cache", [new("PIP_CACHE_DIR")],
            Loc.T("下次安装包会重新下载,首次略慢")),
        new("uv.cache", "uv", Loc.T("uv 包缓存"), AiCategory.PackageCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"uv\cache", [new("UV_CACHE_DIR")],
            Loc.T("下次安装包会重新下载,首次略慢")),
        new("nvidia.dx", "NVIDIA", Loc.T("NVIDIA DirectX 着色器缓存"), AiCategory.ShaderAndKernelCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"NVIDIA\DXCache", None,
            Loc.T("游戏和应用首次启动会重新编译着色器,可能卡一下")),
        new("nvidia.gl", "NVIDIA", Loc.T("NVIDIA OpenGL 着色器缓存"), AiCategory.ShaderAndKernelCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"NVIDIA\GLCache", None,
            Loc.T("首次运行会重新编译着色器")),
        new("nvidia.compute", "NVIDIA", Loc.T("CUDA 计算内核缓存"), AiCategory.ShaderAndKernelCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"NVIDIA\ComputeCache", [new("CUDA_CACHE_PATH")],
            Loc.T("CUDA 程序首次运行会重新编译内核,可能慢几十秒")),
        new("triton.cache", "Triton", Loc.T("Triton 内核缓存"), AiCategory.ShaderAndKernelCache, AiSafety.Regenerable,
            KnownFolder.UserProfile, @".triton\cache", [new("TRITON_CACHE_DIR")],
            Loc.T("首次运行会重新编译内核")),
        new("torch.inductor", "PyTorch", Loc.T("PyTorch Inductor 编译临时文件"), AiCategory.RuntimeTemp, AiSafety.Regenerable,
            KnownFolder.Temp, "torchinductor_*", None,
            Loc.T("下次 torch.compile 会重新编译"), MinAgeDays: 2),
        new("jupyter.runtime", "Jupyter", Loc.T("Jupyter 运行时残留"), AiCategory.RuntimeTemp, AiSafety.Regenerable,
            KnownFolder.AppData, @"jupyter\runtime", [new("JUPYTER_RUNTIME_DIR")],
            Loc.T("只清理已停止内核留下的连接文件"), MinAgeDays: 2),
        new("cursor.cache", "Cursor", Loc.T("Cursor 编辑器缓存"), AiCategory.EditorCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"Cursor\Cache", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
        new("cursor.cacheddata", "Cursor", Loc.T("Cursor 编译脚本缓存"), AiCategory.EditorCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"Cursor\CachedData", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
        new("cursor.gpu", "Cursor", Loc.T("Cursor GPU 缓存"), AiCategory.EditorCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"Cursor\GPUCache", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
        new("cursor.logs", "Cursor", Loc.T("Cursor 日志"), AiCategory.EditorCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"Cursor\logs", None, Loc.T("旧日志不再可查"), MinAgeDays: 14),

        // ---- Comes back by downloading ----
        new("hf.hub", "Hugging Face", Loc.T("Hugging Face 模型缓存"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cache\huggingface\hub",
            [new("HF_HUB_CACHE"), new("HF_HOME", "hub")],
            Loc.T("用到时需要重新下载,模型可能有几个到几十 GB")),
        new("hf.datasets", "Hugging Face", Loc.T("Hugging Face 数据集缓存"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cache\huggingface\datasets",
            [new("HF_DATASETS_CACHE"), new("HF_HOME", "datasets")],
            Loc.T("用到时需要重新下载或重新处理")),
        new("torch.hub", "PyTorch", Loc.T("PyTorch Hub 预训练权重"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cache\torch", [new("TORCH_HOME")],
            Loc.T("用到时需要重新下载权重")),
        new("modelscope", "ModelScope", Loc.T("ModelScope 模型缓存"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cache\modelscope", [new("MODELSCOPE_CACHE")],
            Loc.T("用到时需要重新下载")),
        new("whisper", "Whisper", Loc.T("Whisper 语音模型"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cache\whisper", None,
            Loc.T("用到时需要重新下载")),
        new("keras", "Keras", Loc.T("Keras 预训练模型"), AiCategory.ModelCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".keras\models", [new("KERAS_HOME", "models")],
            Loc.T("用到时需要重新下载")),
        new("conda.pkgs", "Conda", Loc.T("Conda 包缓存"), AiCategory.PackageCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".conda\pkgs", [new("CONDA_PKGS_DIRS")],
            Loc.T("已有环境不受影响,新建环境时需要重新下载")),

        // ---- Only item by item ----
        new("ollama.models", "Ollama", Loc.T("Ollama 已安装模型"), AiCategory.ModelCache, AiSafety.UserChoice,
            KnownFolder.UserProfile, @".ollama\models", [new("OLLAMA_MODELS")],
            Loc.T("是你主动拉取的模型,只能逐个选择删除")),
        new("lmstudio.models", "LM Studio", Loc.T("LM Studio 已下载模型"), AiCategory.ModelCache, AiSafety.UserChoice,
            KnownFolder.UserProfile, @".lmstudio\models", None,
            Loc.T("是你主动下载的模型,只能逐个选择删除")),

        // ---- Never touched ----
        new("cursor.workspace", "Cursor", Loc.T("Cursor 工作区数据与对话记录"), AiCategory.UserData, AiSafety.Never,
            KnownFolder.AppData, @"Cursor\User\workspaceStorage", None,
            Loc.T("包含各项目的对话与状态,不会清理")),
        new("cursor.settings", "Cursor", Loc.T("Cursor 设置与快捷键"), AiCategory.UserData, AiSafety.Never,
            KnownFolder.AppData, @"Cursor\User", None,
            Loc.T("你的个人配置,不会清理")),
        new("hf.token", "Hugging Face", Loc.T("Hugging Face 登录令牌"), AiCategory.UserData, AiSafety.Never,
            KnownFolder.UserProfile, @".cache\huggingface\token", None,
            Loc.T("登录凭据,不会清理")),
    ];

    public static IReadOnlyList<ResolvedAiTarget> Resolve(IAiEnvironment env, IEnumerable<AiTarget>? targets = null)
    {
        var result = new List<ResolvedAiTarget>();
        foreach (var t in targets ?? All)
        {
            var path = ResolveOne(t, env);
            if (path is not null) result.Add(new ResolvedAiTarget(t, path));
        }
        return result;
    }

    private static string? ResolveOne(AiTarget t, IAiEnvironment env)
    {
        foreach (var o in t.Overrides)
        {
            var v = env.Variable(o.Variable);
            if (string.IsNullOrWhiteSpace(v)) continue;
            // Some variables hold several paths (e.g. CONDA_PKGS_DIRS). Use the first one only.
            var first = v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(first)) continue;
            var combined = o.Suffix is null ? first : first.TrimEnd('\\', '/') + "\\" + o.Suffix;
            var normalized = WindowsPath.Normalize(combined);
            if (normalized is not null) return normalized;
        }

        var baseDir = env.Folder(t.Anchor);
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        return WindowsPath.Normalize(baseDir.TrimEnd('\\', '/') + "\\" + t.RelativePath);
    }

    /// <summary>
    /// Turns "...\torchinductor_*" into one entry per existing matching directory. Targets without a
    /// wildcard pass through unchanged. The wildcard is matched against directory names only.
    /// </summary>
    public static IReadOnlyList<ResolvedAiTarget> ExpandWildcards(IEnumerable<ResolvedAiTarget> resolved, Clean.IFileSystem fs)
    {
        var result = new List<ResolvedAiTarget>();
        foreach (var r in resolved)
        {
            if (!r.Path.Contains('*')) { result.Add(r); continue; }

            var cut = r.Path.LastIndexOf('\\');
            if (cut <= 0) continue;
            var parent = r.Path[..cut];
            var pattern = r.Path[(cut + 1)..];
            if (parent.Contains('*') || !fs.DirectoryExists(parent)) continue;

            foreach (var dir in fs.EnumerateDirectories(parent))
            {
                var name = dir[(dir.LastIndexOf('\\') + 1)..];
                if (GlobMatch(name, pattern)) result.Add(r with { Path = dir });
            }
        }
        return result;
    }

    public static bool GlobMatch(string name, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Roots that may be handed to PathGuard. Never-class targets are excluded, and so is any target
    /// whose path is a drive or share root (which a user-set variable could cause), and any path that
    /// still contains an unexpanded wildcard.
    /// </summary>
    public static IReadOnlyList<string> AllowedRoots(IEnumerable<ResolvedAiTarget> resolved) =>
        resolved
            .Where(r => r.Target.CanBeDeleted && !r.Path.Contains('*'))
            .Select(r => r.Path)
            .Where(p => !WindowsPath.IsRoot(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Paths that must stay untouched even when they sit inside an allowed root.</summary>
    public static IReadOnlyList<string> ProtectedPaths(IEnumerable<ResolvedAiTarget> resolved) =>
        resolved
            .Where(r => r.Target.Safety == AiSafety.Never && !r.Path.Contains('*'))
            .Select(r => r.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
