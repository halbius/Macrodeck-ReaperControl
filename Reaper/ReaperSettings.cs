namespace ReaperControl.Reaper;

/// <summary>
/// Connection settings for REAPER's web interface
/// (Preferences → Control/OSC/web → Add → Web browser interface).
/// </summary>
public sealed record ReaperSettings(
    string Host,
    int Port,
    string? Username,
    string? Password,
    string? ResourcePath)
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 8080;

    public static ReaperSettings Default { get; } = new(DefaultHost, DefaultPort, null, null, null);

    public Uri BaseUri => new UriBuilder("http", Host, Port, "/").Uri;

    /// <summary>reaper-kb.ini inside REAPER's resource path, if one was configured.</summary>
    public string? KeyBindingsFile =>
        string.IsNullOrWhiteSpace(ResourcePath) ? null : Path.Combine(ResourcePath, "reaper-kb.ini");
}
