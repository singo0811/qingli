using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWin.Core.Clean;
using TidyWin.Core.Mascot;
using TidyWin.Core.Memory;
using TidyWin.Platform;
using TidyWin.ViewModels;
using TidyWin.Core.Localization;

namespace TidyWin.App;

public sealed partial class ModelsPage : UserControl, IPageLifecycle, IMascotSource
{
    public ModelsViewModel ViewModel { get; }
    public MascotSignal Mascot { get; } = new(MascotLines.ModelsScanning());

    private readonly Tween _hero;
    private readonly Tween _cleaning;
    private readonly Tween _done;
    private bool _started;

    public ModelsPage()
    {
        ViewModel = new ModelsViewModel(
            new WindowsFileSystem(), new ProcessAiEnvironment(), new FileSystemReparseProbe(), new OllamaClient());
        InitializeComponent();

        var glow = Palette.Horizontal(Colors.White, Palette.Amber);
        HeroNumber.Foreground = glow;
        CleanNumber.Foreground = glow;
        DoneNumber.Foreground = glow;

        _hero = new Tween(DispatcherQueue, v => SetNumber(HeroNumber, HeroUnit, v));
        _cleaning = new Tween(DispatcherQueue, v => SetNumber(CleanNumber, CleanUnit, v));
        _done = new Tween(DispatcherQueue, v => SetNumber(DoneNumber, DoneUnit, v));

        ViewModel.PropertyChanged += OnChanged;
    }

    /// <summary>The page scans when it is first shown, not at app start, so opening the app stays quick.</summary>
    public async void Activate()
    {
        if (_started) return;
        _started = true;
        await ViewModel.ScanAsync();
    }

    public void Deactivate() { }

