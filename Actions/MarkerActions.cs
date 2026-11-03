using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>Marker colors from REAPER's web interface (0x01rrggbb; 0 = no custom color).</summary>
internal static class MarkerColors
{
    /// <summary>"#rrggbb", or null when the marker has no custom color.</summary>
    public static string? ToHex(int? color) =>
        color is { } c && c != 0 ? $"#{c & 0xFFFFFF:x6}" : null;

    /// <summary>The color at 40 % brightness, for markers that are not the current one.</summary>
    public static string Dim(string hex)
    {
        var (r, g, b) = Parse(hex);
        return $"#{(int)(r * 0.4):x2}{(int)(g * 0.4):x2}{(int)(b * 0.4):x2}";
    }

    /// <summary>Black or white, whichever reads better on <paramref name="hex" />.</summary>
    public static string ReadableTextOn(string hex)
    {
        var (r, g, b) = Parse(hex);
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        return luminance > 0.6 ? "#000000" : "#ffffff";
    }

    private static (int R, int G, int B) Parse(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }
}

/// <summary>Stable, kebab-case state ids derived from text (state ids are persisted in profiles).</summary>
internal static class TextStateIds
{
    /// <summary><paramref name="prefix" /> + "-" + FNV-1a hash of <paramref name="text" />.</summary>
    public static string For(string prefix, string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return $"{prefix}-{hash:x8}";
    }
}

/// <summary>
/// "Go to marker": jumps to a marker and carries "&lt;id&gt;: &lt;name&gt;" as its label; lit while the
/// play/edit cursor is at or after that marker and before the next one.
/// </summary>
/// <remarks>
/// With "Follow marker filter" off: always the marker with this ID. On: without an active filter
/// still the marker with this ID; with a filter, number n is the n-th matching marker in time order -
/// meant for a folder full of these, numbered 1, 2, 3, ...
/// The label works through button states: every marker gets its own state ids, whose default
/// label is its name. Macro Deck adopts new states with their default appearance, also at runtime,
/// so the label follows when the filter or the markers change.
/// </remarks>
public sealed class MarkerButtonAction(
    ReaperConnection connection,
    MarkerMonitor markers,
    MarkerFilterState filter,
    ILogger logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "go-to-marker";

    private const string NumberParameter = "number";
    private const string FollowFilterParameter = "followFilter";
    private const string UseColorParameter = "useMarkerColor";
    private const string EmptyStateId = "marker-empty";

    public string Id => ActionId;

    public LocalizedText Name => "Go to marker";

    public LocalizedText Description =>
        "Jumps to a marker and shows its ID and name. Optionally follows the marker filter, " +
        "for a folder of marker buttons numbered 1, 2, 3, ...";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Number(NumberParameter, "Marker ID / slot",
            "Marker ID. With \"Follow marker filter\" and an active filter: position in the filtered list (1 = first).",
            min: 1, step: 1, defaultValue: 1, required: true),
        ActionParameter.Toggle(FollowFilterParameter, "Follow marker filter",
            "While a marker filter is active, show the n-th matching marker instead of the marker with this ID."),
        ActionParameter.Toggle(UseColorParameter, "Use marker color",
            "Button in the marker's color: full color for the current marker, dimmed for the others. " +
            "Markers without their own color look as if this were off.",
            defaultValue: true)
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states, even for an unfinished draft: Macro Deck offers "use provided
        // states" only when the action is added, and only if states come back then.
        var snapshot = connection.Client is null ? null : markers.Get();
        var marker = snapshot is null ? null : Resolve(NumberOf(parameters), FollowsFilter(parameters), snapshot);
        if (marker is null)
        {
            return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(
                [
                    new ActionStateDefinition(EmptyStateId, "No marker")
                    {
                        DefaultAppearance = new ActionStateAppearance { Label = "", BackgroundColor = "#2d3748" }
                    }
                ],
                EmptyStateId));
        }

        // "<name>" + line break + "ID: <id>", or just "ID: <id>" for an unnamed marker.
        var idLine = $"ID: {marker.Id.ToString(CultureInfo.InvariantCulture)}";
        var label = string.IsNullOrWhiteSpace(marker.Name) ? idLine : $"{marker.Name}\n{idLine}";
        var color = UsesMarkerColor(parameters) ? MarkerColors.ToHex(marker.Color) : null;

        // The ids carry label and color: a new name or color is a new state, which Macro Deck adopts
        // with its default appearance - that is how label and color follow REAPER.
        // "v2": new ids for all markers, so buttons pick up the corrected default colors.
        var baseId = TextStateIds.For("marker", $"v2\u0001{marker.Id}\u0001{label}\u0001{color}");
        var onId = baseId + "-on";
        var offId = baseId + "-off";

        IReadOnlyList<ActionStateDefinition> states;
        if (color is null)
        {
            states =
            [
                new(onId, $"{label} (current)")
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = "#2f855a", LabelColor = "#ffffff"
                    }
                },
                // Explicit colors: a state without a background color gets one filled in by Macro Deck,
                // which made not-current markers green.
                new(offId, label)
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
                    }
                }
            ];
        }
        else
        {
            states =
            [
                new(onId, $"{label} (current)")
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = color, LabelColor = MarkerColors.ReadableTextOn(color)
                    }
                },
                new(offId, label)
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label,
                        BackgroundColor = MarkerColors.Dim(color),
                        LabelColor = MarkerColors.ReadableTextOn(MarkerColors.Dim(color))
                    }
                }
            ];
        }

        return Task.FromResult<ActionStateSnapshot?>(
            new ActionStateSnapshot(states, snapshot!.CurrentMarkerId == marker.Id ? onId : offId));
    }

    public IActionExecutor CreateExecutor() => new Executor(this, connection, markers, logger);

    private string? ActiveFilterText => filter.Current?.Text;

    // Default on: buttons configured before this setting existed have no value and use the color.
    private static bool UsesMarkerColor<T>(IReadOnlyDictionary<string, T> parameters) =>
        !string.Equals(Values.Get(parameters, UseColorParameter), "false", StringComparison.OrdinalIgnoreCase);

    private static bool FollowsFilter<T>(IReadOnlyDictionary<string, T> parameters) =>
        string.Equals(Values.Get(parameters, FollowFilterParameter), "true", StringComparison.OrdinalIgnoreCase);

    private ReaperMarker? Resolve(int? number, bool followsFilter, MarkerSnapshot snapshot)
    {
        if (number is not { } n || n < 1)
        {
            return null;
        }

        if (followsFilter && filter.Current is { } active)
        {
            return snapshot.Markers.Where(active.Matches).ElementAtOrDefault(n - 1);
        }

        return snapshot.Markers.FirstOrDefault(m => m.Id == n);
    }

    private static int? NumberOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, NumberParameter) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value)
            : null;

    private sealed class Executor(
        MarkerButtonAction action,
        ReaperConnection connection,
        MarkerMonitor markers,
        ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var client = connection.Client;
            if (client is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotConfigured, "REAPER connection is not set up yet.");
            }

            // Resolve against the markers the button shows, so the press goes where the label says.
            var snapshot = markers.Get();
            var marker = snapshot is null ? null : action.Resolve(NumberOf(context.Parameters), FollowsFilter(context.Parameters), snapshot);
            if (marker is null)
            {
                logger.LogInformation(
                    "Marker button: no marker for number '{Number}' ({Known} markers known, filter {Filter})",
                    Values.Get(context.Parameters, NumberParameter) ?? "none",
                    snapshot?.Markers.Count.ToString() ?? "not loaded yet",
                    action.ActiveFilterText ?? "none");
                return ActionResult.Success(); // empty slot: nothing to do
            }

            try
            {
                // "m<id>" = marker with this ID (web interface position string).
                await client.SendAsync([$"SET/POS_STR/m{marker.Id}"], context.CancellationToken);
                markers.RequestRefresh();
                return ActionResult.Success();
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return ActionResult.Failed(ActionErrorCodes.Timeout, "REAPER did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Jumping to marker {Id} failed", marker.Id);
                return ActionResult.Failed(ActionErrorCodes.NotConnected,
                    "REAPER is not reachable. Is it running with the web interface enabled?");
            }
        }
    }
}

