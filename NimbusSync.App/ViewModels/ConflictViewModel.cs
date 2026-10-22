using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusSync.Engine.Models;

namespace NimbusSync.App.ViewModels;

public sealed partial class ConflictViewModel : ObservableObject
{
    private readonly Func<ConflictViewModel, ConflictResolution, Task> _resolve;
    private readonly Action<ConflictViewModel> _reveal;

    public ConflictViewModel(SyncConflict conflict, ProfileViewModel profile,
        Func<ConflictViewModel, ConflictResolution, Task> resolve, Action<ConflictViewModel> reveal)
    {
        Conflict = conflict;
        Profile = profile;
        _resolve = resolve;
        _reveal = reveal;
        CanResolve = profile.IsRunning;
    }

    public SyncConflict Conflict { get; }
    public ProfileViewModel Profile { get; }
    public long Id => Conflict.Id;

    public string FileName => Path.GetFileName(Conflict.RelativePath);

    public string Location
    {
        get
        {
            var dir = Path.GetDirectoryName(Conflict.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(dir) ? Profile.Name : $"{Profile.Name} › {dir.Replace(Path.DirectorySeparatorChar.ToString(), " › ")}";
        }
    }

    public DateTimeOffset DetectedAt => Conflict.DetectedAt;
    public long? LocalSize => Conflict.LocalSize;
    public long? RemoteSize => Conflict.RemoteSize;
    public string LocalModifiedText => FormatTime(Conflict.LocalModified);
    public string RemoteModifiedText => FormatTime(Conflict.RemoteModified);

    public string AbsoluteLocalPath =>
        Path.Combine(Profile.LocalRootPath, Conflict.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The engine plans resolutions, so the profile must be running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepLocalCommand), nameof(KeepRemoteCommand), nameof(KeepBothCommand))]
    private bool _canResolve;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepLocalCommand), nameof(KeepRemoteCommand), nameof(KeepBothCommand))]
    private bool _isBusy;

    private bool CanAct => CanResolve && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task KeepLocal() => _resolve(this, ConflictResolution.KeepLocal);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task KeepRemote() => _resolve(this, ConflictResolution.KeepRemote);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task KeepBoth() => _resolve(this, ConflictResolution.KeepBoth);

    [RelayCommand]
    private void Reveal() => _reveal(this);

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("g") ?? "—";
}
