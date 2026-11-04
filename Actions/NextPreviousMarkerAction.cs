using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Go to next / previous marker": jumps to the marker right (next) or left (previous) of the
/// play/edit cursor. The label shows the direction and that marker's name, and follows the cursor.
/// </summary>
public sealed class NextPreviousMarkerAction(
    ReaperConnection connection,
    MarkerMonitor markers,
    MarkerFilterState filter,
    ILogger<NextPreviousMarkerAction> logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "go-to-next-previous-marker";

    private const string DirectionParameter = "direction";
    private const string FollowFilterParameter = "followFilter";
    private const string UseColorParameter = "useMarkerColor";

    public string Id => ActionId;

    public LocalizedText Name => "Go to next / previous marker";

    public LocalizedText Description =>
        "Jumps to the next or previous marker and shows its name. Optionally only markers matching the marker filter.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(DirectionParameter,
            [
                new ActionParameterOption { Value = "next", Label = "Next marker (right of the cursor)" },
                new ActionParameterOption { Value = "previous", Label = "Previous marker (left of the cursor)" }
            ],
            "Direction", defaultValue: "next", required: true),
        ActionParameter.Toggle(FollowFilterParameter, "Follow marker filter",
            "While a marker filter is active, skip markers that do not match it."),
        ActionParameter.Toggle(UseColorParameter, "Use marker color",
            "Button in the color of the marker it jumps to.", defaultValue: true)
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with a state (Macro Deck offers "use provided states" only when the action is added).
        var next = IsNext(parameters);
        var heading = next ? "Go to next" : "Go to previous";
        var snapshot = connection.Client is null ? null : markers.Get();
        var marker = snapshot is null ? null : Target(snapshot, next, parameters);

        string label;
        string? color = null;
        if (marker is null)
        {
            label = $"{heading}\n–";
        }
        else
        {
            var markerText = string.IsNullOrWhiteSpace(marker.Name)
                ? $"ID: {marker.Id.ToString(CultureInfo.InvariantCulture)}"
                : marker.Name;
            label = $"{heading}\n{markerText}";
            color = UsesMarkerColor(parameters) ? MarkerColors.ToHex(marker.Color) : null;
        }

        var background = color ?? "#2d3748";
        var stateId = TextStateIds.For(next ? "next" : "previous", $"{label}\u0001{color}");
        var state = new ActionStateDefinition(stateId, label)
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = label, BackgroundColor = background, LabelColor = MarkerColors.ReadableTextOn(background)
            }
        };

        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([state], stateId));
    }

    public IActionExecutor CreateExecutor() => new Executor(this, connection, markers, logger);

    private ReaperMarker? Target<T>(MarkerSnapshot snapshot, bool next, IReadOnlyDictionary<string, T> parameters)
    {
        var activeFilter = FollowsFilter(parameters) ? filter.Current : null;
        return next ? snapshot.Next(activeFilter) : snapshot.Previous(activeFilter);
    }

    private static bool IsNext<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, DirectionParameter) != "previous";

    private static bool FollowsFilter<T>(IReadOnlyDictionary<string, T> parameters) =>
        string.Equals(Values.Get(parameters, FollowFilterParameter), "true", StringComparison.OrdinalIgnoreCase);

    // Default on, also for buttons configured before the setting existed.
    private static bool UsesMarkerColor<T>(IReadOnlyDictionary<string, T> parameters) =>
        !string.Equals(Values.Get(parameters, UseColorParameter), "false", StringComparison.OrdinalIgnoreCase);

    private sealed class Executor(
        NextPreviousMarkerAction action,
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

            // The marker the label shows, so the press goes where the button says.
            var snapshot = markers.Get();
            var marker = snapshot is null ? null : action.Target(snapshot, IsNext(context.Parameters), context.Parameters);
            if (marker is null)
            {
                return ActionResult.Success(); // no marker in that direction
            }

            try
            {
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
