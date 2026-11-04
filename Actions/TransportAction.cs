using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Transport": runs a transport command chosen in the action's settings. The button shows the
/// command name and is lit while the action's toggle state is on (as REAPER shows it in the action
/// list's "State" column).
/// </summary>
/// <remarks>
/// Also an icon provider: each command can name an icon from Assets/Icons (see IconLibrary) - one for
/// "off" and optionally another one for "on". With only one icon, "off" shows "<name>-dim".
/// </remarks>
public sealed class TransportAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<TransportAction> logger)
    : IActionDefinition, IStateProviderActionDefinition, IIconProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "transport";

    private const string CommandParameter = "command";

    /// <param name="Key">Stored parameter value and state id prefix - never rename.</param>
    /// <param name="Name">Shown in the picker and as button label - can be changed any time.</param>
    /// <param name="CommandId">REAPER command id.</param>
    /// <param name="Icon">Icon file name in Assets/Icons (without extension) for "off". Null: no icon.</param>
    /// <param name="IconWhenOn">Icon for "on". Null: <paramref name="Icon" /> in full, and dimmed for "off".</param>
    /// <param name="State">Where "on" comes from. Null: the action's own toggle state.</param>
    private sealed record Command(
        string Key,
        string Name,
        string CommandId,
        string? Icon = null,
        string? IconWhenOn = null,
        StateRule? State = null);

    // On = REAPER is playing or recording (the play actions themselves have no on/off state).
    private static readonly StateRule Playing = new(new TransportPlaying(), "1");

    // On = REAPER is paused (not while playing or stopped).
    private static readonly StateRule Paused = new(new TransportPaused(), "1");

    // To add a command: one line here. Icons: put e.g. record.png (+ record-dim.png) into Assets/Icons and write "record".
    private static readonly IReadOnlyList<Command> Commands =
    [
        // Play buttons show what a press does: "play" while stopped, "stop"/"pause" while playing -
        // swap the two names for the other way round.
        new("play-stop", "Play/Stop", "40044", Icon: "play", IconWhenOn: "stop", State: Playing),
        new("play-stop-move-editcursor", "Play/Stop, Move Edit Cursor", "40328",
            Icon: "play", IconWhenOn: "stop", State: Playing),
        // Lit (full icon) only while paused; dimmed while playing or stopped.
        new("pause", "Pause", "1008", Icon: "pause", State: Paused),
        new("repeat", "Repeat", "1068", Icon: "repeat"),
        new("stop-at-loop-end", "Stop at loop end", "41834"),
        // Same icon either way (IconWhenOn = Icon: never dimmed); lit while playing.
        new("play-skip-selection", "Play, Skip Timeline Selection", "40317",
            Icon: "play", IconWhenOn: "play", State: Playing),
    ];

    public string Id => ActionId;

    public LocalizedText Name => "Transport";

    public LocalizedText Description =>
        "Runs a REAPER transport command and shows whether it is on.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(CommandParameter,
            Commands.Select(c => new ActionParameterOption { Value = c.Key, Label = c.Name }).ToList(),
            "Command", defaultValue: Commands[0].Key, required: true)
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (Macro Deck offers "use provided states" only when the action is
        // added). The ids carry the command, so choosing another command brings its label.
        var command = CommandOf(parameters);
        var active = CommandStates.Snapshot(RuleFor(command), connection, stateMonitor)?.ActiveStateId;

        return Task.FromResult<ActionStateSnapshot?>(
            new ActionStateSnapshot(StatesFor(command), active is null ? null : StateId(command, active)));
    }

    public IActionExecutor CreateExecutor() => new Executor(connection, stateMonitor, logger);

    private static Command CommandOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Commands.FirstOrDefault(c => c.Key == Values.Get(parameters, CommandParameter)) ?? Commands[0];

    // The action's own toggle state unless the command names another source.
    private static StateRule RuleFor(Command command) => command.State ?? StateRule.Toggle(command.CommandId);

    public TimeSpan IconPollInterval => TimeSpan.FromSeconds(1);

    /// <summary>Icon for the current state, from the monitor's cache - never talks to REAPER.</summary>
    public Task<ActionIconSnapshot?> GetActionIconAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var command = CommandOf(parameters);
        if (command.Icon is null)
        {
            return Task.FromResult<ActionIconSnapshot?>(null);
        }

        var on = CommandStates.Snapshot(RuleFor(command), connection, stateMonitor)?.ActiveStateId == CommandStates.On;
        var snapshot = on
            ? IconLibrary.Snapshot(command.IconWhenOn ?? command.Icon, dimmed: false)
            : IconLibrary.Snapshot(command.Icon, dimmed: command.IconWhenOn is null);

        return Task.FromResult(snapshot);
    }

    public Task<ActionIconContent?> GetActionIconContentAsync(
        IReadOnlyDictionary<string, object?> parameters, string version, CancellationToken cancellationToken) =>
        Task.FromResult(IconLibrary.Content(version));

    /// <summary>"on" / "off" / "unavailable" → this command's own state id.</summary>
    private static string StateId(Command command, string state) => $"{command.Key}-{state}";

    private static IReadOnlyList<ActionStateDefinition> StatesFor(Command command) =>
    [
        new(StateId(command, CommandStates.On), $"{command.Name} (on)")
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = command.Name, BackgroundColor = "#2f855a", LabelColor = "#ffffff"
            }
        },
        new(StateId(command, CommandStates.Off), command.Name)
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = command.Name, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
            }
        },
        new(StateId(command, CommandStates.Unavailable), $"{command.Name} (unavailable)")
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = command.Name, BackgroundColor = "#4a5568", LabelColor = "#cbd5e0"
            }
        }
    ];

    private sealed class Executor(ReaperConnection connection, CommandStateMonitor stateMonitor, ILogger logger)
        : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var command = CommandOf(context.Parameters);
            var result = await CommandStates.RunAsync(
                [command.CommandId], RuleFor(command), connection, stateMonitor, logger, context.CancellationToken);

            // RunAsync names the expected state "on"/"off"; this action's states carry the command key.
            return result is { Status: ActionResultStatus.Succeeded, ExpectedStateId: { } expected }
                ? ActionResult.Success(StateId(command, expected))
                : result;
        }
    }
}
