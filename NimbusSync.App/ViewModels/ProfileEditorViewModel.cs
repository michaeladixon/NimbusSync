using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusSync.App.Services;
using NimbusSync.Engine.Models;

namespace NimbusSync.App.ViewModels;

public sealed record PollOption(int Seconds, string Label);

/// <summary>Create or edit a sync profile. Root locations are fixed once a profile exists.</summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly SyncProfile? _existing;
    private readonly IReadOnlyList<string> _otherRoots;

    public ProfileEditorViewModel(SyncProfile? existing, IEnumerable<string> otherProfileRoots)
    {
        _existing = existing;
        _otherRoots = otherProfileRoots.Select(NormalizeDir).ToList();

        if (existing is null)
        {
            ExcludePatterns = string.Join(Environment.NewLine, SyncProfile.DefaultExcludes);
            return;
        }

        Name = existing.Name;
        LocalRootPath = existing.LocalRootPath;
        RemoteInput = existing.RemoteFolderId;
        SharedDriveInput = existing.SharedDriveId ?? "";
        Direction = existing.Direction;
        PollIntervalSeconds = existing.PollIntervalSeconds;
        NativeFilePolicy = existing.NativeFilePolicy;
        ExcludePatterns = existing.ExcludePatterns.Replace("\n", Environment.NewLine);
        if (PollOptions.All(o => o.Seconds != existing.PollIntervalSeconds))
            PollOptions.Add(new PollOption(existing.PollIntervalSeconds, $"Every {existing.PollIntervalSeconds} seconds"));
    }

    public event EventHandler<bool>? RequestClose;

    public bool IsNew => _existing is null;
    public bool IsExisting => !IsNew;
    public string Title => IsNew ? "Add a sync folder" : $"Edit “{_existing!.Name}”";
    public string SaveLabel => IsNew ? "Add folder" : "Save";

    public List<PollOption> PollOptions { get; } =
    [
        new(30, "Every 30 seconds"),
        new(60, "Every minute"),
        new(300, "Every 5 minutes"),
        new(900, "Every 15 minutes")
    ];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _localRootPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteHint))]
    private string _remoteInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedDriveHint))]
    private string _sharedDriveInput = "";

    [ObservableProperty] private SyncDirection _direction = SyncDirection.Bidirectional;
    [ObservableProperty] private int _pollIntervalSeconds = 60;
    [ObservableProperty] private NativeFilePolicy _nativeFilePolicy = NativeFilePolicy.LinkStub;
    [ObservableProperty] private string _excludePatterns = "";
    [ObservableProperty] private string? _error;

    public string RemoteHint => string.IsNullOrWhiteSpace(RemoteInput)
        ? "Paste the folder's link from drive.google.com, or its ID."
        : DriveLinkParser.TryGetId(RemoteInput, out var id) ? $"Folder ID: {id}" : "That isn't a Drive folder link or ID.";

    public string SharedDriveHint => string.IsNullOrWhiteSpace(SharedDriveInput)
        ? "Leave empty for My Drive."
        : DriveLinkParser.TryGetId(SharedDriveInput, out var id) ? $"Shared drive ID: {id}" : "That isn't a shared drive link or ID.";

    /// <summary>Only valid after a successful save.</summary>
    public SyncProfile Result { get; private set; } = null!;

    [RelayCommand]
    private void Save()
    {
        Error = Validate(out var profile);
        if (Error is not null) return;
        Result = profile!;
        RequestClose?.Invoke(this, true);
    }

    private string? Validate(out SyncProfile? profile)
    {
        profile = null;
        var name = Name.Trim();
        if (name.Length == 0) return "Give this folder a name.";

        if (!Path.IsPathFullyQualified(LocalRootPath.Trim())) return "Choose a folder on this PC.";
        var local = NormalizeDir(LocalRootPath.Trim());
        if (IsNew)
        {
            if (Path.GetPathRoot(local) is { } root && NormalizeDir(root) == local)
                return "Choose a folder, not a whole drive.";
            var dataDir = NormalizeDir(AppPaths.DataDir);
            if (IsSameOrNested(local, dataDir))
                return "That folder overlaps NimbusSync's own data folder.";
            if (_otherRoots.FirstOrDefault(r => IsSameOrNested(local, r)) is { } clash)
                return $"That folder overlaps another sync folder ({clash}).";
        }

        if (!DriveLinkParser.TryGetId(RemoteInput, out var remoteId)) return "Enter a Drive folder link or ID.";

        string? sharedDriveId = null;
        if (!string.IsNullOrWhiteSpace(SharedDriveInput))
        {
            if (!DriveLinkParser.TryGetId(SharedDriveInput, out var driveId)) return "The shared drive link or ID isn't valid.";
            sharedDriveId = driveId;
        }

        var excludes = string.Join('\n', ExcludePatterns
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        profile = new SyncProfile
        {
            Id = _existing?.Id ?? 0,
            Name = name,
            LocalRootPath = IsNew ? local : _existing!.LocalRootPath,
            RemoteFolderId = IsNew ? remoteId : _existing!.RemoteFolderId,
            SharedDriveId = IsNew ? sharedDriveId : _existing!.SharedDriveId,
            Direction = Direction,
            PollIntervalSeconds = PollIntervalSeconds,
            NativeFilePolicy = NativeFilePolicy,
            ExcludePatterns = excludes
        };
        return null;
    }

    private static string NormalizeDir(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrNested(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
