using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Run REAPER command": one button runs one or more REAPER actions - native (40044),
/// custom actions and ReaScripts (_RS…, _&lt;hash&gt;), extension actions (_SWS_…).
/// Several commands are separated by ';' and sent in one request, executed in order.
/// </summary>
/// <remarks>
/// Also a button state provider: the button shows whether an action is on or off - the same
/// state REAPER shows as a checkmark in its menus. By default that is the first command; a
/// different one can be set under "Show state of".
/// </remarks>
public sealed class RunCommandAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<RunCommandAction> logger)
    : IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "run-command";

    private const string CommandParameter = "command";
    private const string StateCommandParameter = "stateCommand";
    private const int MaxOptions = 200;

    public string Id => ActionId;

    public LocalizedText Name => "Run REAPER command";

    public LocalizedText Description =>
        "Runs a REAPER action, custom action or script by command id. Several ids separated by ';'. " +
        "Can show the action's on/off state on the button.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice(CommandParameter, "Command",
                "Pick from the list or type a command id, e.g. 40044 or _RS7d3c…; several separated by ';'.",
                placeholder: "40044", required: true)
            .AsSearchable(),
        ActionParameter.Text(StateCommandParameter, "Show state of",
            "Command id whose on/off state the button shows. Empty: the first command above.",
            placeholder: "first command")
    ];

    // The host clamps this to at least 1 s; the monitor's state.update makes changes show sooner.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<DynamicOptionsResult> GetDynamicOptionsAsync(
        DynamicOptionsContext context, CancellationToken cancellationToken)
    {
        var options = connection.Catalog.Search(context.Filter)
            .Take(MaxOptions)
            .Select(a => new ActionParameterOption
            {
                Value = a.CommandId,
                Label = $"{a.Name}  ({a.CommandId})",
                Metadata = new Dictionary<string, string> { ["kind"] = a.Kind }
            })
            .ToList();

        return Task.FromResult(new DynamicOptionsResult
        {
            Options = options,
            // Native ids, SWS ids and command chains are typed in freely.
            AllowsCustomValue = true,
            CacheSeconds = 30
        });
    }

    // Also called with half-typed drafts while the button is being edited: no value is no error.
    // Macro Deck offers "use provided states" only when the action is added - before a command is
    // entered - and only if states come back then; so a missing command still reports the state set.
    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        Task.FromResult(StateRuleOf(parameters) is { } rule
            ? CommandStates.Snapshot(rule, connection, stateMonitor)
            : new ActionStateSnapshot(CommandStates.Definitions, null));

    public IActionExecutor CreateExecutor() => new Executor(connection, stateMonitor, logger);

    private static StateRule? StateRuleOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        StateCommandOf(parameters) is { } id ? StateRule.Toggle(id) : null;

    /// <summary>The "Show state of" id, or else the first command.</summary>
    private static string? StateCommandOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var explicitId = Values.Get(parameters, StateCommandParameter);
        if (explicitId is not null)
        {
            return explicitId;
        }

        var commandList = Values.Get(parameters, CommandParameter);
        return commandList is null ? null : ReaperWebClient.SplitCommandList(commandList).FirstOrDefault();
    }

    private sealed class Executor(ReaperConnection connection, CommandStateMonitor stateMonitor, ILogger logger)
        : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var commandList = Values.Get(context.Parameters, CommandParameter);
            if (commandList is null)
            {
                return Task.FromResult(
                    ActionResult.Failed(ActionErrorCodes.InvalidParameter, "No REAPER command set."));
            }

            return CommandStates.RunAsync(
                ReaperWebClient.SplitCommandList(commandList), StateRuleOf(context.Parameters),
                connection, stateMonitor, logger, context.CancellationToken);
        }
    }
}
