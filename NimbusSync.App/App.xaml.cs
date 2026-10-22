using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NimbusSync.App.Services;
using NimbusSync.App.ViewModels;
using NimbusSync.App.Views;
using NimbusSync.Engine.State;
using Serilog;
using Serilog.Extensions.Logging;

namespace NimbusSync.App;

public partial class App : Application
{
    // Per-user names: one instance per Windows session; a second launch brings the window forward.
    private static readonly string InstanceName = $"NimbusSync.{Environment.UserName}";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateWait;
    private SerilogLoggerFactory? _loggerFactory;
    private Microsoft.Extensions.Logging.ILogger? _logger;
    private SyncHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIconController? _tray;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, $@"Local\{InstanceName}.Mutex", out var isFirst);
        if (!isFirst)
        {
            if (EventWaitHandle.TryOpenExisting($@"Local\{InstanceName}.Activate", out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}.Activate");
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateSignal,
            (_, _) => Dispatcher.InvokeAsync(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        AppPaths.EnsureCreated();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogDir, "nimbus-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggerFactory = new SerilogLoggerFactory(serilog, dispose: true);
        _logger = _loggerFactory.CreateLogger<App>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var settings = AppSettings.Load();
            _host = new SyncHost(new SqliteSyncStateStore(AppPaths.StateDb), _loggerFactory);
            _vm = new MainViewModel(_host, settings, new WpfUiServices(() => _window), Dispatcher, _loggerFactory.CreateLogger<MainViewModel>());

            _window = new MainWindow(_vm);
            _window.Closing += OnMainWindowClosing;
            _tray = new TrayIconController(_vm, ShowMainWindow, () => _ = ExitAsync());

            if (!(settings.StartMinimized && settings.HasCredentials))
                ShowMainWindow();

            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Exit rather than linger without a window: with ShutdownMode=OnExplicitShutdown the process
            // would keep the single-instance mutex, and every later launch would only signal it.
            _logger.LogCritical(ex, "Startup failed");
            MessageBox.Show(ex.Message, "NimbusSync couldn't start", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            Shutdown(1);
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window keeps syncing in the tray; Quit lives in the tray menu.
        if (_exiting) return;
        e.Cancel = true;
        _window!.Hide();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();
        _vm?.Shutdown();
        try
        {
            if (_host is not null) await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while stopping sync engines");
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _activateWait?.Unregister(null);
        _activateSignal?.Dispose();
        _loggerFactory?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, "NimbusSync hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
