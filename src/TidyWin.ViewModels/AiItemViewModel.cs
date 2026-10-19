using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public sealed partial class AiItemViewModel : ObservableObject
{
    public AiScanItem Scan { get; }

    public AiItemViewModel(AiScanItem scan)
    {
        Scan = scan;
        _isSelected = CanSelect && scan.Target.SelectedByDefault;
    }

    public event EventHandler? SelectionChanged;

    public string Tool => Scan.Target.Tool;
    public string Title => Scan.Target.Title;
    public string Consequence => Scan.Target.Consequence;
    public string Path => Scan.Path;
    public AiSafety Safety => Scan.Target.Safety;

    /// <summary>What a clean would actually free: respects the minimum age.</summary>
    public long Bytes => Scan.EligibleBytes;
    public string BytesText => ByteSize.Format(Bytes);

    /// <summary>Only the two "whole folder" tiers can be ticked. Per-item model removal is a separate flow.</summary>
    public bool CanSelect =>
        Scan.Exists
        && Bytes > 0
        && Safety is AiSafety.Regenerable or AiSafety.ReDownloadable
        && !WindowsPath.IsRoot(Scan.Path);

    public string TierLabel => Safety switch
    {
        AiSafety.Regenerable => Loc.T("自动重建"),
        AiSafety.ReDownloadable => Loc.T("需重新下载"),
        AiSafety.UserChoice => Loc.T("逐个确认"),
        _ => Loc.T("不会清理"),
    };

    public string Hint => Safety switch
    {
        AiSafety.UserChoice => Loc.T("这是你主动装的模型,请到「模型」页一个一个选"),
        _ => Consequence,
    };

    /// <summary>A hint from how long ago anything in this place last changed: it tells whether someone is using it.</summary>
    public string AgeNote => AgeNoteFor(Scan.NewestWriteUtc, DateTime.UtcNow);

    public static string AgeNoteFor(DateTime? newestUtc, DateTime nowUtc)
    {
        if (newestUtc is not { } t) return "";
        var days = (nowUtc - t).TotalDays;
        return days switch
        {
            < 1 => Loc.T("刚刚还有文件在写入,可能有程序正在用它"),
            < 30 => Loc.F("{0} 天前还在用",Math.Max(1, (int)days)),
            >= 90 => Loc.F("已 {0} 个月没有变动,放心清",(int)(days / 30)),
            _ => "",
        };
    }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            IsSelected = false;
            return;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
