using System.Text.RegularExpressions;

namespace ReaperControl.Reaper;

public sealed record ReaperActionInfo(string CommandId, string Name, string Kind);

/// <summary>
/// Reads custom actions and ReaScripts from reaper-kb.ini so they can be picked by name.
/// Native actions are not in that file; they are typed as numeric id (e.g. 40044) or come
/// from a list of well-known ones below.
/// </summary>
/// <remarks>
/// Relevant reaper-kb.ini lines (section 0 = main action list):
/// <code>
/// ACT &lt;flags&gt; &lt;section&gt; "&lt;id&gt;" "Custom: name" 40001 40002 …   → command "_&lt;id&gt;"
/// SCR &lt;flags&gt; &lt;section&gt; &lt;id&gt; "Custom: script.lua" "path"     → command "_&lt;id&gt;"
/// </code>
/// </remarks>
public sealed partial class ReaperActionCatalog
{
    private readonly object _gate = new();
    private string? _path;
    private DateTime _loadedWriteTime;
    private IReadOnlyList<ReaperActionInfo> _cache = [];

    // A few native main-section actions so the picker is useful without any import.
    private static readonly ReaperActionInfo[] WellKnown =
    [
        new("40044", "Transport: Play/stop", "Native"),
        new("1007", "Transport: Play", "Native"),
        new("1016", "Transport: Stop", "Native"),
        new("1008", "Transport: Pause", "Native"),
        new("1013", "Transport: Record", "Native"),
        new("40042", "Transport: Go to start of project", "Native"),
        new("40043", "Transport: Go to end of project", "Native"),
        new("40157", "Markers: Insert marker at current position", "Native"),
        new("40326", "View: Show region/marker manager window", "Native"),
        new("40906", "View: Show track manager window", "Native"),
        new("40026", "File: Save project", "Native"),
        new("40029", "Edit: Undo", "Native"),
        new("40030", "Edit: Redo", "Native"),
    ];

    public void SetSource(string? keyBindingsFile)
    {
        lock (_gate)
        {
            _path = keyBindingsFile;
            _loadedWriteTime = default;
            _cache = [];
        }
    }

    /// <summary>All known actions; re-reads reaper-kb.ini when it changed on disk.</summary>
    public IReadOnlyList<ReaperActionInfo> GetAll()
    {
        lock (_gate)
        {
            if (_path is not null && File.Exists(_path))
            {
                var writeTime = File.GetLastWriteTimeUtc(_path);
                if (writeTime != _loadedWriteTime)
                {
                    _cache = Parse(File.ReadLines(_path));
                    _loadedWriteTime = writeTime;
                }
            }

            return [.. _cache, .. WellKnown];
        }
    }

    /// <summary>
    /// Command id of a ReaScript loaded into REAPER's action list, found by its file name
    /// (the action list names scripts "Custom: &lt;file name&gt;"). Null if it is not loaded.
    /// </summary>
    public string? FindScriptCommandId(string scriptFileName) =>
        GetAll()
            .FirstOrDefault(a => a.Kind == "Script" &&
                                 a.Name.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase))
            ?.CommandId;

    public IEnumerable<ReaperActionInfo> Search(string? filter)
    {
        var all = GetAll();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return all;
        }

        return all.Where(a =>
            a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            a.CommandId.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<ReaperActionInfo> Parse(IEnumerable<string> lines)
    {
        var result = new List<ReaperActionInfo>();
        foreach (var line in lines)
        {
            var m = KbLine().Match(line);
            if (!m.Success || m.Groups["section"].Value != "0")
            {
                continue;
            }

            var kind = m.Groups["kind"].Value == "SCR" ? "Script" : "Custom action";
            var id = m.Groups["id"].Value.Trim('"');
            var name = m.Groups["name"].Value;
            result.Add(new ReaperActionInfo("_" + id, name, kind));
        }

        return result;
    }

    [GeneratedRegex("""^(?<kind>ACT|SCR)\s+\d+\s+(?<section>\d+)\s+(?<id>"[^"]+"|\S+)\s+"(?<name>[^"]*)""")]
    private static partial Regex KbLine();
}
