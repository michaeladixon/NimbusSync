namespace NimbusSync.Engine.Sync;

public sealed class SyncEngineOptions
{
    /// <summary>Operations are dropped (and the item marked Error) after this many failures.</summary>
    public int MaxOperationAttempts { get; init; } = 10;

    /// <summary>Base delay for exponential backoff on failed operations.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Operations fetched per execution pass.</summary>
    public int OperationBatchSize { get; init; } = 200;

    /// <summary>Caps how far a remote parent chain is walked before giving up.</summary>
    public int MaxRemoteDepth { get; init; } = 64;
}
