using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>One built-in REAPER action: fixed command id, no settings, on/off state on the button.</summary>
/// <param name="Id">Macro Deck action id. Persisted in profiles - never change after first use.</param>
/// <param name="Name">Name shown in Macro Deck's action picker. Can be changed any time.</param>
/// <param name="CommandId">REAPER command id (Action List → right click → Copy selected action command ID).</param>
/// <param name="State">Where the button's on/off state comes from. Null: the action's own toggle state.</param>
/// <param name="Description">Optional text in the action picker.</param>
public sealed record FixedCommand(
    string Id,
    string Name,
    string CommandId,
    StateRule? State = null,
    string? Description = null)
{
    public StateRule EffectiveState => State ?? StateRule.Toggle(CommandId);
}

/// <summary>
/// The fixed buttons. To add one: one line here - nothing else to change.
/// </summary>
public static class FixedCommands
{
    public static IReadOnlyList<FixedCommand> All { get; } =
    [
        // One line per fixed button, e.g.:
        // new("transport-play-stop", "Transport: Play/Stop", "40044"),
        // The item ruler modes moved to the "Item ruler" action (Actions/ItemRulerAction.cs).
    ];
}

/// <summary>Macro Deck action for one <see cref="FixedCommand" />.</summary>
public sealed class FixedCommandAction(
    FixedCommand command,
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    public string Id => command.Id;

    public LocalizedText Name => command.Name;

    public LocalizedText Description =>
        command.Description ?? $"Runs REAPER action {command.CommandId} and shows whether it is active.";

    public IReadOnlyList<ActionParameter> Parameters { get; } = [];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        Task.FromResult(CommandStates.Snapshot(command.EffectiveState, connection, stateMonitor));

    public IActionExecutor CreateExecutor() => new Executor(command, connection, stateMonitor, logger);

    private sealed class Executor(
        FixedCommand command,
        ReaperConnection connection,
        CommandStateMonitor stateMonitor,
        ILogger logger) : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context) =>
            CommandStates.RunAsync(
                [command.CommandId], command.EffectiveState,
                connection, stateMonitor, logger, context.CancellationToken);
    }
}
