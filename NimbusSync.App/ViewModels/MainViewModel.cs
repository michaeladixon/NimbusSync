using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NimbusSync.App.Services;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.Sync;

namespace NimbusSync.App.ViewModels;

public enum AppPage { Folders, Conflicts, Activity, Settings }

/// <summary>Dialogs and prompts, so the view model stays free of window types.</summary>
public interface IUiServices
{
    bool ShowProfileEditor(ProfileEditorViewModel editor);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
}

/// <summary>
/// Root view model for the main window and tray. Engine work runs on the thread pool;
/// host events arrive on background threads and are marshalled to the dispatcher here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IProfileActions
{
    private const int MaxActivity = 500;
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

    private readonly SyncHost _host;
    private readonly AppSettings _settings;
    private readonly IUiServices _ui;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _refreshTimer;
    /// <summary>Starts still in flight, so a pause can cancel one before the engine registers. UI thread only.</summary>
    private readonly Dictionary<long, CancellationTokenSource> _pendingStarts = new();

    public MainViewModel(SyncHost host, AppSettings settings, IUiServices ui, Dispatcher dispatcher, ILogger<MainViewModel> logger)
    {
        _host = host;
        _settings = settings;
        _ui = ui;
        _dispatcher = dispatcher;
        _logger = logger;

        ClientId = settings.ClientId ?? "";
        StartMinimized = settings.StartMinimized;

        _host.StatusChanged += (_, e) => _dispatcher.InvokeAsync(() => OnProfileStatus(e));
        _host.ConflictDetected += (_, e) => _dispatcher.InvokeAsync(() => OnConflictDetected(e));
        Conflicts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConflictCount));
            UpdateOverallStatus();
        };

        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await RefreshCountsAsync(), _dispatcher);
    }

    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];
    public ObservableCollection<ConflictViewModel> Conflicts { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    /// <summary>Raised for every newly detected conflict; the tray shows a notification.</summary>
    public event EventHandler<ConflictViewModel>? ConflictRaised;
    public event EventHandler? OverallStatusChanged;

    [ObservableProperty] private AppPage _selectedPage = AppPage.Folders;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(SignOutCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(SignOutCommand))]
    private bool _isSigningIn;

    [ObservableProperty] private string? _accountMessage;

    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientSecretInput = "";
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string? _settingsMessage;

    [ObservableProperty] private StatusKind _overallKind = StatusKind.Offline;
    [ObservableProperty] private string _overallText = "Not connected";

    public int ConflictCount => Conflicts.Count;
    public bool HasCredentials => _settings.HasCredentials;
    public bool HasSavedSecret => _settings.ClientSecretProtected is not null;
    public bool AnyRunning => Profiles.Any(p => p.IsRunning);
    public string DataDirectory => AppPaths.DataDir;

    public string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    // ------------------------------------------------------------------ startup

    public async Task InitializeAsync()
    {
        try
        {
            await _host.InitializeAsync();
            foreach (var profile in await _host.GetProfilesAsync())
                Profiles.Add(new ProfileViewModel(profile, this));
            await LoadConflictsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the state database");
            _ui.ShowError("NimbusSync couldn't start", $"The sync database at {AppPaths.StateDb} couldn't be opened.\n\n{ex.Message}");
            return;
        }

        _refreshTimer.Start();
        UpdateOverallStatus();

        // A stored refresh token means sign-in completes without opening a browser.
        if (_settings.HasCredentials && SyncHost.HasStoredToken)
            await SignInCoreAsync(interactive: false);
    }

    private async Task LoadConflictsAsync()
    {
        foreach (var profile in Profiles)
        {
            var conflicts = await _host.GetConflictsAsync(profile.Id);
            foreach (var c in conflicts) AddConflict(profile, c, notify: false);
        }
    }

    private async Task StartEnabledProfilesAsync()
    {
        foreach (var profile in Profiles.Where(p => p.Profile.IsEnabled && !p.IsRunning).ToList())
            await StartProfileAsync(profile);
    }

    // ------------------------------------------------------------------ account

    private bool CanSignIn => !IsSignedIn && !IsSigningIn;
    private bool CanSignOut => IsSignedIn && !IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignIn() => SignInCoreAsync(interactive: true);

    private async Task SignInCoreAsync(bool interactive)
    {
        if (!_settings.HasCredentials)
        {
            AccountMessage = "Add your OAuth client ID and secret first.";
            SelectedPage = AppPage.Settings;
            return;
        }

        IsSigningIn = true;
        AccountMessage = interactive ? "Finish signing in in your browser…" : "Connecting…";
        try
        {
            using var cts = new CancellationTokenSource(SignInTimeout);
            await Task.Run(() => _host.SignInAsync(_settings.ClientId!, _settings.ClientSecret!, cts.Token), cts.Token);
            IsSignedIn = true;
            AccountMessage = null;
            AddActivity("Account", "Connected to Google Drive", ActivityKind.Success);
        }
        catch (OperationCanceledException)
        {
            AccountMessage = "Sign-in timed out. Try again when you're ready.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-in failed");
            AccountMessage = $"Couldn't connect: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            UpdateOverallStatus();
        }

        if (IsSignedIn) await StartEnabledProfilesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOut()
    {
        if (!_ui.Confirm("Disconnect Google Drive?",
                "Syncing stops for every folder and the saved sign-in is removed. Your files stay where they are."))
            return;

        await _host.SignOutAsync();
        foreach (var p in Profiles)
        {
            p.IsRunning = false;
            p.State = SyncEngineState.Stopped;
            UpdateConflictAvailability(p);
        }
        IsSignedIn = false;
        AddActivity("Account", "Disconnected from Google Drive", ActivityKind.Info);
        UpdateOverallStatus();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.ClientId = string.IsNullOrWhiteSpace(ClientId) ? null : ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(ClientSecretInput)) _settings.ClientSecret = ClientSecretInput.Trim();
        _settings.StartMinimized = StartMinimized;
        try
        {
            _settings.Save();
            ClientSecretInput = "";
            SettingsMessage = "Saved.";
        }
        catch (Exception ex)
        {
            SettingsMessage = $"Couldn't save settings: {ex.Message}";
        }
        OnPropertyChanged(nameof(HasCredentials));
        OnPropertyChanged(nameof(HasSavedSecret));
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_settings.StartMinimized == value) return;
        _settings.StartMinimized = value;
        try { _settings.Save(); }
        catch (Exception ex) { SettingsMessage = $"Couldn't save settings: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(AppPaths.DataDir);

    [RelayCommand]
    private void OpenLogs() => OpenInExplorer(AppPaths.LogDir);

    // ------------------------------------------------------------------ profiles

    [RelayCommand]
    private async Task AddProfile()
    {
        var editor = new ProfileEditorViewModel(null, Profiles.Select(p => p.LocalRootPath));
        if (!_ui.ShowProfileEditor(editor)) return;

        try
        {
            var saved = await _host.SaveProfileAsync(editor.Result);
            var vm = new ProfileViewModel(saved, this);
            Profiles.Add(vm);
            AddActivity(vm.Name, $"Added {vm.LocalRootPath}", ActivityKind.Info);
            if (IsSignedIn) await StartProfileAsync(vm);
        }
        catch (Exception ex)
        {
            _ui.ShowError("Couldn't add the folder", ex.Message);
        }
        UpdateOverallStatus();
    }

    public async Task EditAsync(ProfileViewModel profile)
    {
        var wasRunning = profile.IsRunning;
        if (wasRunning && !_ui.Confirm($"Pause “{profile.Name}” to edit it?",
                "Syncing for this folder pauses while you edit and resumes when you save."))
            return;

        var editor = new ProfileEditorViewModel(profile.Profile,
            Profiles.Where(p => p != profile).Select(p => p.LocalRootPath));

        if (wasRunning) await StopProfileAsync(profile, persistPause: false);
        // Busy keeps the tray's Resume/Sync all from restarting the profile while the editor is open.
        profile.IsBusy = true;
        try
        {
            if (!_ui.ShowProfileEditor(editor)) return;
            var saved = await _host.SaveProfileAsync(editor.Result);
            profile.Apply(saved);
        }
        catch (Exception ex)
        {
            _ui.ShowError("Couldn't save the folder", ex.Message);
        }
        finally
        {
            profile.IsBusy = false;
            if (wasRunning && IsSignedIn) await StartProfileAsync(profile);
        }
    }

    public async Task RemoveAsync(ProfileViewModel profile)
    {
        if (!_ui.Confirm($"Stop syncing “{profile.Name}”?",
                $"NimbusSync forgets this folder's sync history. Nothing is deleted from {profile.LocalRootPath} or from Google Drive."))
            return;

        profile.IsBusy = true;
        try
        {
            await Task.Run(() => _host.DeleteProfileAsync(profile.Id));
            Profiles.Remove(profile);
            foreach (var c in Conflicts.Where(c => c.Profile == profile).ToList()) Conflicts.Remove(c);
            AddActivity(profile.Name, "Removed", ActivityKind.Info);
        }
        catch (Exception ex)
        {
            _ui.ShowError("Couldn't remove the folder", ex.Message);
        }
        finally
        {
            profile.IsBusy = false;
            UpdateOverallStatus();
        }
    }

    public Task ToggleRunAsync(ProfileViewModel profile)
    {
        if (profile.IsRunning) return StopProfileAsync(profile, persistPause: true);
        if (!IsSignedIn)
        {
            SelectedPage = AppPage.Settings;
            AccountMessage = "Connect Google Drive to start syncing.";
            return Task.CompletedTask;
        }
        return StartProfileAsync(profile);
    }

    public async Task SyncNowAsync(ProfileViewModel profile)
    {
        profile.IsBusy = true;
        try
        {
            await Task.Run(() => _host.SyncNowAsync(profile.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual sync failed for profile {ProfileId}", profile.Id);
            AddActivity(profile.Name, $"Sync failed: {ex.Message}", ActivityKind.Error);
        }
        finally
        {
            profile.IsBusy = false;
        }
    }

    public void OpenFolder(ProfileViewModel profile) => OpenInExplorer(profile.LocalRootPath);

    private async Task StartProfileAsync(ProfileViewModel profile)
    {
        profile.IsBusy = true;
        profile.ErrorMessage = null;
        using var cts = new CancellationTokenSource();
        _pendingStarts[profile.Id] = cts;
        try
        {
            // IsRunning flips before the start finishes, so the card shows the initial scan and can be paused.
            var start = Task.Run(() => _host.StartAsync(profile.Id, cts.Token));
            profile.IsRunning = true;
            profile.IsBusy = false;
            await start;
            profile.Profile.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Paused before the start finished.
        }
        catch (Exception ex)
        {
            profile.ErrorMessage = ex.Message;
            AddActivity(profile.Name, $"Couldn't start: {ex.Message}", ActivityKind.Error);
        }
        finally
        {
            if (_pendingStarts.GetValueOrDefault(profile.Id) == cts) _pendingStarts.Remove(profile.Id);
            profile.IsRunning = _host.IsRunning(profile.Id);
            profile.IsBusy = false;
            UpdateConflictAvailability(profile);
            UpdateOverallStatus();
        }
    }

    private async Task StopProfileAsync(ProfileViewModel profile, bool persistPause)
    {
        if (_pendingStarts.TryGetValue(profile.Id, out var pending)) pending.Cancel();
        profile.IsBusy = true;
        try
        {
            if (persistPause)
            {
                await Task.Run(() => _host.PauseAsync(profile.Id));
                profile.Profile.IsEnabled = false;
                AddActivity(profile.Name, "Paused", ActivityKind.Info);
            }
            else
            {
                await Task.Run(() => _host.StopAsync(profile.Id));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop profile {ProfileId}", profile.Id);
        }
        finally
        {
            profile.IsRunning = _host.IsRunning(profile.Id);
            profile.State = SyncEngineState.Stopped;
            profile.IsBusy = false;
            UpdateConflictAvailability(profile);
            UpdateOverallStatus();
        }
    }

    // ------------------------------------------------------------------ tray actions

    [RelayCommand]
    private Task SyncAll()
        => Task.WhenAll(Profiles.Where(p => p.IsRunning && !p.IsBusy).ToList().Select(SyncNowAsync));

    [RelayCommand]
    private Task PauseAll()
        => Task.WhenAll(Profiles.Where(p => p.IsRunning).ToList().Select(p => StopProfileAsync(p, persistPause: true)));

    [RelayCommand]
    private Task ResumeAll()
    {
        if (!IsSignedIn) return Task.CompletedTask;
        return Task.WhenAll(Profiles.Where(p => !p.IsRunning && !p.IsBusy).ToList().Select(StartProfileAsync));
    }

    // ------------------------------------------------------------------ conflicts

    private async Task ResolveAsync(ConflictViewModel conflict, ConflictResolution resolution)
    {
        conflict.IsBusy = true;
        try
        {
            await Task.Run(() => _host.ResolveConflictAsync(conflict.Profile.Id, conflict.Id, resolution));
            Conflicts.Remove(conflict);
            conflict.Profile.ConflictCount = Conflicts.Count(c => c.Profile == conflict.Profile);
            var what = resolution switch
            {
                ConflictResolution.KeepLocal => "kept this PC's version",
                ConflictResolution.KeepRemote => "kept the Drive version",
                _ => "kept both versions"
            };
            AddActivity(conflict.Profile.Name, $"{conflict.FileName}: {what}", ActivityKind.Success);
        }
        catch (Exception ex)
        {
            _ui.ShowError("Couldn't resolve the conflict", ex.Message);
        }
        finally
        {
            conflict.IsBusy = false;
        }
    }

    private void AddConflict(ProfileViewModel profile, SyncConflict conflict, bool notify)
    {
        if (conflict.Resolution is not null || Conflicts.Any(c => c.Id == conflict.Id)) return;

        var vm = new ConflictViewModel(conflict, profile, ResolveAsync, c => RevealInExplorer(c.AbsoluteLocalPath));
        Conflicts.Insert(0, vm);
        profile.ConflictCount = Conflicts.Count(c => c.Profile == profile);
        if (notify) ConflictRaised?.Invoke(this, vm);
    }

    private void UpdateConflictAvailability(ProfileViewModel profile)
    {
        foreach (var c in Conflicts.Where(c => c.Profile == profile)) c.CanResolve = profile.IsRunning;
    }

    // ------------------------------------------------------------------ engine events

    private void OnProfileStatus(ProfileStatusEventArgs e)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == e.ProfileId);
        if (profile is null) return;

        var previous = profile.State;
        profile.State = e.State;

        if (e.State == SyncEngineState.Watching && previous is SyncEngineState.Syncing or SyncEngineState.InitialScan)
            profile.LastSyncedAt = DateTimeOffset.Now;

        // State names arrive as messages on every cycle; only operations and failures are worth listing.
        if (e.State == SyncEngineState.Error)
            AddActivity(profile.Name, e.Message == nameof(SyncEngineState.Error) ? "Sync cycle failed; see logs" : e.Message, ActivityKind.Error);
        else if (e.State == SyncEngineState.InitialScan && previous != SyncEngineState.InitialScan)
            AddActivity(profile.Name, "Scanning both sides for the first time", ActivityKind.Info);
        else if (!Enum.TryParse<SyncEngineState>(e.Message, out _))
            AddActivity(profile.Name, e.Message, ActivityKind.Info);

        UpdateOverallStatus();
    }

    private void OnConflictDetected(ProfileConflictEventArgs e)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == e.ProfileId);
        if (profile is null) return;

        AddConflict(profile, e.Conflict, notify: e.Conflict.Resolution is null);
        var verb = e.Conflict.Resolution is null ? "changed on both sides" : "auto-resolved by keeping both";
        AddActivity(profile.Name, $"{Path.GetFileName(e.Conflict.RelativePath)} {verb}", ActivityKind.Warning);
    }

    private async Task RefreshCountsAsync()
    {
        foreach (var profile in Profiles.Where(p => p.IsRunning).ToList())
        {
            try
            {
                profile.PendingCount = await _host.CountPendingAsync(profile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pending count failed for profile {ProfileId}", profile.Id);
            }
            profile.RefreshClock();
        }
        UpdateOverallStatus();
    }

    // ------------------------------------------------------------------ activity & status

    [RelayCommand]
    private void ClearActivity() => Activity.Clear();

    private void AddActivity(string source, string message, ActivityKind kind)
    {
        Activity.Insert(0, new ActivityEntry(DateTimeOffset.Now, source, message, kind));
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
    }

    private void UpdateOverallStatus()
    {
        var running = Profiles.Where(p => p.IsRunning).ToList();
        (OverallKind, OverallText) = true switch
        {
            _ when !IsSignedIn => (StatusKind.Offline, IsSigningIn ? "Connecting…" : "Not connected"),
            _ when Profiles.Any(p => p.Kind == StatusKind.Error) => (StatusKind.Error, "Sync problem"),
            _ when Conflicts.Count > 0 => (StatusKind.Attention, Conflicts.Count == 1 ? "1 conflict" : $"{Conflicts.Count} conflicts"),
            _ when running.Any(p => p.Kind == StatusKind.Busy) => (StatusKind.Busy, "Syncing…"),
            _ when running.Count == 0 => (StatusKind.Paused, Profiles.Count == 0 ? "No folders yet" : "Paused"),
            _ => (StatusKind.Ok, "Up to date")
        };
        OnPropertyChanged(nameof(AnyRunning));
        OverallStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ shell

    private static void OpenInExplorer(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void RevealInExplorer(string file)
    {
        var args = File.Exists(file) ? $"/select,\"{file}\"" : $"\"{Path.GetDirectoryName(file)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public void Shutdown() => _refreshTimer.Stop();
}
