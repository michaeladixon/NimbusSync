namespace NimbusSync.App.ViewModels;

public enum ActivityKind { Info, Success, Warning, Error }

public sealed record ActivityEntry(DateTimeOffset Timestamp, string Source, string Message, ActivityKind Kind)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("T");

    public string Glyph => Kind switch
    {
        ActivityKind.Success => "",
        ActivityKind.Warning => "",
        ActivityKind.Error => "",
        _ => ""
    };
}
