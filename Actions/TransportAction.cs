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
public sealed class TransportAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<TransportAction> logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "transport";

    private const string CommandParameter = "command";

    /// <param name="Key">Stored parameter value and state id prefix - never rename.</param>
    /// <param name="Name">Shown in the picker and as button label - can be changed any time.</param>
    /// <param name="CommandId">REAPER command id.</param>
    private sealed record Command(string Key, string Name, string CommandId);

    // To add a command: one line here.
    private static readonly IReadOnlyList<Command> Commands =
    [
        new("play-stop", "Play/Stop", "40044"),
        new("play-stop-move-editcursor", "Play/Stop, Move Edit Cursor", "40328"),
        new("play-pause", "Play/Pause", "40073"),
        new("repeat", "Repeat", "1068"),
        new("stop-at-loop-end", "Stop at loop end", "41834"),
        new("play-skip-selection", "Play, Skip Timeline Selection", "40317"),


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

    // The action's own toggle state - no script needed.
    private static StateRule RuleFor(Command command) => StateRule.Toggle(command.CommandId);

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
