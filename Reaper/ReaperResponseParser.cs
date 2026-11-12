using System.Globalization;

namespace ReaperControl.Reaper;

public sealed record TransportState(
    int PlayState, double PositionSeconds, bool Repeat, string PositionString, string PositionBeats = "");

public sealed record ReaperTrack(int Index, string Name, int Flags, double Volume, double Pan)
{
    // Flag bits as documented for the web interface TRACK reply.
    public bool IsFolder => (Flags & 1) != 0;
    public bool IsSelected => (Flags & 2) != 0;
    public bool IsMuted => (Flags & 8) != 0;
    public bool IsSoloed => (Flags & 16) != 0;
    public bool IsRecordArmed => (Flags & 64) != 0;
}

public sealed record ReaperMarker(bool IsRegion, string Name, int Id, double Start, double? End, int? Color);

/// <summary>Parses the tab-separated line format REAPER's web interface answers with.</summary>
public static class ReaperResponseParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // TRANSPORT \t playstate \t position_seconds \t isRepeatOn \t position_string \t position_string_beats
    public static TransportState? ParseTransport(string reply) =>
        Lines(reply, "TRANSPORT")
            .Where(f => f.Length >= 5)
            .Select(f => new TransportState(Int(f[1]), Dbl(f[2]), f[3] == "1", f[4], f.Length >= 6 ? f[5] : ""))
            .FirstOrDefault();

    // TRACK \t index \t name \t flags \t volume \t pan \t …
    public static IReadOnlyList<ReaperTrack> ParseTracks(string reply) =>
        Lines(reply, "TRACK")
            .Where(f => f.Length >= 6)
            .Select(f => new ReaperTrack(Int(f[1]), f[2], Int(f[3]), Dbl(f[4]), Dbl(f[5])))
            .ToList();

    // MARKER \t name \t id \t position [\t color]
    // REGION \t name \t id \t start \t end [\t color]
    public static IReadOnlyList<ReaperMarker> ParseMarkers(string reply)
    {
        var result = new List<ReaperMarker>();
        foreach (var f in Lines(reply, "MARKER", "REGION"))
        {
            if (f[0] == "MARKER" && f.Length >= 4)
            {
                result.Add(new ReaperMarker(false, f[1], Int(f[2]), Dbl(f[3]), null,
                    f.Length >= 5 ? Int(f[4]) : null));
            }
            else if (f[0] == "REGION" && f.Length >= 5)
            {
                result.Add(new ReaperMarker(true, f[1], Int(f[2]), Dbl(f[3]), Dbl(f[4]),
                    f.Length >= 6 ? Int(f[5]) : null));
            }
        }

        return result;
    }

    /// <summary>
    /// Raw values for <paramref name="requested" />, keyed by <see cref="StateSource.Key" />.
    /// </summary>
    /// <remarks>
    /// GET/&lt;command_id&gt;            → CMDSTATE \t command_id \t state  (&gt;0 on, 0 off, -1 no state)
    /// GET/EXTSTATE/section/key  → EXTSTATE \t section \t key \t value
    /// Toggle states are matched by id; if REAPER spells an id differently but answered every toggle
    /// request, by position. An ext state without an answer counts as empty (never set).
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ParseStates(string reply, IReadOnlyList<StateSource> requested)
    {
        var cmdLines = Lines(reply, "CMDSTATE").Where(f => f.Length >= 3).ToList();
        var cmdById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cmdLines)
        {
            cmdById[f[1]] = f[2].Trim();
        }

        var extByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Lines(reply, "EXTSTATE").Where(f => f.Length >= 3))
        {
            extByKey[$"{f[1]}/{f[2]}"] = f.Length >= 4 ? f[3].Trim() : "";
        }

        var toggles = requested.OfType<CommandToggleState>().ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in requested)
        {
            switch (source)
            {
                case CommandToggleState t when cmdById.TryGetValue(t.CommandId, out var state):
                    result[t.Key] = state;
                    break;
                case CommandToggleState t when cmdLines.Count == toggles.Count:
                    result[t.Key] = cmdLines[toggles.IndexOf(t)][2].Trim();
                    break;
                case ExtStateValue e:
                    result[e.Key] = extByKey.GetValueOrDefault($"{e.Section}/{e.Name}", "");
                    break;
                // play state: 0 stopped, 1 playing, 2 paused, 5 recording, 6 record paused
                case TransportPlaying t when ParseTransport(reply) is { } transport:
                    result[t.Key] = transport.PlayState is 1 or 5 ? "1" : "0";
                    break;
                case TransportPaused t when ParseTransport(reply) is { } transport:
                    result[t.Key] = transport.PlayState is 2 or 6 ? "1" : "0";
                    break;
            }
        }

        return result;
    }

    private static IEnumerable<string[]> Lines(string reply, params string[] kinds) =>
        reply.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(f => kinds.Contains(f[0]));

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;

    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0;
}
