namespace ReaperControl.Reaper;

/// <summary>
/// Names of REAPER's render presets, read from reaper-render.ini (and reaper-render2.ini) in the
/// resource path. A preset can consist of several lines (format, output, post-processing,
/// metadata) that share one name.
/// </summary>
public static class RenderPresetCatalog
{
    private static readonly string[] PresetFiles = ["reaper-render.ini", "reaper-render2.ini"];

    private static readonly HashSet<string> PresetLineKinds =
    [
        "<RENDERPRESET", "<RENDERPRESET2", "RENDERPRESET_OUTPUT", "RENDERPRESET_EXT", "<RENDERPRESETMETADATA"
    ];

    /// <summary>All preset names, sorted; empty when no resource path is set or no file exists.</summary>
    public static IReadOnlyList<string> GetNames(string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in PresetFiles.Select(f => Path.Combine(resourcePath, f)).Where(File.Exists))
        {
            foreach (var line in File.ReadLines(file))
            {
                var tokens = Tokenize(line.TrimStart());
                if (tokens.Count >= 2 && PresetLineKinds.Contains(tokens[0]))
                {
                    names.Add(tokens[1]);
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// REAPER's config tokenizer: whitespace separated; a token starting with " ' or ` runs to the
    /// same quote followed by whitespace (or the end of the line).
    /// </summary>
    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var pos = 0;
        while (pos < line.Length)
        {
            while (pos < line.Length && char.IsWhiteSpace(line[pos]))
            {
                pos++;
            }

            if (pos >= line.Length)
            {
                break;
            }

            var quote = line[pos];
            if (quote is '"' or '\'' or '`')
            {
                var start = pos + 1;
                var end = start;
                while (end < line.Length && !(line[end] == quote && (end + 1 == line.Length || char.IsWhiteSpace(line[end + 1]))))
                {
                    end++;
                }

                tokens.Add(line[start..Math.Min(end, line.Length)]);
                pos = end + 1;
            }
            else
            {
                var end = pos;
                while (end < line.Length && !char.IsWhiteSpace(line[end]))
                {
                    end++;
                }

                tokens.Add(line[pos..end]);
                pos = end;
            }
        }

        return tokens;
    }
}
