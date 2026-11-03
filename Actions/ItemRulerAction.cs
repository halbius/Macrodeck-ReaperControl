using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Item ruler": sets the item ruler display mode of the selected items (Item settings → Display item
/// ruler). The mode is chosen in the action's settings; the button shows the mode name and is lit while
/// the selected items use that mode.
/// </summary>
/// <remarks>
/// A per-item setting (ITEMRULER in the item chunk), so the actions have no toggle state: the state
/// comes from ReaperScripts/ReaperControl_ItemRulerState.lua via ExtState "ReaperControl"/"ItemRuler".
/// The ITEMRULER values do not follow the menu order - they were observed in REAPER.
/// </remarks>
public sealed class ItemRulerAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<ItemRulerAction> logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "item-ruler";

    private const string ModeParameter = "mode";

    /// <param name="Key">Stored parameter value and state id prefix - never rename.</param>
    private sealed record Mode(string Key, string Name, string CommandId, string ItemRulerValue);

    private static readonly IReadOnlyList<Mode> Modes =
    [
        new("item-time", "Item time", "42312", "1"),
        new("item-hmsf", "Item H:M:S:F", "42314", "3"),
        new("item-beats", "Item beats", "42315", "4"),
        new("item-beats-minimal", "Item beats, minimal", "42359", "5"),
        new("source-time", "Source time", "42313", "2"),
        new("source-time-hmsf", "Source time H:M:S:F", "42358", "6"),
    ];

    public string Id => ActionId;

    public LocalizedText Name => "Item ruler";

    public LocalizedText Description =>
        "Sets the item ruler display of the selected items. Lit while the selected items use this mode " +
        "(needs ReaperControl_ItemRulerState.lua running in REAPER).";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(ModeParameter,
            Modes.Select(m => new ActionParameterOption { Value = m.Key, Label = m.Name }).ToList(),
            "Ruler mode", defaultValue: Modes[0].Key, required: true)
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (Macro Deck offers "use provided states" only when the action is
        // added). The ids carry the mode, so choosing another mode brings that mode's label.
        var mode = ModeOf(parameters);
        var states = StatesFor(mode);

        var active = CommandStates.Snapshot(RuleFor(mode), connection, stateMonitor)?.ActiveStateId switch
        {
            CommandStates.On => $"{mode.Key}-on",
            CommandStates.Off => $"{mode.Key}-off",
            CommandStates.Unavailable => $"{mode.Key}-unavailable",
            _ => null
        };

        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(states, active));
    }

    public IActionExecutor CreateExecutor() => new Executor(connection, stateMonitor, logger);

    private static Mode ModeOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Modes.FirstOrDefault(m => m.Key == Values.Get(parameters, ModeParameter)) ?? Modes[0];

    private static StateRule RuleFor(Mode mode) =>
        StateRule.ExtStateEquals("ReaperControl", "ItemRuler", mode.ItemRulerValue);

    private static IReadOnlyList<ActionStateDefinition> StatesFor(Mode mode) =>
    [
        new($"{mode.Key}-on", $"{mode.Name} (active)")
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = mode.Name, BackgroundColor = "#2f855a", LabelColor = "#ffffff"
            }
        },
        new($"{mode.Key}-off", mode.Name)
        {
            DefaultAppearance = new ActionStateAppearance { Label = mode.Name }
        },
        new($"{mode.Key}-unavailable", $"{mode.Name} (unavailable)")
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = mode.Name, BackgroundColor = "#4a5568", LabelColor = "#cbd5e0"
            }
        }
    ];

    private sealed class Executor(ReaperConnection connection, CommandStateMonitor stateMonitor, ILogger logger)
        : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var mode = ModeOf(context.Parameters);
            return CommandStates.RunAsync(
                [mode.CommandId], RuleFor(mode), connection, stateMonitor, logger, context.CancellationToken);
        }
    }
}
