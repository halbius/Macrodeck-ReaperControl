namespace ReaperControl.Reaper;

/// <summary>
/// The Lua scripts that come with the plugin (folder ReaperScripts in the package) and where they go
/// in REAPER: &lt;resource path&gt;/Scripts/Macrodeck-ReaperControl.
/// </summary>
public static class ReaperScriptFiles
{
    /// <summary>Folder below REAPER's Scripts folder. Changing it changes the scripts' command ids.</summary>
    public const string InstallFolder = "Macrodeck-ReaperControl";

    /// <summary>The background scripts the plugin starts and stops on its own (see ScriptKeeper).</summary>
    public static readonly IReadOnlyList<BackgroundScript> Background =
    [
        new("ItemState", "ReaperControl_ItemState.lua"),
        new("TrackState", "ReaperControl_TrackState.lua"),
        new("Timecode", "ReaperControl_Timecode.lua"),
        new("Context", "ReaperControl_Context.lua"),
    ];

    /// <summary>
    /// The background script that publishes ExtState "ReaperControl"/<paramref name="key" />, or null.
    /// </summary>
    public static BackgroundScript? ForKey(string key) => key switch
    {
        _ when key.StartsWith("Item", StringComparison.Ordinal) => Background[0],
        _ when key.StartsWith("Track", StringComparison.Ordinal) => Background[1],
        "Timecode" or "PlayRate" => Background[2],
        "Context" => Background[3],
        _ => null
    };

    /// <summary>The folder with the bundled scripts: next to the plugin (dev run) or at the package root.</summary>
    public static string? SourceFolder()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ReaperScripts"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "ReaperScripts"))
        ];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    public static string TargetFolder(string resourcePath) =>
        Path.Combine(resourcePath, "Scripts", InstallFolder);

    /// <summary>Copies all bundled scripts into REAPER's Scripts folder, overwriting older copies.</summary>
    /// <returns>The copied file names.</returns>
    /// <exception cref="DirectoryNotFoundException">The bundled scripts were not found.</exception>
    public static IReadOnlyList<string> Install(string resourcePath)
    {
        var source = SourceFolder()
                     ?? throw new DirectoryNotFoundException("The plugin's ReaperScripts folder was not found.");
        var target = TargetFolder(resourcePath);
        Directory.CreateDirectory(target);

        var copied = new List<string>();
        foreach (var file in Directory.GetFiles(source, "*.lua"))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(target, name), overwrite: true);
            copied.Add(name);
        }

        return copied;
    }
}

/// <param name="Name">Used for the ExtState keys "&lt;Name&gt;Running" and "&lt;Name&gt;Heartbeat".</param>
/// <param name="FileName">File in ReaperScripts.</param>
public sealed record BackgroundScript(string Name, string FileName)
{
    public string RunningKey => Name + "Running";
    public string HeartbeatKey => Name + "Heartbeat";
}
