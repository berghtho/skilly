using System.Reflection;
using System.Windows;
using System.Threading;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;

namespace Skilly;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private SingleInstanceGuard? _guard;
    private RollingLog? _log;
    private MainWindow? _mainWindow;
    private Thread? _focusWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        SkillyPaths.EnsureApplicationDirectories();
        _log = new RollingLog(SkillyPaths.LogsDirectory);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _log.Info($"Skilly {version} starting. Exe: {Environment.ProcessPath}. Cwd: {Environment.CurrentDirectory}. Root: {SkillyPaths.ApplicationRoot}.");

        _guard = new SingleInstanceGuard();
        if (!_guard.IsPrimary)
        {
            var signaled = SingleInstanceGuard.TrySignalFocus();
            _log.Info($"Existing instance already running for this user; focus signal sent={signaled}. Secondary launch exits.");
            Shutdown(0);
            return;
        }

        StartFocusWatcher();

        var home = ResolveHome();
        var stateStore = new StateStore(_log);
        var processRunner = new ProcessRunner(_log);
        var ghClient = new GhClient(processRunner);
        var inspector = new SourceInspector(ghClient, _log);
        var installer = new GitHubInstaller(ghClient, stateStore, _log, home);
        var checker = new GitHubChecker(ghClient);
        var updater = new GitHubUpdater(checker, stateStore, _log);
        var githubProvider = new GitHubProvider(ghClient, inspector, installer, checker, updater);
        var checkRunner = new GitHubCheckRunner(githubProvider, stateStore);
        var scanner = new InventoryScanner();
        InventorySnapshot RefreshInventory() => scanner.Scan(home, stateStore.Load());

        var viewModel = new ViewModels.MainViewModel();
        viewModel.LoadInventory(RefreshInventory());

        _mainWindow = new MainWindow(_log, viewModel, githubProvider, checkRunner, RefreshInventory);
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _log.Info("Primary instance ready.");
    }

    private static string ResolveHome()
    {
        var configured = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void StartFocusWatcher()
    {
        var guard = _guard!;
        var dispatcher = Dispatcher;
        _focusWatcher = new Thread(() =>
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    if (guard.FocusEvent.WaitOne(500))
                    {
                        dispatcher.BeginInvoke(() =>
                        {
                            var window = _mainWindow;
                            if (window is null)
                            {
                                return;
                            }

                            if (window.WindowState == WindowState.Minimized)
                            {
                                window.WindowState = WindowState.Normal;
                            }

                            window.Show();
                            window.Activate();
                            window.Focus();
                        });
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "SkillyFocusWatcher",
        };
        _focusWatcher.Start();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _focusWatcher?.Join(1000);
        _log?.Info($"Skilly exiting with code {e.ApplicationExitCode}.");
        _guard?.Dispose();
        base.OnExit(e);
    }
}
