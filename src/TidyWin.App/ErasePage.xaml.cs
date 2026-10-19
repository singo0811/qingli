using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Erase;
using TidyWin.Core.Mascot;
using TidyWin.Core.Safety;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>
/// Clears one AI tool's traces from this computer. Everything is listed first with its size, conversations, logins and caches are
/// ticked and the person's own settings are not, a running tool blocks the whole thing, and the delete itself is a permanent,
/// confirmed step. What lives on the servers is never claimed to be deleted: the page says what to do about it.
/// </summary>
public sealed partial class ErasePage : UserControl, IPageLifecycle, IMascotSource
{
    public EraseViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.EraseIntro());

    private bool _loaded;

    public ErasePage()
    {
        ViewModel = new EraseViewModel(
            new WindowsEraseSystem(), new ProcessAiEnvironment(), new WindowsFileSystem(), new FileSystemReparseProbe(),
            new ManifestStore(), OperationGate.Shared);

        InitializeComponent();
        ViewModel.PropertyChanged += OnChanged;
        ApplyPhase();
    }

    public async void Activate()
    {
        // Reading how much each tool has left walks their folders, so it is done when the page is first shown, and again on return.
        if (ViewModel.Phase == ErasePhase.Idle && !_loaded)
        {
            _loaded = true;
            await ViewModel.LoadCardsAsync();
        }
    }

    public void Deactivate() { }

    internal static Visibility RevealVisibility(EraseItemType type) =>
        type == EraseItemType.Files ? Visibility.Visible : Visibility.Collapsed;

    // ───────── what is on screen ─────────

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EraseViewModel.Phase): ApplyPhase(); break;
            case nameof(EraseViewModel.Message): ShowMessage(); break;
            case nameof(EraseViewModel.Running): ApplyRunning(); break;
            case nameof(EraseViewModel.CanErase):
            case nameof(EraseViewModel.SelectedText): ApplyButtons(); break;
            case nameof(EraseViewModel.NothingFound): NothingText.Visibility = Palette.Vis(ViewModel.NothingFound); break;
        }
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        var name = ViewModel.Tool?.Name ?? "";
        PickView.Visibility = Palette.Vis(p == ErasePhase.Idle);
        DetailView.Visibility = Palette.Vis(p == ErasePhase.Ready);
        BusyView.Visibility = Palette.Vis(p is ErasePhase.Scanning or ErasePhase.Erasing);
        DoneView.Visibility = Palette.Vis(p == ErasePhase.Done);

        switch (p)
        {
            case ErasePhase.Idle:
                Mascot.Set(MascotLines.EraseIntro());
                break;

            case ErasePhase.Scanning:
                BusyTitle.Text = Loc.F("正在看「{0}」留下了什么",name);
                break;

            case ErasePhase.Ready:
                DetailTitle.Text = name;
                BeforeText.Text = Bullets(ViewModel.BeforeSteps);
                AfterText.Text = Bullets(ViewModel.AfterSteps);
                NothingText.Visibility = Palette.Vis(ViewModel.NothingFound);
                ApplyRunning();
                ShowMessage();
                ApplyButtons();
                break;

            case ErasePhase.Erasing:
                BusyTitle.Text = Loc.F("正在清「{0}」的痕迹",name);
                Mascot.Set(MascotLines.EraseRunning(name));
                break;

            case ErasePhase.Done:
                DoneTitle.Text = ViewModel.ResultTitle;
                DoneDetail.Text = ViewModel.ResultDetail;
                DoneDetail.Visibility = Palette.Vis(ViewModel.ResultDetail.Length > 0);
                DoneAfter.Text = Bullets(ViewModel.AfterSteps);
                Mascot.Set(MascotLines.EraseDone(name));
                break;
        }
    }

    private static string Bullets(IReadOnlyList<string> lines) => string.Join("\n", lines.Select((s, i) => $"{i + 1}. {s}"));

    private void ApplyRunning()
    {
        var name = ViewModel.Tool?.Name ?? "";
        RunningBar.Visibility = Palette.Vis(ViewModel.Running);
        RunningText.Text = Loc.F("检测到「{0}」正在运行。要先把它完全关掉(托盘里的也要退出)才能清除;现在可以先看列表、做预演。",name);
        if (ViewModel.Phase == ErasePhase.Ready)
        {
            Mascot.Set(ViewModel.Running
                ? MascotLines.EraseBlocked(name)
                : MascotLines.EraseReady(name, ViewModel.Groups.Sum(g => g.Items.Count)));
        }
    }

    private void ShowMessage()
    {
        MessageBar.Visibility = Palette.Vis(!string.IsNullOrEmpty(ViewModel.Message));
        MessageText.Text = ViewModel.Message ?? "";
    }

    private void ApplyButtons()
    {
        EraseButton.IsEnabled = ViewModel.CanErase;
        DryButton.IsEnabled = ViewModel.Phase == ErasePhase.Ready && ViewModel.SelectedCount > 0;
        SelectedLine.Text = ViewModel.SelectedText;
    }

    // ───────── actions ─────────

    private async void OnPickTool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EraseToolCardViewModel card }) await ViewModel.ChooseAsync(card.Tool.Id);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        ViewModel.Back();
        // The picture of what each tool has left is out of date after a clean-up, so it is read again.
        _ = ViewModel.LoadCardsAsync();
    }

    private void OnToggleAll(object sender, RoutedEventArgs e) =>
        ViewModel.SetAll(ViewModel.SelectedCount < ViewModel.Groups.SelectMany(g => g.Items).Count(i => i.CanTick));

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EraseItemViewModel { Scan: { } scan } }) return;

        // A file is shown selected inside its folder; a folder is opened.
        var args = scan.Location.IsFile ? $"/select,\"{scan.Path}\"" : $"\"{scan.Path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private async void OnDryRun(object sender, RoutedEventArgs e) => await ViewModel.EraseAsync(dryRun: true);

    private async void OnErase(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.Groups.SelectMany(g => g.Items).Where(i => i.IsSelected && i.CanTick).ToList();
        if (picked.Count == 0 || ViewModel.Tool is not { } tool) return;

        var files = picked.Count(i => i.ItemType == EraseItemType.Files);
        var keys = picked.Count(i => i.ItemType == EraseItemType.Variable);
        var creds = picked.Count(i => i.ItemType == EraseItemType.Credential);
        var parts = new List<string>();
        if (files > 0) parts.Add(Loc.F("{0} 处文件和文件夹({1})",files,ByteSize.Format(ViewModel.SelectedBytes)));
        if (keys > 0) parts.Add(Loc.F("{0} 个环境变量里的密钥",keys));
        if (creds > 0) parts.Add(Loc.F("{0} 条 Windows 凭据",creds));

        var includesSettings = picked.Any(i => i.Scan?.Location.Kind == EraseKind.Settings);
        var text = Loc.T("将永久删除:\n· ") + string.Join("\n· ", parts)
                   + Loc.T("\n\n不会进回收站,删了就找不回来:对话记录没了,登录状态没了,环境变量里的密钥也没了(要去服务商那里重新生成)。")
                   + (includesSettings ? Loc.T("\n\n你勾了「设置与你自己写的内容」,里面你自己写的命令、规则和配置也会一起没有。") : "")
                   + Loc.T("\n\n已经在应用里退出登录了吗?没退出的话,服务器上的登录令牌还是有效的。");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("彻底清除「{0}」的痕迹?",tool.Name),
            Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text } },
            PrimaryButtonText = Loc.T("永久删除"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.EraseAsync();
    }
}
