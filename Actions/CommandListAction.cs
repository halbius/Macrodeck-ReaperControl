using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>One entry of a <see cref="CommandListAction" />.</summary>
/// <param name="Key">Stored parameter value and state id prefix - never rename after first use.</param>
/// <param name="Name">Shown in the picker and as button label - can be changed any time.</param>
/// <param name="CommandId">REAPER command id (Action List → right click → Copy selected action command ID).</param>
/// <param name="Icon">Icon file name in Assets/Icons (without extension) for "off". Null: no icon.</param>
/// <param name="IconWhenOn">Icon for "on". Null: <paramref name="Icon" /> in full, "&lt;Icon&gt;-dim" for "off".</param>
/// <param name="State">Where "on" comes from. Null: the REAPER action's own toggle state.</param>
/// <param name="OnColor">Button color while on ("#rrggbb"). Null: green.</param>
/// <param name="OffColor">Button color while off. Null: dark grey.</param>
public sealed record ListCommand(
    string Key,
    string Name,
    string CommandId,
    string? Icon = null,
    string? IconWhenOn = null,
    StateRule? State = null,
    string? OnColor = null,
    string? OffColor = null);

/// <summary>
/// Base for actions that offer a list of REAPER commands to choose from in the button's settings
/// (Transport, Item, ...). A subclass only names itself and lists its commands - everything else
/// (running, on/off state, colors, icons, "Show text") lives here.
/// </summary>
/// <remarks>
/// Where "on" comes from is per command (<see cref="ListCommand.State" />):
/// <list type="bullet">
/// <item>nothing: the REAPER action's own toggle state ("State" column of the action list),</item>
/// <item>a transport state (playing, paused) - see TransportAction,</item>
/// <item>a value a background Lua script publishes, e.g. a setting of the selected items - see ItemAction.</item>
/// </list>
/// </remarks>
public abstract class CommandListAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger logger)
    : IActionDefinition, IStateProviderActionDefinition, IIconProviderActionDefinition
{
    private const string CommandParameter = "command";
    private const string ShowLabelParameter = "showLabel";

    /// <summary>Persisted in users' profiles - never rename after release.</summary>
    public abstract string Id { get; }

    public abstract LocalizedText Name { get; }

    public abstract LocalizedText Description { get; }

    /// <summary>The commands to choose from. The first one is the default.</summary>
    protected abstract IReadOnlyList<ListCommand> Commands { get; }

    private IReadOnlyList<ActionParameter>? _parameters;

    // Built on first use: Commands comes from the subclass.
    public IReadOnlyList<ActionParameter> Parameters => _parameters ??=
    [
        ActionParameter.Choice(CommandParameter,
            Commands.Select(c => new ActionParameterOption { Value = c.Key, Label = c.Name }).ToList(),
            "Command", defaultValue: Commands[0].Key, required: true),
        ActionParameter.Toggle(ShowLabelParameter, "Show text",
            "Show the command name on the button. Turn off for icon-only buttons.", defaultValue: true)
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public TimeSpan IconPollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (Macro Deck offers "use provided states" only when the action is
        // added). The ids carry the command, so choosing another command brings its label.
        var command = CommandOf(parameters);
        var showLabel = ShowsLabel(parameters);
        var active = CommandStates.Snapshot(RuleFor(command), connection, stateMonitor)?.ActiveStateId;

        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(
            StatesFor(command, showLabel), active is null ? null : StateId(command, active, showLabel)));
    }

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

    public IActionExecutor CreateExecutor() => new Executor(this, connection, stateMonitor, logger);

    private ListCommand CommandOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Commands.FirstOrDefault(c => c.Key == Values.Get(parameters, CommandParameter)) ?? Commands[0];

    private static StateRule RuleFor(ListCommand command) => command.State ?? StateRule.Toggle(command.CommandId);

    // Default on, also for buttons configured before the setting existed.
    private static bool ShowsLabel<T>(IReadOnlyDictionary<string, T> parameters) =>
        !string.Equals(Values.Get(parameters, ShowLabelParameter), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "on" / "off" / "unavailable" → this command's own state id. Icon-only buttons and own colors get
    /// their own ids, so switching "Show text" or changing a color reaches existing buttons.
    /// </summary>
    /// <remarks>
    /// Kept short on purpose: when an action also provides the icon, Macro Deck's button editor puts the
    /// state id twice into an internal node id limited to 128 characters - a state id over ~26
    /// characters makes the editor fail. So: at most 16 characters for the command part (long keys are
    /// shortened with a hash) plus a short suffix.
    /// </remarks>
    private static string StateId(ListCommand command, string state, bool showLabel)
    {
        var shortState = state switch
        {
            CommandStates.On => "on",
            CommandStates.Off => "off",
            _ => "na"
        };
        var id = $"{ShortKey(command.Key)}-{shortState}";
        if (!showLabel)
        {
            id += "-i";
        }

        return command.OnColor is null && command.OffColor is null ? id : id + "-c";
    }

    /// <summary>The key itself up to 16 characters, else its first 10 + "-" + a 4-digit hash.</summary>
    private static string ShortKey(string key)
    {
        if (key.Length <= 16)
        {
            return key;
        }

        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return $"{key[..10].TrimEnd('-')}-{hash & 0xFFFF:x4}";
    }

    private static IReadOnlyList<ActionStateDefinition> StatesFor(ListCommand command, bool showLabel)
    {
        var label = showLabel ? command.Name : "";
        return
        [
            new(StateId(command, CommandStates.On, showLabel), $"{command.Name} (on)")
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = label, BackgroundColor = command.OnColor ?? "#2f855a", LabelColor = "#ffffff"
                }
            },
            new(StateId(command, CommandStates.Off, showLabel), command.Name)
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = label, BackgroundColor = command.OffColor ?? "#2d3748", LabelColor = "#e2e8f0"
                }
            },
            new(StateId(command, CommandStates.Unavailable, showLabel), $"{command.Name} (unavailable)")
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = label, BackgroundColor = "#4a5568", LabelColor = "#cbd5e0"
                }
            }
        ];
    }

    private sealed class Executor(
        CommandListAction action,
        ReaperConnection connection,
        CommandStateMonitor stateMonitor,
        ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var command = action.CommandOf(context.Parameters);
            var result = await CommandStates.RunAsync(
                [command.CommandId], RuleFor(command), connection, stateMonitor, logger, context.CancellationToken);

            // RunAsync names the expected state "on"/"off"; this action's states carry the command key.
            return result is { Status: ActionResultStatus.Succeeded, ExpectedStateId: { } expected }
                ? ActionResult.Success(StateId(command, expected, ShowsLabel(context.Parameters)))
                : result;
        }
    }
}
