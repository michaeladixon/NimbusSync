using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusSync.Engine.Models;
using NimbusSync.Engine.Sync;

namespace NimbusSync.App.ViewModels;

public enum StatusKind { Ok, Busy, Paused, Attention, Error, Offline }

/// <summary>Actions a profile card can trigger; implemented by <see cref="MainViewModel"/>.</summary>
public interface IProfileActions
{
    Task ToggleRunAsync(ProfileViewModel profile);
    Task SyncNowAsync(ProfileViewModel profile);
    Task EditAsync(ProfileViewModel profile);
    Task RemoveAsync(ProfileViewModel profile);
    void OpenFolder(ProfileViewModel profile);
}

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly IProfileActions _actions;

    public ProfileViewModel(SyncProfile profile, IProfileActions actions)
    {
        _actions = actions;
        Apply(profile);
    }

    public long Id { get; private set; }
    public SyncProfile Profile { get; private set; } = null!;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _localRootPath = "";
    [ObservableProperty] private string _remoteSummary = "";
    [ObservableProperty] private string _directionText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    private SyncEngineState _state = SyncEngineState.Stopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind), nameof(ToggleRunGlyph), nameof(ToggleRunLabel))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool _isRunning;

    /// <summary>A start, pause, or manual sync issued from the UI is in flight.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(ToggleRunCommand), nameof(EditCommand), nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    private int _pendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    private int _conflictCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Kind))]
    private string? _errorMessage;

    [ObservableProperty] private DateTimeOffset? _lastSyncedAt;

    public string ToggleRunGlyph => IsRunning ? "" : "";
    public string ToggleRunLabel => IsRunning ? "Pause" : "Resume";

    public string StatusText
    {
        get
        {
            if (!IsRunning)
                return IsBusy ? "Starting…" : ErrorMessage is not null ? "Couldn't start" : "Paused";

            return State switch
            {
                SyncEngineState.InitialScan => "Scanning for the first time…",
                SyncEngineState.Syncing => PendingCount > 0 ? $"Syncing · {PendingCount} left" : "Syncing…",
                SyncEngineState.Error => "Sync error, retrying",
                _ when ConflictCount > 0 => ConflictCount == 1 ? "1 conflict needs you" : $"{ConflictCount} conflicts need you",
                _ when PendingCount > 0 => $"{PendingCount} queued",
                _ => "Up to date"
            };
        }
    }

    public StatusKind Kind
    {
        get
        {
            if (!IsRunning)
                return IsBusy ? StatusKind.Busy : ErrorMessage is not null ? StatusKind.Error : StatusKind.Paused;

            return State switch
            {
                SyncEngineState.Error => StatusKind.Error,
                _ when ConflictCount > 0 => StatusKind.Attention,
                SyncEngineState.InitialScan or SyncEngineState.Syncing => StatusKind.Busy,
                _ when PendingCount > 0 => StatusKind.Busy,
                _ => StatusKind.Ok
            };
        }
    }

    public void Apply(SyncProfile profile)
    {
        Profile = profile;
        Id = profile.Id;
        Name = profile.Name;
        LocalRootPath = profile.LocalRootPath;
        RemoteSummary = profile.SharedDriveId is null
            ? $"My Drive · {profile.RemoteFolderId}"
            : $"Shared drive · {profile.RemoteFolderId}";
        DirectionText = profile.Direction switch
        {
            SyncDirection.LocalToRemote => "Upload only",
            SyncDirection.RemoteToLocal => "Download only",
            _ => "Two-way"
        };
    }

    /// <summary>Re-evaluates relative timestamps such as "2 min ago".</summary>
    public void RefreshClock() => OnPropertyChanged(nameof(LastSyncedAt));

    private bool CanSyncNow => IsRunning && !IsBusy;
    private bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ToggleRun() => _actions.ToggleRunAsync(this);

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private Task SyncNow() => _actions.SyncNowAsync(this);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Edit() => _actions.EditAsync(this);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Remove() => _actions.RemoveAsync(this);

    [RelayCommand]
    private void OpenFolder() => _actions.OpenFolder(this);
}
