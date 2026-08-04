using Microsoft.UI.Xaml;
using MLCCS.VideoSearch.Core.Privacy;

namespace MLCCS.VideoSearch.UI;

public partial class App : Application
{
    private Window? _window;
    public static MainWindow? CurrentWindow { get; private set; }
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) => WriteStartupFailure(eventArgs.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (Environment.GetCommandLineArgs().Any(value => value.Equals("--health-check", StringComparison.OrdinalIgnoreCase)))
            {
                // Successful App construction proves WinAppSDK and application resources loaded.
                Exit();
                return;
            }
            StartAgentIfAvailable();
            _window = CurrentWindow = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            throw;
        }
    }

    private static void StartAgentIfAvailable()
    {
        var agent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent", "MLCCS.VideoSearch.Agent.exe"));
        if (!File.Exists(agent) || System.Diagnostics.Process.GetProcessesByName("MLCCS.VideoSearch.Agent").Length != 0) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(agent) { UseShellExecute = true });
    }

    private static void WriteStartupFailure(Exception exception)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLCCS", "VideoSearch");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ui-startup-crash.log"),
                DiagnosticRedactor.Redact(exception.ToString(), Environment.UserName));
        }
        catch { }
    }
}
