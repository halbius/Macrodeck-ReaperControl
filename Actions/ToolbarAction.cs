using System.Text.RegularExpressions;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Toolbar": opens/closes one of REAPER's floating toolbars 1-32 - as a toolbar (optionally at the
/// mouse cursor) or as a menu. No on/off feedback; the button shows the toolbar's name.
/// </summary>
/// <remarks>
/// The name comes from reaper-menu.ini in the REAPER resource path ("title=" in the section
/// "[Floating toolbar &lt;n&gt;]"); without a name, or without the resource path, it is "Toolbar &lt;n&gt;".
/// </remarks>
public sealed class ToolbarAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<ToolbarAction> logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "toolbar";

    public const int Count = 32;

    private const string ToolbarParameter = "toolbar";
    private const string ModeParameter = "mode";
    private const string AtMouseParameter = "atMouse";

    private const string ModeToolbar = "toolbar";
    private const string ModeMenu = "menu";

    public string Id => ActionId;

    public LocalizedText Name => "Toolbar";

    public LocalizedText Description => "Opens or closes a REAPER toolbar, as a toolbar or as a menu.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(ToolbarParameter,
            Enumerable.Range(1, Count)
                .Select(n => new ActionParameterOption { Value = n.ToString(), Label = $"Toolbar {n}" })
                .ToList(),
            "Toolbar", defaultValue: "1", required: true),
        ActionParameter.Choice(ModeParameter,
            [
                new ActionParameterOption { Value = ModeToolbar, Label = "As toolbar" },
                new ActionParameterOption { Value = ModeMenu, Label = "As menu" }
            ],
            "Open", defaultValue: ModeToolbar, required: true),
        ActionParameter.Toggle(AtMouseParameter, "Open toolbar at mouse cursor", defaultValue: false)
            .OnlyWhen(ModeParameter, ModeToolbar)
    ];

    /// <summary>REAPER command id for toolbar <paramref name="n" /> (1-32) in the chosen way.</summary>
    /// <remarks>The ids are in blocks, as REAPER added toolbars over the versions.</remarks>
    internal static int CommandId(int n, string mode, bool atMouse)
    {
        if (mode == ModeMenu)
        {
            return 43401 + n - 1; // "Toolbars: Show toolbar n as menu"
        }

        if (atMouse) // "Toolbar: Open toolbar n at mouse cursor"
        {
            return n switch
            {
                <= 4 => 41111 + n - 1,
                <= 8 => 41655 + n - 5,
                <= 16 => 41960 + n - 9,
                _ => 42761 + n - 17
            };
        }

        return n switch // "Toolbar: Open/close toolbar n"
        {
            <= 8 => 41679 + n - 1,
            <= 16 => 41936 + n - 9,
            _ => 42713 + n - 17
        };
    }

    private static int ToolbarOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        int.TryParse(Values.Get(parameters, ToolbarParameter), out var n) && n is >= 1 and <= Count ? n : 1;

    private static string ModeOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, ModeParameter) == ModeMenu ? ModeMenu : ModeToolbar;

    private static bool AtMouse<T>(IReadOnlyDictionary<string, T> parameters) =>
        string.Equals(Values.Get(parameters, AtMouseParameter), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The toolbar's name as the button label: one state whose id is derived from the label, so a
    /// renamed or another toolbar brings its new label (Macro Deck applies default appearances only to
    /// new state ids).
    /// </summary>
    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var n = ToolbarOf(parameters);
        var label = ToolbarNames.Get(connection.Client?.Settings.ResourcePath, n) ?? $"Toolbar {n}";

        var hash = 2166136261u;
        foreach (var c in label)
        {
            hash = (hash ^ c) * 16777619u;
        }

        var stateId = $"tb{n}-{hash:x8}";
        var state = new ActionStateDefinition(stateId, label)
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = label, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
            }
        };
        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([state], stateId));
    }

    // Changes only when the toolbar is renamed in REAPER.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(30);

    public IActionExecutor CreateExecutor() => new Executor(connection, stateMonitor, logger);

    private sealed class Executor(ReaperConnection connection, CommandStateMonitor stateMonitor, ILogger logger)
        : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var parameters = context.Parameters;
            var id = CommandId(ToolbarOf(parameters), ModeOf(parameters), AtMouse(parameters));
            return CommandStates.RunAsync([id.ToString()], null, connection, stateMonitor, logger,
                context.CancellationToken);
        }
    }
}

/// <summary>Toolbar names from reaper-menu.ini, re-read when the file changed.</summary>
internal static partial class ToolbarNames
{
    private static readonly Lock Gate = new();
    private static string? _path;
    private static DateTime _writeTime;
    private static Dictionary<int, string> _names = [];

    public static string? Get(string? resourcePath, int toolbar)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        var path = Path.Combine(resourcePath, "reaper-menu.ini");
        lock (Gate)
        {
            try
            {
                var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                if (path != _path || time != _writeTime)
                {
                    _path = path;
                    _writeTime = time;
                    _names = time == default ? [] : Parse(File.ReadLines(path));
                }
            }
            catch (IOException)
            {
                // REAPER is writing the file: keep the names read last time.
            }

            return _names.GetValueOrDefault(toolbar);
        }
    }

    private static Dictionary<int, string> Parse(IEnumerable<string> lines)
    {
        var names = new Dictionary<int, string>();
        int? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var m = SectionPattern().Match(line);
                current = m.Success ? int.Parse(m.Groups[1].Value) : null;
            }
            else if (current is { } n && line.StartsWith("title=", StringComparison.OrdinalIgnoreCase)
                     && line[6..].Trim() is { Length: > 0 } title)
            {
                names[n] = title;
            }
        }

        return names;
    }

    [GeneratedRegex(@"^\[Floating toolbar (\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionPattern();
}
