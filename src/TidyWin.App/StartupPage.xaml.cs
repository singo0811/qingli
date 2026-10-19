using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Mascot;
using TidyWin.Core.Safety;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>Which programs start with Windows, what each one is, and which can be switched off without losing anything.</summary>
public sealed partial class StartupPage : UserControl, IPageLifecycle, IMascotSource
{
    public StartupViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.StartupReading());

    private bool _loadedOnce;
    private bool _programmatic; // true while we put a switch back, so that is not taken for a click

    public StartupPage()
    {
        ViewModel = new StartupViewModel(new WindowsStartupSource(), gate: OperationGate.Shared, manifest: new ManifestStore());
        InitializeComponent();
        HeroNumber.Foreground = Palette.Horizontal(Colors.White, Palette.Mint);
        ViewModel.PropertyChanged += OnChanged;
        Render();
    }

    public async void Activate()
    {
        // Start-up entries change when software is installed, so look again each time the page is opened.
        if (!_loadedOnce) Mascot.Set(MascotLines.StartupReading());
        _loadedOnce = true;
        await ViewModel.RefreshAsync();
    }

    public void Deactivate() { }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StartupViewModel.EnabledCount) or nameof(StartupViewModel.SuggestedCount)
            or nameof(StartupViewModel.DeadCount) or nameof(StartupViewModel.Loaded) or nameof(StartupViewModel.Message)
            or nameof(StartupViewModel.Busy))
            Render();
    }

    private void Render()
    {
        HeroNumber.Text = ViewModel.EnabledCount.ToString();
        var n = ViewModel.SuggestedCount;
        BulkText.Text = n > 0 ? Loc.F("关掉 {0} 个不影响使用的",n) : Loc.T("没有需要关的");
        BulkButton.IsEnabled = n > 0 && !ViewModel.Busy;
        HeroSub.Text = !ViewModel.Loaded ? "" :
            ViewModel.DeadCount > 0 ? Loc.F("另有 {0} 个指向的程序已经不存在,或看起来可疑,可以移除",ViewModel.DeadCount)
            : Loc.T("关掉只是不再随开机自动运行,程序本身还在,想用时手动打开就行");
        MessageText.Text = ViewModel.Message ?? "";
        EmptyText.Visibility = Palette.Vis(ViewModel.Loaded && ViewModel.Items.Count == 0);

        if (ViewModel.Loaded)
            Mascot.Set(MascotLines.StartupSummary(ViewModel.EnabledCount, ViewModel.SuggestedCount, ViewModel.DeadCount));
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnDisableSuggested(object sender, RoutedEventArgs e)
    {
        await ViewModel.DisableSuggestedAsync();
        Render();
    }

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_programmatic || sender is not ToggleSwitch { DataContext: StartupItemViewModel item } toggle) return;
        if (toggle.IsOn == item.IsEnabled) return;

        var ok = await ViewModel.SetEnabledAsync(item, toggle.IsOn);
        if (!ok)
        {
            // Windows refused (usually: needs administrator rights). Put the switch back where it really is.
            _programmatic = true;
            toggle.IsOn = item.IsEnabled;
            _programmatic = false;
        }
        Render();
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StartupItemViewModel item }) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.F("移除「{0}」?",item.Name),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Loc.F("这会删掉这条开机启动记录,不会删除任何程序文件。\n\n{0}\n\n{1}",item.Note,item.Command),
            },
            PrimaryButtonText = Loc.T("移除"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ViewModel.RemoveAsync(item);
        Render();
    }
}
