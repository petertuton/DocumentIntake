namespace DocumentIntake.Functions.Options;

/// <summary>
/// Container names used by the pipeline. These are constants rather than configuration
/// because orchestrator code must behave identically across replays, and the Bicep
/// deployment creates exactly these containers.
/// </summary>
public static class Containers
{
    public const string Inbox = "inbox";
    public const string Processing = "processing";
    public const string Ignored = "ignored";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } =
    [
        Inbox,
        Processing,
        Ignored,
        Completed,
        Failed,
    ];
}
