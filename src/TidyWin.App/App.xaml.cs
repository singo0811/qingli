using Microsoft.UI.Xaml;

namespace TidyWin.App;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _single;
    private static EventWaitHandle? _show;

    public App()
    {
        // The language comes first: some screens build their text as they are created.
        // TIDYWIN_LANG=en|zh overrides the saved choice for one run (screenshots and tests).
        TidyWin.Core.Localization.Loc.Use(Environment.GetEnvironmentVariable("TIDYWIN_LANG") is "en" or "zh"
            ? Environment.GetEnvironmentVariable("TIDYWIN_LANG")
            : TidyWin.Platform.AppSettings.Load().Language,
            TidyWin.Platform.WindowsLanguage.TwoLetterCode());
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write("Task", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!TakeSingleInstance())
        {
            Exit();
            return;
        }
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception e)
        {
            CrashLog.Write("Launch", e);
            throw;
        }
    }

    /// <summary>A second launch only asks the one already running to show itself.</summary>
    private static bool TakeSingleInstance()
    {
        _single = new Mutex(false, @"Local\TidyWin.Single");
        try
        {
            if (!_single.WaitOne(0))
            {
                try { EventWaitHandle.OpenExisting(@"Local\TidyWin.Show").Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous run ended without releasing it. This run holds it now.
        }
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TidyWin.Show");
        return true;
    }
}

/// <summary>
/// Keeps the last problems in a small text file on this computer, so a crash can be explained afterwards.
/// Nothing is sent anywhere. The file is capped so it can never grow large.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 64 * 1024;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "crash.log");

    public static void Write(string where, Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{e}\n\n");
        }
        catch
        {
            // Logging must never be the thing that crashes.
        }
    }
}