/// <summary>
/// "Marker filter": activates a filter for the marker buttons (press again to clear it). Shows the
/// filter text as label, lit while the filter is active. Only one filter is active at a time.
/// </summary>
public sealed class MarkerFilterAction(MarkerFilterState filter, MarkerMonitor markers)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "marker-filter";

    private const string TextParameter = "text";
    private const string MatchParameter = "match";

    public string Id => ActionId;

    public LocalizedText Name => "Marker filter";

    public LocalizedText Description =>
        "Shows only matching markers on the marker buttons. Press again to show all markers.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Text(TextParameter, "Filter text", "E.g. #S or Jingle. Not case-sensitive.",
            placeholder: "#S", required: true),
        ActionParameter.Choice(MatchParameter,
            [
                new ActionParameterOption { Value = "contains", Label = "Name contains the text" },
                new ActionParameterOption { Value = "starts-with", Label = "Name starts with the text" }
            ],
            "Match", defaultValue: "contains")
    ];

    // The filter only changes through these buttons, which notify right away.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(5);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (see MarkerButtonAction): a placeholder until a text is entered.
        var own = FilterOf(parameters);
        var label = own?.Text ?? "Filter";
        var baseId = TextStateIds.For("filter", own is null ? "" : $"{own.Match}\u0001{own.Text}");
        var onId = baseId + "-on";
        var offId = baseId + "-off";

        IReadOnlyList<ActionStateDefinition> states =
        [
            new(onId, $"{label} (active)")
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = label, BackgroundColor = "#2b6cb0", LabelColor = "#ffffff"
                }
            },
            new(offId, label)
            {
                DefaultAppearance = new ActionStateAppearance { Label = label }
            }
        ];

        var active = own is not null && own.Equals(filter.Current);
        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(states, active ? onId : offId));
    }

    public IActionExecutor CreateExecutor() => new Executor(filter, markers);

    private static MarkerFilter? FilterOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var text = Values.Get(parameters, TextParameter);
        if (text is null)
        {
            return null;
        }

        var match = Values.Get(parameters, MatchParameter) == "starts-with"
            ? MarkerMatch.StartsWith
            : MarkerMatch.Contains;
        return new MarkerFilter(text, match);
    }

    private sealed class Executor(MarkerFilterState filter, MarkerMonitor markers) : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var own = FilterOf(context.Parameters);
            if (own is null)
            {
                return Task.FromResult(ActionResult.Failed(ActionErrorCodes.InvalidParameter, "No filter text set."));
            }

            var now = filter.Toggle(own);
            // Marker buttons and the other filter buttons change with it.
            markers.NotifyButtonsChanged();

            var baseId = TextStateIds.For("filter", $"{own.Match}\u0001{own.Text}");
            return Task.FromResult(ActionResult.Success(baseId + (now is null ? "-off" : "-on")));
        }
    }
}
