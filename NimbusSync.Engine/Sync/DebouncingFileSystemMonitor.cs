using System.Collections.Concurrent;
using NimbusSync.Engine.Abstractions;
using NimbusSync.Engine.Models;

namespace NimbusSync.Engine.Sync;

/// <summary>
/// FileSystemWatcher wrapper. Coalesces bursts of events per path and holds them
/// until the path has been quiet for the debounce window. The engine drains it;
/// nothing else does, so no change can be consumed by the wrong party.
/// </summary>
public sealed class DebouncingFileSystemMonitor : IFileSystemMonitor
{
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string? _rootPath;
    private ExclusionMatcher _exclusions = new([]);
    private volatile bool _needsFullRescan;

    public DebouncingFileSystemMonitor(TimeSpan? debounceInterval = null)
    {
        _debounce = debounceInterval ?? TimeSpan.FromMilliseconds(750);
    }

    public bool NeedsFullRescan => _needsFullRescan;
    public void AcknowledgeRescan() => _needsFullRescan = false;

    public void Start(string rootPath, IReadOnlyList<string> excludePatterns)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _exclusions = new ExclusionMatcher(excludePatterns);

        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += (_, e) => Record(e.FullPath, ChangeType.Created);
        _watcher.Changed += (_, e) => Record(e.FullPath, ChangeType.Modified);
        _watcher.Deleted += (_, e) => Record(e.FullPath, ChangeType.Deleted);
        _watcher.Renamed += (_, e) =>
        {
            Record(e.OldFullPath, ChangeType.Deleted);
            Record(e.FullPath, ChangeType.Created);
        };
        _watcher.Error += (_, _) => _needsFullRescan = true;
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    public IReadOnlyList<DetectedChange> DrainChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var ready = new List<DetectedChange>();
        foreach (var kvp in _pending)
        {
            if (now - kvp.Value.LastEventAt < _debounce) continue;
            if (_pending.TryRemove(kvp.Key, out var p))
                ready.Add(new DetectedChange { RelativePath = p.RelativePath, Type = p.Type });
        }
        return ready;
    }

    /// <summary>Test hook and manual-scan hook: queue a change as if the watcher saw it.</summary>
    public void Inject(string relativePath, ChangeType type)
        => _pending[relativePath] = new PendingChange(relativePath, type, DateTimeOffset.UtcNow - _debounce);

    private void Record(string fullPath, ChangeType type)
    {
        if (_rootPath is null) return;
        string relative;
        try { relative = PathNormalizer.ToRelative(_rootPath, fullPath); }
        catch (ArgumentException) { return; }

        if (relative.Length == 0 || _exclusions.IsExcluded(relative)) return;

        _pending.AddOrUpdate(relative,
            _ => new PendingChange(relative, type, DateTimeOffset.UtcNow),
            (_, existing) =>
            {
                // Delete wins over everything; Created wins over Modified.
                var effective = type == ChangeType.Deleted ? ChangeType.Deleted
                    : existing.Type == ChangeType.Deleted ? ChangeType.Created
                    : existing.Type == ChangeType.Created ? ChangeType.Created
                    : type;
                return new PendingChange(relative, effective, DateTimeOffset.UtcNow);
            });
    }

    public void Dispose() => Stop();

    private sealed record PendingChange(string RelativePath, ChangeType Type, DateTimeOffset LastEventAt);
}
