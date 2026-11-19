using System.Text.RegularExpressions;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Item: Open in editor": opens the selected items (or copies of them) in REAPER's primary or
/// secondary external editor. No on/off feedback; the label names the editor, e.g. "Open items in RX11".
/// </summary>
/// <remarks>
/// The editors come from reaper.ini in the REAPER resource path, section [extedit], key __default__:
/// "&lt;primary path&gt;|&lt;secondary path&gt;" (Preferences → External Editors). The name is the program's file
/// name, shortened by <see cref="EditorNames.Rules" />; the field "Editor name" overrides it.
/// </remarks>
public sealed class ItemEditorAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<ItemEditorAction> logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "item-open-in-editor";

    private const string EditorParameter = "editor";
    private const string OpenParameter = "open";
    private const string NameParameter = "editorName";

    public string Id => ActionId;

    public LocalizedText Name => "Item: Open in editor";

    public LocalizedText Description =>
        "Opens the selected items or copies of them in REAPER's primary or secondary external editor.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(EditorParameter,
            [
                new ActionParameterOption { Value = "primary", Label = "Primary editor" },
                new ActionParameterOption { Value = "secondary", Label = "Secondary editor" }
            ],
            "Editor", defaultValue: "primary", required: true),
        ActionParameter.Choice(OpenParameter,
            [
                new ActionParameterOption { Value = "items", Label = "Items" },
                new ActionParameterOption { Value = "copies", Label = "Item copies" }
            ],
            "Open", defaultValue: "items", required: true),
        ActionParameter.Text(NameParameter, "Editor name",
            "Leave empty to use the name from REAPER's preferences (needs the resource path in the plugin settings).")
    ];

    // REAPER command ids: (secondary?, copies?).
    private static string CommandId(bool secondary, bool copies) => (secondary, copies) switch
    {
        (false, false) => "40109", // Item: Open items in primary external editor
        (true, false) => "40202",  // Item: Open items in secondary external editor
        (false, true) => "40132",  // Item: Open item copies in primary external editor
        (true, true) => "40203",   // Item: Open item copies in secondary external editor
    };

    private static bool Secondary<T>(IReadOnlyDictionary<string, T> p) => Values.Get(p, EditorParameter) == "secondary";

    private static bool Copies<T>(IReadOnlyDictionary<string, T> p) => Values.Get(p, OpenParameter) == "copies";

    private string Label<T>(IReadOnlyDictionary<string, T> p)
    {
        var secondary = Secondary(p);
        var editor = Values.Get(p, NameParameter)
                     ?? EditorNames.Get(connection.Client?.Settings.ResourcePath, secondary)
                     ?? (secondary ? "secondary editor" : "primary editor");
        return (Copies(p) ? "Open item copies in " : "Open items in ") + editor;
    }

    /// <summary>
    /// The label as one state whose id is derived from it, so a changed editor or name brings the new
    /// label (Macro Deck applies default appearances only to new state ids).
    /// </summary>
    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var label = Label(parameters);
        var hash = 2166136261u;
        foreach (var c in label)
        {
            hash = (hash ^ c) * 16777619u;
        }

        var stateId = $"editor-{hash:x8}";
        var state = new ActionStateDefinition(stateId, label)
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = label, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
            }
        };
        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([state], stateId));
    }

    // Changes only when the editors are changed in REAPER's preferences.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(30);

    public IActionExecutor CreateExecutor() => new Executor(connection, stateMonitor, logger);

    private sealed class Executor(ReaperConnection connection, CommandStateMonitor stateMonitor, ILogger logger)
        : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context) =>
            CommandStates.RunAsync(
                [CommandId(Secondary(context.Parameters), Copies(context.Parameters))], null,
                connection, stateMonitor, logger, context.CancellationToken);
    }
}

/// <summary>Names of REAPER's external editors from reaper.ini, re-read when the file changed.</summary>
internal static partial class EditorNames
{
    /// <summary>
    /// Shorten the program's file name to a button label, applied in this order. To change a name:
    /// add or edit a rule here (or set "Editor name" on the button).
    /// "iZotope RX 11 Audio Editor" → "RX11", "Sequoia_x64" → "Sequoia".
    /// </summary>
    internal static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"[ _-]*\(?(x64|x86|win64|win32|64[ -]?bit)\)?$", RegexOptions.IgnoreCase), ""),
        (new Regex(@"^iZotope\s+", RegexOptions.IgnoreCase), ""),
        (new Regex(@"\s+Audio Editor$", RegexOptions.IgnoreCase), ""),
        (new Regex(@"^RX\s+(\d+)", RegexOptions.IgnoreCase), "RX$1"),
    ];

    private static readonly Lock Gate = new();
    private static string? _path;
    private static DateTime _writeTime;
    private static string?[] _names = [null, null];

    /// <summary>Short name of the primary or secondary editor, or null when not known.</summary>
    public static string? Get(string? resourcePath, bool secondary)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        var path = Path.Combine(resourcePath, "reaper.ini");
        lock (Gate)
        {
            try
            {
                var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                if (path != _path || time != _writeTime)
                {
                    _path = path;
                    _writeTime = time;
                    _names = time == default ? [null, null] : Parse(File.ReadLines(path));
                }
            }
            catch (IOException)
            {
                // REAPER is writing the file: keep the names read last time.
            }

            return _names[secondary ? 1 : 0];
        }
    }

    /// <summary>[extedit] __default__=&lt;primary&gt;|&lt;secondary&gt;.</summary>
    private static string?[] Parse(IEnumerable<string> lines)
    {
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[extedit]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && line.StartsWith("__default__=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line["__default__=".Length..].Split('|');
                return [ShortName(parts.ElementAtOrDefault(0)), ShortName(parts.ElementAtOrDefault(1))];
            }
        }

        return [null, null];
    }

    /// <summary>Program file name (Windows or macOS path) without extension, shortened by <see cref="Rules" />.</summary>
    internal static string? ShortName(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return null;
        }

        var file = programPath.Trim().TrimEnd('/', '\\');
        file = file[(file.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = file.LastIndexOf('.');
        if (dot > 0)
        {
            file = file[..dot]; // .exe, .app
        }

        foreach (var (pattern, replacement) in Rules)
        {
            file = pattern.Replace(file, replacement).Trim();
        }

        return file.Length > 0 ? file : null;
    }
}
