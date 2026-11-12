using System.Collections.Concurrent;
using System.Security.Cryptography;
using MacroDeck.Sdk.Actions;

namespace ReaperControl.Reaper;

/// <summary>
/// The plugin's own button icons: image files in Assets/Icons next to the plugin, addressed by file
/// name without extension ("play" = Assets/Icons/play.svg or play.png).
/// </summary>
/// <remarks>
/// To add an icon: put the file into Assets/Icons in the project - the project file copies the whole
/// folder into the package. Macro Deck accepts only PNG, JPEG, WebP and GIF from plugins (no SVG).
/// The dimmed look for "off" is its own file: "&lt;name&gt;-dim.png"; without one, "off" shows the full icon.
/// </remarks>
public static class IconLibrary
{
    // Next to the plugin's .dll (publish output) and in the package root (macrodeck-build.json "include").
    private static readonly string[] Folders =
    [
        .. new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Icons"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Icons"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Assets", "Icons"))
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>For the log: which folders exist and which icons they hold.</summary>
    public static string Describe() => string.Join("; ", Folders.Select(f => Directory.Exists(f)
        ? $"{f}: {string.Join(", ", Directory.EnumerateFiles(f).Select(p =>
            new FileInfo(p).Length > MaxBytes ? $"{Path.GetFileName(p)} (TOO LARGE, ignored)" : Path.GetFileName(p)))}"
        : $"{f}: (missing)"));

    private static readonly (string Extension, string MediaType)[] Formats =
    [
        (".png", "image/png"),
        (".jpg", "image/jpeg"),
        (".gif", "image/gif"),
        (".webp", "image/webp"),
    ];

    /// <summary>Bigger files are ignored: use about 256 × 256 px, well under 1 MB.</summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly ConcurrentDictionary<string, ActionIconContent?> Cache = new();

    /// <summary>
    /// Snapshot for an icon, or null when the file does not exist (the button then shows its own icon).
    /// The version names icon and dimming, so Macro Deck fetches the bytes again only when either changes.
    /// </summary>
    public static ActionIconSnapshot? Snapshot(string? name, bool dimmed)
    {
        if (string.IsNullOrWhiteSpace(name) || Load(name, dimmed) is not { } content)
        {
            return null;
        }

        return new ActionIconSnapshot
        {
            Version = $"{name}|{(dimmed ? "dim" : "full")}|{Hash(content.Data)}",
            MediaType = content.MediaType
        };
    }

    /// <summary>The bytes for a version <see cref="Snapshot" /> handed out, or null for an unknown one.</summary>
    public static ActionIconContent? Content(string version)
    {
        var parts = version.Split('|');
        if (parts.Length != 3)
        {
            return null;
        }

        var content = Load(parts[0], parts[1] == "dim");
        return content is not null && Hash(content.Data) == parts[2] ? content : null;
    }

    /// <summary>The dimmed variant is "&lt;name&gt;-dim"; if it does not exist, the full icon.</summary>
    private static ActionIconContent? Load(string name, bool dimmed) =>
        Cache.GetOrAdd($"{name}|{dimmed}", _ => (dimmed ? Find(name + "-dim") : null) ?? Find(name));

    private static ActionIconContent? Find(string fileName)
    {
        foreach (var (extension, mediaType) in Formats)
        foreach (var folder in Folders)
        {
            var path = Path.Combine(folder, fileName + extension);
            // Large images stall Macro Deck's editor (the bytes travel inside a protocol message).
            if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes)
            {
                return new ActionIconContent(File.ReadAllBytes(path), mediaType);
            }
        }

        return null;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..12].ToLowerInvariant();

}
