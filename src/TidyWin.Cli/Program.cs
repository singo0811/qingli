using System.Text;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Platform;
using TidyWin.Core.Localization;

// tidywin --dry-run [system|ai|all] [--json]
// Reads and reports. It has no option that deletes anything; cleaning is done in the app, where each step is confirmed.

Console.OutputEncoding = Encoding.UTF8;

var rest = args.ToList();
var json = rest.Remove("--json");
var dry = rest.Remove("--dry-run");
var help = rest.Remove("--help") | rest.Remove("-h") | rest.Remove("/?");
var langArg = rest.FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal));
if (langArg is not null) rest.Remove(langArg);
Loc.Use(langArg?[7..] ?? Environment.GetEnvironmentVariable("TIDYWIN_LANG") ?? AppSettings.Load().Language, WindowsLanguage.TwoLetterCode());
var scope = rest.Count > 0 ? rest[0].ToLowerInvariant() : "all";

if (help || !dry || rest.Count > 1 || scope is not ("system" or "ai" or "all"))
{
    Console.WriteLine(Loc.T("用法: tidywin --dry-run [system|ai|all] [--json]\r\n\r\n  --dry-run   只预览会清理什么、能释放多少,不删除任何东西(必须写)\r\n  system      系统临时文件和各种缓存\r\n  ai          AI 工具留下的缓存\r\n  all         以上两者(默认)\r\n  --json      输出 JSON,方便脚本读取\r\n\r\n真正的清理请在 TidyWin 程序里进行,每一步都会先问你。"));
    return help ? 0 : 2;
}

IReadOnlyList<AiTarget> catalog = scope switch
{
    "system" => SystemCatalog.All,
    "ai" => AiCatalog.All,
    _ => [.. SystemCatalog.All, .. AiCatalog.All],
};

try
{
    var fs = new WindowsFileSystem();
    var resolved = AiCatalog.ExpandWildcards(AiCatalog.Resolve(new ProcessAiEnvironment(), catalog), fs);
    var scans = AiScanner.Scan(resolved, fs, DateTime.UtcNow);
    var report = DryRunReport.Build(scope, scans);
    Console.Write(json ? report.ToJson() + Environment.NewLine : report.ToText());
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(Loc.T("读取失败:") + e.Message);
    return 1;
}
