using TidyWin.Core.Memory;
using TidyWin.Core.Status;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Mascot;

/// <summary>
/// What the mascot says about each page. Every line reports something that is really true about the current state;
/// she never makes up numbers, and sizes are passed in already formatted.
/// </summary>
public static class MascotLines
{
    // ───────── AI junk ─────────

    public static MascotState AiScanning() => new(Mood.Busy, Loc.T("我去翻翻哪些缓存能放心清……"));

    /// <param name="items">How many of the found items are already ticked (the safe, regenerable ones).</param>
    public static MascotState AiFound(string size, int items) => items > 0
        ? new(Mood.Happy, Loc.F("找到 {0} 可以清理,稳妥的 {1} 项我先勾好啦。想留的可以取消。",size,items))
        : new(Mood.Calm, Loc.F("找到 {0},但都要重新下载才能恢复,我没替你勾。你自己挑吧。",size));

    public static MascotState SystemScanning() => new(Mood.Busy, Loc.T("我去翻翻临时文件和各种缓存……"));

    public static MascotState AiNothing() => new(Mood.Satisfied, Loc.T("很干净,现在什么都不用清。"));

    public static MascotState AiCleaning() => new(Mood.Busy, Loc.T("正在清理,每个文件删之前我都会再检查一遍。"));

    public static MascotState AiDone(string size, bool preview) => preview
        ? new(Mood.Calm, Loc.F("预览完成:真清理的话能腾出 {0}。这次什么都没删。",size))
        : new(Mood.Satisfied, Loc.F("清好啦,腾出了 {0}!",size));

    // ───────── finding big AI files ─────────

    public static MascotState FinderIntro() => new(Mood.Calm, Loc.T("磁盘被 AI 撑满了?我帮你找出大文件,逐个说清楚能不能删。我什么都不会替你勾。"));

    public static MascotState FinderScanning() => new(Mood.Busy, Loc.T("我把各个盘翻一遍,只看不动……"));

    public static MascotState FinderFound(string size, string safe, int safeCount) => safeCount > 0
        ? new(Mood.Happy, Loc.F("找到 {0}。其中 {1} 我有把握:重复的合并(不删除,程序照常能用)、能自动重新生成的。其余的得你自己判断。",size,safe))
        : new(Mood.Calm, Loc.F("找到 {0},但都得你自己判断,我没有十足把握的。每一项我都写了删掉会怎样。",size));

    public static MascotState FinderNothing() => new(Mood.Satisfied, Loc.T("没有发现散落的大模型文件或重复的 Python 环境。"));

    public static MascotState FinderApplied(string summary) => new(Mood.Satisfied, summary);

    // ───────── where the space went ─────────

    public static MascotState DiskIntro() => new(Mood.Calm, Loc.T("想知道空间都去哪了?选一个盘,我只看不动,告诉你哪个文件夹最占地方。"));

    public static MascotState DiskScanning() => new(Mood.Busy, Loc.T("我在把这个盘一层层加起来……"));

    public static MascotState DiskLook(string where, string biggest, string size, int percent) =>
        new(Mood.Calm, Loc.F("「{0}」里最大的是「{1}」,{2},占 {3}%。点「打开」往里看。",where,biggest,size,percent));

    public static MascotState DiskEmpty(string where) => new(Mood.Calm, Loc.F("「{0}」里面没有什么占地方的东西。",where));

    public static MascotState DiskMoved(string size) =>
        new(Mood.Satisfied, Loc.F("移到回收站了({0})。想要回来就从回收站还原;清空回收站之后空间才会真正回来。",size));

    // ───────── small fixes ─────────

    public static MascotState OptimizeIntro() =>
        new(Mood.Calm, Loc.T("哪里卡了、哪里不对劲,就挑对应的一项。每一项我都会先说清楚会发生什么。"));

    public static MascotState OptimizeRunning(string title) => new(Mood.Busy, Loc.F("正在「{0}」……",title));

