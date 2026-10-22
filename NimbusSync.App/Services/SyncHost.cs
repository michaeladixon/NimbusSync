using System.IO;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using NimbusSync.App.Cloud;
using NimbusSync.Engine.Abstractions;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.Sync;

namespace NimbusSync.App.Services;

/// <summary>
/// Owns the Drive connection and one <see cref="SyncEngine"/> per running profile.
/// The engine holds a single profile, so each running profile gets its own engine and watcher.
///
/// Profile rows carry the change token. The engine advances the token on the profile
/// instance it was started with, so this class never upserts a copy that might hold a
/// stale token: edits are applied to a fresh read, and only while the profile is stopped.
///
/// Events are raised on background threads.
/// </summary>
public sealed class SyncHost : IAsyncDisposable
{
    private readonly ISyncStateStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SyncHost> _logger;
    private readonly Dictionary<long, RunningProfile> _running = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleDriveProvider? _cloud;

    public SyncHost(ISyncStateStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SyncHost>();
    }

    public event EventHandler<ProfileStatusEventArgs>? StatusChanged;
    public event EventHandler<ProfileConflictEventArgs>? ConflictDetected;

    public bool IsSignedIn => _cloud is not null;

    /// <summary>True when a refresh token is on disk, so sign-in completes without a browser prompt.</summary>
    public static bool HasStoredToken =>
        Directory.Exists(AppPaths.TokenStore) && Directory.EnumerateFiles(AppPaths.TokenStore).Any();

    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);

    // ------------------------------------------------------------------ account

    /// <summary>Opens the browser for consent unless a refresh token is already stored.</summary>
    public async Task SignInAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        var cloud = await GoogleDriveProvider.CreateAsync(clientId, clientSecret, AppPaths.TokenStore, ct);
        _cloud?.Dispose();
        _cloud = cloud;
    }

    public async Task SignOutAsync()
    {
        await StopAllAsync();
        _cloud?.Dispose();
        _cloud = null;
        await new FileDataStore(AppPaths.TokenStore, fullPath: true).ClearAsync();
    }

    // ------------------------------------------------------------------ profiles

    public Task<IReadOnlyList<SyncProfile>> GetProfilesAsync(CancellationToken ct = default) => _store.GetAllProfilesAsync(ct);

    public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(long profileId, CancellationToken ct = default)
        => _store.GetUnresolvedConflictsAsync(profileId, ct);

    public Task<int> CountPendingAsync(long profileId, CancellationToken ct = default)
        => _store.CountPendingOperationsAsync(profileId, ct);

    public bool IsRunning(long profileId)
    {
        lock (_running) return _running.ContainsKey(profileId);
    }

    /// <summary>
    /// Creates a profile, or updates the user-editable fields of a stopped one.
    /// Root paths are fixed after creation: re-pointing a profile would invalidate every baseline.
    /// </summary>
    public async Task<SyncProfile> SaveProfileAsync(SyncProfile edited, CancellationToken ct = default)
    {
        if (edited.Id == 0)
        {
            edited.ChangeToken = null;
            await _store.UpsertProfileAsync(edited, ct);
            return edited;
        }

        if (IsRunning(edited.Id))
            throw new InvalidOperationException("Pause the profile before editing it.");

        var current = await _store.GetProfileAsync(edited.Id, ct)
                      ?? throw new KeyNotFoundException($"Profile {edited.Id} no longer exists.");
        current.Name = edited.Name;
        current.Direction = edited.Direction;
        current.PollIntervalSeconds = edited.PollIntervalSeconds;
        current.NativeFilePolicy = edited.NativeFilePolicy;
        current.ExcludePatterns = edited.ExcludePatterns;
        await _store.UpsertProfileAsync(current, ct);
        return current;
    }

    public async Task DeleteProfileAsync(long profileId, CancellationToken ct = default)
    {
        await StopAsync(profileId);
        await _store.DeleteProfileAsync(profileId, ct);
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts the profile and marks it enabled. Returns after the initial scan, if one is needed.</summary>
    public async Task StartAsync(long profileId, CancellationToken ct = default)
    {
        var cloud = _cloud ?? throw new InvalidOperationException("Connect a Google account first.");

        await _gate.WaitAsync(ct);
        RunningProfile running;
        try
        {
            if (IsRunning(profileId)) return;

            var profile = await _store.GetProfileAsync(profileId, ct)
                          ?? throw new KeyNotFoundException($"Profile {profileId} no longer exists.");
            if (!profile.IsEnabled)
            {
                profile.IsEnabled = true;
                await _store.UpsertProfileAsync(profile, ct);
            }

            var engine = new SyncEngine(cloud, _store, new DebouncingFileSystemMonitor(), _loggerFactory.CreateLogger<SyncEngine>());
            running = new RunningProfile(profile, engine,
                (_, e) => StatusChanged?.Invoke(this, new ProfileStatusEventArgs(profileId, e.State, e.Message)),
                (_, c) => ConflictDetected?.Invoke(this, new ProfileConflictEventArgs(profileId, c)));
            engine.StatusChanged += running.OnStatus;
            engine.ConflictDetected += running.OnConflict;
            lock (_running) _running[profileId] = running;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await running.Engine.StartAsync(running.Profile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || !IsCurrent(profileId, running))
        {
            // Paused or shut down during the initial scan. StopAsync may already have removed it.
            await RemoveAsync(profileId, stopEngine: false, expected: running);
            throw;
        }
        catch (Exception ex)
        {
            // Nobody cancelled, so a cancellation here is a request timeout and must surface as a failure.
            _logger.LogError(ex, "Failed to start profile {ProfileId}", profileId);
            await RemoveAsync(profileId, stopEngine: false, expected: running);
            StatusChanged?.Invoke(this, new ProfileStatusEventArgs(profileId, SyncEngineState.Error, ex.Message));
            if (ex is OperationCanceledException) throw new TimeoutException(ex.Message, ex);
            throw;
        }
    }

    /// <summary>Stops the profile and marks it disabled so it stays paused across restarts.</summary>
    public async Task PauseAsync(long profileId)
    {
        // Serialized with StartAsync's registration: a start that read the profile but hasn't
        // registered yet would otherwise re-enable it after this write.
        await _gate.WaitAsync();
        try
        {
            var profile = await StopAsync(profileId) ?? await _store.GetProfileAsync(profileId);
            if (profile is null || !profile.IsEnabled) return;
            profile.IsEnabled = false;
            // Safe: the engine is stopped, so this instance's token is the latest one.
            await _store.UpsertProfileAsync(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the engine without changing the profile's enabled flag (app exit, sign-out).</summary>
    public Task<SyncProfile?> StopAsync(long profileId) => RemoveAsync(profileId, stopEngine: true);

    public async Task StopAllAsync()
    {
        long[] ids;
        lock (_running) ids = _running.Keys.ToArray();
        await Task.WhenAll(ids.Select(StopAsync));
    }

    /// <summary>Runs a full cycle now on a running profile.</summary>
    public Task SyncNowAsync(long profileId, CancellationToken ct = default)
    {
        var running = Get(profileId) ?? throw new InvalidOperationException("The profile is paused.");
        return running.Engine.RunOnceAsync(running.Profile, ct);
    }

    /// <summary>Resolution is planned by the profile's engine, so the profile must be running.</summary>
    public Task ResolveConflictAsync(long profileId, long conflictId, ConflictResolution resolution, CancellationToken ct = default)
    {
        var running = Get(profileId) ?? throw new InvalidOperationException("Resume the profile to resolve its conflicts.");
        return running.Engine.ResolveConflictAsync(conflictId, resolution, ct);
    }

    private RunningProfile? Get(long profileId)
    {
        lock (_running) return _running.GetValueOrDefault(profileId);
    }

    private bool IsCurrent(long profileId, RunningProfile running)
    {
        lock (_running) return ReferenceEquals(_running.GetValueOrDefault(profileId), running);
    }

    /// <param name="expected">When set, removes only that registration; a pause and resume may have registered a newer one.</param>
    private async Task<SyncProfile?> RemoveAsync(long profileId, bool stopEngine, RunningProfile? expected = null)
    {
        RunningProfile? running;
        lock (_running)
        {
            if (!_running.TryGetValue(profileId, out running)) return null;
            if (expected is not null && !ReferenceEquals(running, expected)) return null;
            _running.Remove(profileId);
        }

        try
        {
            if (stopEngine) await running.Engine.StopAsync();
        }
        finally
        {
            running.Engine.StatusChanged -= running.OnStatus;
            running.Engine.ConflictDetected -= running.OnConflict;
            running.Engine.Dispose();
        }
        return running.Profile;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        _cloud?.Dispose();
        _gate.Dispose();
    }

    private sealed record RunningProfile(
        SyncProfile Profile,
        SyncEngine Engine,
        EventHandler<SyncStatusEventArgs> OnStatus,
        EventHandler<SyncConflict> OnConflict);
}

public sealed class ProfileStatusEventArgs(long profileId, SyncEngineState state, string message) : EventArgs
{
    public long ProfileId { get; } = profileId;
    public SyncEngineState State { get; } = state;
    public string Message { get; } = message;
}

public sealed class ProfileConflictEventArgs(long profileId, SyncConflict conflict) : EventArgs
{
    public long ProfileId { get; } = profileId;
    public SyncConflict Conflict { get; } = conflict;
}
