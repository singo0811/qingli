using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Mascot;
using TidyWin.Core.Optimize;
using TidyWin.Core.Safety;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>A handful of small fixes. Each one says what it does first and asks before doing it.</summary>
public sealed partial class OptimizePage : UserControl, IPageLifecycle, IMascotSource
{
    public OptimizeViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.OptimizeIntro());

    public OptimizePage()
    {
        ViewModel = new OptimizeViewModel(new WindowsOptimizer(), OperationGate.Shared);
        InitializeComponent();
        ViewModel.PropertyChanged += OnChanged;
    }

    public async void Activate() => await ViewModel.RefreshAsync();

    public void Deactivate() { }

    internal static string WhenText(string s) => Loc.T("适合:") + s;
    internal static string WarnText(string? s) => s is null ? "" : Loc.T("注意:") + s;
    internal static bool CanRun(OptimizeState s) => s != OptimizeState.Running;
    internal static string ActionText(OptimizeState s) => s switch
    {
        OptimizeState.Running => Loc.T("进行中…"),
        OptimizeState.Done => Loc.T("再来一次"),
        OptimizeState.Failed => Loc.T("重试"),
        _ => Loc.T("执行"),
    };

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OptimizeViewModel.Message)) return;
        MessageBar.Visibility = Palette.VisText(ViewModel.Message);
        MessageText.Text = ViewModel.Message ?? "";
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptimizeItemViewModel item }) return;

        var t = item.Task;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = $"{t.Title}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = t.Effect + (t.Warning is null ? "" : Loc.T("\n\n注意:") + t.Warning)
                       + (item.Hint is { Length: > 0 } h && t.Id == OptimizeCatalog.Recycle ? Loc.F("\n\n回收站{0}。",h) : ""),
            },
            PrimaryButtonText = Loc.T("执行"),
            CloseButtonText = Loc.T("再想想"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Mascot.Set(MascotLines.OptimizeRunning(t.Title));
        await ViewModel.RunAsync(item);
        Mascot.Set(item.State == OptimizeState.Done ? MascotLines.OptimizeDone(t.Title) : MascotLines.OptimizeFailed(t.Title));
    }
}