    public static MascotState OptimizeDone(string title) => new(Mood.Satisfied, Loc.F("「{0}」做完了。",title));

    public static MascotState OptimizeFailed(string title) =>
        new(Mood.Worried, Loc.F("「{0}」没做成,原因写在那一项下面。",title));

    // ───────── erasing one AI tool's traces ─────────

    public static MascotState EraseIntro() =>
        new(Mood.Calm, Loc.T("选一个 AI 工具,我把它留在这台电脑上的记录、登录和缓存都列出来。你勾了才删,而且删完还会告诉你服务器那边要自己做什么。"));

    public static MascotState EraseReady(string name, int count) =>
        new(Mood.Calm, Loc.F("「{0}」找到 {1} 处。对话和登录我替你勾了,你自己写的设置没勾。先点「预演」看看也行。",name,count));

    public static MascotState EraseRunning(string name) => new(Mood.Busy, Loc.F("正在清「{0}」的痕迹……",name));

    public static MascotState EraseBlocked(string name) =>
        new(Mood.Worried, Loc.F("「{0}」还开着。先把它完全关掉我才能清,不然会删到一半。",name));

    public static MascotState EraseDone(string name) =>
        new(Mood.Satisfied, Loc.F("「{0}」在这台电脑上的痕迹清掉了。服务器上的对话和账号要你自己去处理,下面写了怎么做。",name));

    // ───────── installed software ─────────

    public static MascotState SoftwareIntro(int count) =>
        new(Mood.Calm, Loc.F("这台电脑装了 {0} 个软件。卸载时我只会运行它自己的卸载程序,不会硬删它的文件。",count));

    public static MascotState SoftwareUninstalling(string name) =>
        new(Mood.Busy, Loc.F("「{0}」的卸载程序在运行,在它的窗口里点完就好,我在旁边等着。",name));

    public static MascotState SoftwareLeftovers(string name, int count, string size) =>
        new(Mood.Calm, Loc.F("「{0}」卸载好了。它还留下 {1} 个同名文件夹({2}),要不要清,你来勾,我一个都不会替你选。",name,count,size));

    public static MascotState SoftwareClean(string name) =>
        new(Mood.Satisfied, Loc.F("「{0}」卸得干干净净。",name));

    public static MascotState SoftwareCleaned(string size) =>
        new(Mood.Satisfied, Loc.F("残留清掉了,释放 {0}。",size));

    public static MascotState SoftwareUpdates(int count) =>
        count == 0
            ? new(Mood.Satisfied, Loc.T("能查到的软件都是最新的。"))
            : new(Mood.Calm, Loc.F("有 {0} 个软件可以更新。更新由系统自带的 winget 来做,不会动你的数据。",count));

    // ───────── start-up programs ─────────

    public static MascotState StartupReading() => new(Mood.Busy, Loc.T("我看看开机时都有谁一起跑起来……"));

    public static MascotState StartupSummary(int enabled, int suggested, int dead) =>
        dead > 0 && suggested > 0
            ? new(Mood.Happy, Loc.F("开机启动了 {0} 个。{1} 个关掉也不影响用,另有 {2} 个指向的程序已经不在了。关掉的随时能再打开。",enabled,suggested,dead))
            : dead > 0
                ? new(Mood.Happy, Loc.F("开机启动了 {0} 个,其中 {1} 个指向的程序已经不在了,可以移除。",enabled,dead))
                : suggested > 0
                    ? new(Mood.Happy, Loc.F("开机启动了 {0} 个,其中 {1} 个(聊天、更新、游戏平台之类)关掉也不影响用。关掉的随时能再打开。",enabled,suggested))
                    : new(Mood.Satisfied, Loc.F("开机启动了 {0} 个,我没看到不该开的。",enabled));

    public static MascotState Failed(string what) => new(Mood.Worried, Loc.F("{0}没能完成,稍后再试一次?",what));

    // ───────── models ─────────

