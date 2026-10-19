using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Mascot;
using TidyWin.Core.Safety;
using TidyWin.Core.Software;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>
/// Installed programs. Uninstalling only ever starts the program's own uninstaller; what it leaves behind is listed,
/// never pre-ticked, and removing it is a separate, explicit, permanent step. Updates are done by winget.
/// </summary>
public sealed partial class SoftwarePage : UserControl, IPageLifecycle, IMascotSource
{
    public SoftwareViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.SoftwareIntro(0));

    private bool _showUpdates;
    private bool _loaded;

    public SoftwarePage()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        ViewModel = new SoftwareViewModel(
            new WindowsSoftwareSource(), new ProcessUninstaller(), new WingetClient(), new WindowsFileSystem(),
            new FileSystemReparseProbe(), roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            File.Exists, new ManifestStore(), OperationGate.Shared);

        InitializeComponent();
        ViewModel.PropertyChanged += OnChanged;
        ApplyPhase();
        ApplyTab();
    }

    public async void Activate()
    {
        if (_loaded || ViewModel.Phase != SoftwarePhase.Idle) return;
        _loaded = true;
        await ViewModel.LoadAsync();
    }

    public void Deactivate() { }

    internal static string AppSub(string publisher, string version, string date) =>
        string.Join("  ·  ", new[] { publisher, version.Length > 0 ? Loc.T("版本 ") + version : "", date.Length > 0 ? Loc.T("装于 ") + date : "" }
            .Where(s => s.Length > 0));

    // ───────── what is on screen ─────────

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SoftwareViewModel.Phase): ApplyPhase(); break;
            case nameof(SoftwareViewModel.Message):
            case nameof(SoftwareViewModel.Total): ShowMessage(); break;
            case nameof(SoftwareViewModel.UpdatesPhase):
            case nameof(SoftwareViewModel.UpgradeCount): ApplyUpdates(); break;
        }
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        var listing = p is SoftwarePhase.Idle or SoftwarePhase.Loading or SoftwarePhase.Ready;
        ListPanel.Visibility = Palette.Vis(listing);
        BusyView.Visibility = Palette.Vis(p is SoftwarePhase.Uninstalling or SoftwarePhase.Checking or SoftwarePhase.Cleaning);
        ResidualView.Visibility = Palette.Vis(p == SoftwarePhase.Residuals);
        DoneView.Visibility = Palette.Vis(p == SoftwarePhase.Done);
        LoadingRing.Visibility = Palette.Vis(p == SoftwarePhase.Loading);

        var subject = ViewModel.Subject ?? "";
        switch (p)
        {
            case SoftwarePhase.Idle:
            case SoftwarePhase.Loading:
            case SoftwarePhase.Ready:
                SubHeading.Text = ViewModel.Total > 0 ? Loc.F("共 {0} 个。卸载用的是软件自带的卸载程序,不会硬删它的文件。",ViewModel.Total) : "";
                ShowMessage();
                if (p == SoftwarePhase.Ready) Mascot.Set(_showUpdates ? MascotLines.SoftwareUpdates(ViewModel.UpgradeCount) : MascotLines.SoftwareIntro(ViewModel.Total));
                break;

            case SoftwarePhase.Uninstalling:
                BusyTitle.Text = Loc.F("正在卸载「{0}」",subject);
                BusyDetail.Text = Loc.T("请在它弹出的卸载窗口里完成操作。窗口可能被压在后面,看一下任务栏。");
                Mascot.Set(MascotLines.SoftwareUninstalling(subject));
                break;

            case SoftwarePhase.Checking:
                BusyTitle.Text = Loc.F("正在确认「{0}」是否已卸载",subject);
                BusyDetail.Text = Loc.T("最多等一分半钟。如果你在卸载窗口里点了取消,我会发现并且什么都不改。");
                break;

            case SoftwarePhase.Cleaning:
                BusyTitle.Text = Loc.T("正在清理残留");
                BusyDetail.Text = "";
                break;

            case SoftwarePhase.Residuals:
                var bytes = ViewModel.Residuals.Sum(r => r.Residual.Bytes);
                ResidualTitle.Text = Loc.F("「{0}」留下了 {1} 个同名文件夹",subject,ViewModel.Residuals.Count);
                CleanButton.IsEnabled = false;
                Mascot.Set(MascotLines.SoftwareLeftovers(subject, ViewModel.Residuals.Count, ByteSize.Format(bytes)));
                break;

            case SoftwarePhase.Done:
                ShowDone();
                break;
        }
    }

    private void ShowDone()
    {
        var subject = ViewModel.Subject ?? "";
        DoneText.Text = ViewModel.Message ?? Loc.T("完成。");
        var skipped = ViewModel.Report?.SkippedCount ?? 0;
        DoneDetail.Text = skipped > 0 ? Loc.F("有 {0} 个文件没能删掉(多半是还在被使用),留在原处没有动。",skipped) : "";
        Mascot.Set(ViewModel.Report is { DryRun: false } r
            ? MascotLines.SoftwareCleaned(ByteSize.Format(r.BytesFreed))
            : MascotLines.SoftwareClean(subject));
    }

    private void ShowMessage()
    {
        var m = ViewModel.Message;
        var listing = ViewModel.Phase is SoftwarePhase.Idle or SoftwarePhase.Loading or SoftwarePhase.Ready;
        MessageBar.Visibility = Palette.Vis(listing && !string.IsNullOrEmpty(m));
        MessageText.Text = m ?? "";
        if (listing && ViewModel.Total > 0) SubHeading.Text = Loc.F("共 {0} 个。卸载用的是软件自带的卸载程序,不会硬删它的文件。",ViewModel.Total);
    }

    private void ApplyTab()
    {
        InstalledPanel.Visibility = Palette.Vis(!_showUpdates);
        UpdatesPanel.Visibility = Palette.Vis(_showUpdates);
        InstalledTabButton.Style = _showUpdates ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        UpdatesTabButton.Style = _showUpdates ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        Heading.Text = _showUpdates ? Loc.T("有哪些可以更新") : Loc.T("装了哪些软件");
        SubHeading.Visibility = Palette.Vis(!_showUpdates);
        if (_showUpdates) ApplyUpdates();
    }

    private void ApplyUpdates()
    {
        UpdatesRing.Visibility = Palette.Vis(ViewModel.UpdatesPhase == UpdatesPhase.Loading);
        CheckUpdatesButton.IsEnabled = ViewModel.UpdatesPhase != UpdatesPhase.Loading;
        CheckUpdatesButton.Content = ViewModel.UpdatesPhase is UpdatesPhase.NotAsked ? Loc.T("检查更新") : Loc.T("重新检查");
        UpdatesNote.Text = ViewModel.UpdatesPhase switch
        {
            UpdatesPhase.NotAsked => Loc.T("用系统自带的 winget 查一遍,只列出来,你点了才会更新。"),
            UpdatesPhase.Loading => Loc.T("正在查询……可能要十几秒。"),
            UpdatesPhase.Unavailable => Loc.T("这台电脑上没有 winget(应用安装程序),没法检查更新。可以在微软商店里装「应用安装程序」。"),
            UpdatesPhase.Failed => Loc.T("没能拿到更新列表(可能没联网)。稍后再试。"),
            _ => ViewModel.Upgrades.Count == 0 ? Loc.T("没有发现可更新的软件。") : Loc.F("{0} 个可以更新。有些更新会弹出系统授权窗口。",ViewModel.UpgradeCount),
        };
        if (_showUpdates && ViewModel.UpdatesPhase == UpdatesPhase.Ready) Mascot.Set(MascotLines.SoftwareUpdates(ViewModel.UpgradeCount));
    }

    // ───────── actions ─────────

    private void OnShowInstalled(object sender, RoutedEventArgs e)
    {
        _showUpdates = false;
        ApplyTab();
        Mascot.Set(MascotLines.SoftwareIntro(ViewModel.Total));
    }

    private void OnShowUpdates(object sender, RoutedEventArgs e)
    {
        _showUpdates = true;
        ApplyTab();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Sort = SortBox.SelectedIndex switch { 1 => SoftwareSort.Name, 2 => SoftwareSort.Date, _ => SoftwareSort.Size };
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await ViewModel.LoadUpdatesAsync();

    private async void OnUpgrade(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UpgradeItemViewModel item }) await ViewModel.UpgradeAsync(item);
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppItemViewModel item }) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("卸载「{0}」?",item.Name),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Loc.T("我会运行这个软件自带的卸载程序,接下来它会弹出自己的窗口,你在里面确认。")
                       + Loc.T("卸载完成后,我会列出它可能留下的同名文件夹,由你决定要不要清,不会替你删。"),
            },
            PrimaryButtonText = Loc.T("开始卸载"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.UninstallAsync(item);
    }

    private void OnTick(object sender, RoutedEventArgs e) =>
        CleanButton.IsEnabled = ViewModel.Residuals.Any(r => r.IsSelected);

    private void OnRevealResidual(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResidualItemViewModel r })
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r.Path}\"") { UseShellExecute = true });
    }

    private async void OnCleanResiduals(object sender, RoutedEventArgs e)
    {
        var chosen = ViewModel.Residuals.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("永久删除这 {0} 个文件夹?",chosen.Count),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = string.Join("\n", chosen.Select(c => $"{c.Path}  ({c.SizeText})"))
                       + Loc.T("\n\n这是永久删除,不会进回收站,删了就找不回来。里面如果有你要留的设置或存档,请先取消。"),
            },
            PrimaryButtonText = Loc.T("永久删除"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.CleanResidualsAsync();
    }

    private void OnSkipResiduals(object sender, RoutedEventArgs e) => ViewModel.SkipResiduals();

    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Dismiss();
}
