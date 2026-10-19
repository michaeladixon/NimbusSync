using NimbusSync.Engine.Models;

namespace NimbusSync.Engine.Abstractions;

/// <summary>
/// Watches the local sync root and accumulates debounced change events.
/// The engine is the sole consumer: it calls <see cref="DrainChanges"/> each cycle.
/// There is deliberately no push event; a second consumer would race the engine
/// for the same changes.
/// </summary>
public interface IFileSystemMonitor : IDisposable
{
    void Start(string rootPath, IReadOnlyList<string> excludePatterns);
    void Stop();

    /// <summary>
    /// Returns and clears all changes whose last event is older than the debounce window.
    /// Changes still within the window stay queued for the next call.
    /// </summary>
    IReadOnlyList<DetectedChange> DrainChanges();

    /// <summary>
    /// Set when the OS event buffer overflowed and events were lost. The engine
    /// must perform a full local scan, then call <see cref="AcknowledgeRescan"/>.
    /// </summary>
    bool NeedsFullRescan { get; }
    void AcknowledgeRescan();
}