    public static MascotState ModelsScanning() => new(Mood.Busy, Loc.T("我去看看你电脑上有哪些本地模型……"));

    public static MascotState ModelsNone() => new(Mood.Calm, Loc.T("没找到本地模型。装了 Ollama、LM Studio 或 Hugging Face 的话我会认出来。"));

    public static MascotState ModelsFound(string total, int count) =>
        new(Mood.Calm, Loc.F("一共 {0} 个模型,{1}。慢慢挑,没选的我一概不碰。",count,total));

    public static MascotState ModelsSelected(int count, string size) =>
        new(Mood.Worried, Loc.F("选了 {0} 个,删掉能腾出 {1}。删之前我会再问你一次。",count,size));

    public static MascotState ModelsDeleting() => new(Mood.Busy, Loc.T("正在删除你选的模型……"));

    public static MascotState ModelsDone(string size, bool preview) => preview
        ? new(Mood.Calm, Loc.F("预览完成:能腾出 {0}。这次什么都没删。",size))
        : new(Mood.Satisfied, Loc.F("删好啦,腾出了 {0}。以后要用得重新下载哦。",size));

    // ───────── memory ─────────

    public static MascotState MemoryTrimming() => new(Mood.Busy, Loc.T("正在让闲置的程序把用不到的内存还回来,不会关掉它们。"));

    public static MascotState Memory(MemoryPressure pressure, string free, int idleCount, bool leaking) =>
        leaking
            ? new(Mood.Worried, Loc.T("有个程序的内存一直在涨,像是泄漏了,往下看看是哪个。"))
            : pressure switch
            {
                MemoryPressure.Calm => new(Mood.Happy, Loc.F("内存宽裕,还有 {0} 可用,放心用。",free)),
                MemoryPressure.Moderate => new(Mood.Calm, Loc.F("内存用了一半多,还有 {0} 可用。",free)),
                MemoryPressure.High => idleCount > 0
                    ? new(Mood.Worried, Loc.F("内存有点紧,只剩 {0}。有 {1} 组后台程序闲着,可以收一收。",free,idleCount))
                    : new(Mood.Worried, Loc.F("内存有点紧,只剩 {0}。",free)),
                _ => new(Mood.Worried, Loc.F("内存快满了,只剩 {0}!先关掉不用的程序吧。",free)),
            };

    // ───────── security ─────────

    public static MascotState SecurityScanning() => new(Mood.Busy, Loc.T("正在看系统防护和网络有没有问题……"));

    public static MascotState AiNetChecking() => new(Mood.Busy, Loc.T("正在一个个敲 AI 网站的门,看直连和走代理各能不能进……"));

    public static MascotState AiNetResult(int problems) => problems == 0
        ? new(Mood.Satisfied, Loc.T("这些 AI 网站都连得上,网络这一关没问题。"))
        : new(Mood.Worried, Loc.F("有 {0} 处网络问题会影响 AI 工具,每一项下面写了原因和办法。",problems));

    public static MascotState Security(int risks, int warns) => (risks, warns) switch
    {
        ( > 0, _) => new(Mood.Worried, Loc.F("有 {0} 件事需要处理,我标在最上面了。",risks)),
        (0, > 0) => new(Mood.Calm, Loc.F("有 {0} 件事建议留意,不算急。",warns)),
        _ => new(Mood.Happy, Loc.T("没发现要处理的问题,我替你盯着呢。")),
    };

    // ───────── system health ─────────

    public static MascotState Health(HealthBand band) => band switch
    {
        HealthBand.Excellent => new(Mood.Happy, Loc.T("电脑状态很好,什么都不用做。")),
        HealthBand.Good => new(Mood.Calm, Loc.T("状态不错,有点小地方可以留意。")),
        HealthBand.Fair => new(Mood.Worried, Loc.T("有几项需要留意,看看下面的扣分原因。")),
        _ => new(Mood.Worried, Loc.T("状态不太好,建议先处理下面的问题。")),
    };
}