    private static void SetNumber(TextBlock n, TextBlock u, double bytes)
    {
        var (a, b) = ByteSize.Split((long)bytes);
        n.Text = a;
        u.Text = b;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ModelsViewModel.Phase): ApplyPhase(); break;
            case nameof(ModelsViewModel.SelectedBytes): _hero.To(ViewModel.SelectedBytes); RenderBar(); break;
            case nameof(ModelsViewModel.SelectedCount): RenderBar(); UpdateMascot(); break;
            case nameof(ModelsViewModel.FreedSoFar): _cleaning.To(ViewModel.FreedSoFar); break;
            case nameof(ModelsViewModel.Progress): CleanBar.Value = ViewModel.Progress; break;
            case nameof(ModelsViewModel.Notice): RenderBar(); break;
        }
    }

    private void ApplyPhase()
    {
        var p = ViewModel.Phase;
        ScanView.Visibility = Palette.Vis(p is CleanPhase.Idle or CleanPhase.Scanning);
        ReadyView.Visibility = Palette.Vis(p == CleanPhase.Ready);
        CleaningView.Visibility = Palette.Vis(p == CleanPhase.Cleaning);
        DoneView.Visibility = Palette.Vis(p == CleanPhase.Done);

        switch (p)
        {
            case CleanPhase.Idle when ViewModel.Error is { } err:
                ScanTitle.Text = Loc.T("查找没能完成:") + err;
                break;
            case CleanPhase.Ready:
                ShowReady();
                break;
            case CleanPhase.Cleaning:
                _cleaning.Jump(0);
                CleanBar.Value = 0;
                break;
            case CleanPhase.Done:
                ShowDone();
                break;
        }

        UpdateMascot();
    }

    private void UpdateMascot()
    {
        var vm = ViewModel;
        var whole = vm.Everything.Where(i => !i.Entry.Id.StartsWith("hf-old:", StringComparison.Ordinal)).ToList();
        Mascot.Set(vm.Phase switch
        {
            CleanPhase.Idle when vm.Error is not null => MascotLines.Failed(Loc.T("查找")),
            CleanPhase.Ready when !vm.HasAny => MascotLines.ModelsNone(),
            CleanPhase.Ready when vm.SelectedCount > 0 => MascotLines.ModelsSelected(vm.SelectedCount, ByteSize.Format(vm.SelectedBytes)),
            CleanPhase.Ready => MascotLines.ModelsFound(ByteSize.Format(vm.TotalBytes), whole.Count),
            CleanPhase.Cleaning => MascotLines.ModelsDeleting(),
            CleanPhase.Done when vm.Report is { } r => MascotLines.ModelsDone(ByteSize.Format(r.BytesFreed), r.DryRun),
            _ => MascotLines.ModelsScanning(),
        });
    }

    private void ShowReady()
    {
        _hero.Jump(0);
        _hero.To(ViewModel.SelectedBytes);

        EmptyBlock.Visibility = Palette.Vis(!ViewModel.HasAny);
        Section(OllamaSection, OllamaHeader, "OLLAMA", ViewModel.Ollama);
        Section(LmSection, LmHeader, "LM STUDIO", ViewModel.LmStudio);
        Section(HfSection, HfHeader, "HUGGING FACE", ViewModel.HuggingFace);

        HeroSub.Text = ViewModel.HasAny
            ? Loc.F("共 {0} 的本地模型。逐个选择要删除的,没选的一律不动。",ByteSize.Format(ViewModel.TotalBytes))
            : "";
        RenderBar();

        // Scroll back to the top once the new content has been laid out. Doing it before layout is silently ignored.
        ReadyView.UpdateLayout();
        ListScroll.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ListScroll.ChangeView(null, 0, null, disableAnimation: true));

        // Animate the whole list, never the children inside the ScrollViewer: those get clipped.
        Motion.Cascade(ListScroll);
    }

    private static void Section(StackPanel box, TextBlock header, string title, IReadOnlyCollection<ModelItemViewModel> items)
    {
        box.Visibility = Palette.Vis(items.Count > 0);
        var whole = items.Where(i => !i.Entry.Id.StartsWith("hf-old:", StringComparison.Ordinal)).ToList();
        header.Text = Loc.F("{0}  ·  {1} 个  ·  {2}",title,whole.Count,ByteSize.Format(whole.Sum(i => i.FreeBytes)));
    }

    private void RenderBar()
    {
        var n = ViewModel.SelectedCount;
        var preview = PreviewCheck.IsChecked == true;
        DeleteButtonText.Text = n == 0 ? Loc.T("先选择要删除的模型") : preview ? Loc.F("预览 {0} 项",n) : Loc.F("删除 {0} 项 · {1}",n,ByteSize.Format(ViewModel.SelectedBytes));
        DeleteButton.IsEnabled = n > 0;
        BarNote.Text = ViewModel.Notice ?? Loc.T("删除的文件不会进回收站。模型文件可以重新下载,但要联网。");
    }

    private void ShowDone()
    {
        var r = ViewModel.Report;
        if (r is null) return;

        DoneLabel.Text = r.DryRun ? Loc.T("预览 · 本可释放") : Loc.T("已释放");
        _done.Jump(0);
        _done.To(r.BytesFreed);
        DoneDetail.Text = r.DryRun
            ? Loc.F("将处理 {0} 个文件。这只是预览,没有删除任何东西。",r.Deleted)
            : Loc.F("删除了 {0} 个文件,清掉 {1} 个空文件夹",r.Deleted,r.DirectoriesRemoved);

        var notes = new List<string>();
        void Add(SkipReason reason, string text)
        {
            var c = r.Skipped.Count(s => s.Reason == reason);
            if (c > 0) notes.Add($"{c} {text}");
        }
        Add(SkipReason.InUse, Loc.T("个文件正被其他程序使用,已跳过。关掉对应程序后再试一次。"));
        Add(SkipReason.Guarded, Loc.T("个文件没通过安全检查,没有删除。"));
        Add(SkipReason.AccessDenied, Loc.T("个文件没有权限删除。"));
        Add(SkipReason.Failed, Loc.T("个文件删除失败。"));
        if (ViewModel.Notice is { Length: > 0 } n) notes.Add(n);
        DoneNote.Text = notes.Count == 0 ? Loc.T("没有任何文件被跳过。") : string.Join("\n", notes);
    }

    // ───────── hover ─────────

    private void OnCardEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0xFF, 0xB8, 0x4D));
    }

    private void OnCardExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Palette.Hex("1D2636"));
    }

    // ───────── actions ─────────

    private void OnClear(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();
    private void OnModeChanged(object sender, RoutedEventArgs e) => RenderBar();
    private async void OnRescan(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var preview = PreviewCheck.IsChecked == true;
        var chosen = ViewModel.Everything.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) return;

        if (!preview)
        {
            var lines = string.Join("\n", chosen.Take(12).Select(i => $"·  {i.Name}   {i.SizeText}"));
            if (chosen.Count > 12) lines += Loc.F("\n…以及另外 {0} 项",chosen.Count - 12);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Dark,
                Title = Loc.F("删除这 {0} 个模型?",chosen.Count),
                Content = lines + Loc.F("\n\n将释放 {0}。文件不会进回收站,以后要用只能重新下载。",ByteSize.Format(ViewModel.SelectedBytes)),
                PrimaryButtonText = Loc.T("删除"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await ViewModel.CleanAsync(preview);
    }
}
