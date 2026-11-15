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
/// <param name="Options">Variants of this command, chosen in an extra field below "Command" that only
/// shows while this command is picked (e.g. "Lock" → Controls / Height / Items). Each variant brings its
/// own command id, state and label; icon and colors come from this entry. Null: no extra field.</param>
/// <param name="OptionsLabel">Label of that extra field.</param>
public sealed record ListCommand(
    string Key,
    string Name,
    string CommandId,
    string? Icon = null,
    string? IconWhenOn = null,
    StateRule? State = null,
    string? OnColor = null,
    string? OffColor = null,
    IReadOnlyList<ListOption>? Options = null,
    string? OptionsLabel = null);

/// <summary>One variant of a <see cref="ListCommand" /> with <see cref="ListCommand.Options" />.</summary>
/// <param name="Key">Stored parameter value and state id prefix - never rename after first use.
/// Unique across the whole action (it is also accepted as "Command" value, see CommandListAction).</param>
/// <param name="Name">Shown in the extra field and as button label.</param>
/// <param name="CommandId">REAPER command id. Not used when the option has <paramref name="Variants" />.</param>
/// <param name="State">Where "on" comes from. Null: the REAPER action's own toggle state.</param>
/// <param name="Fields">Further choice fields, shown only while this option is picked (e.g. "Fwd" →
/// Unit: Measure / Beat, Seek: Normal / No seek). Together they pick one of <paramref name="Variants" />.</param>
/// <param name="Variants">What the fields lead to: one entry per combination of field values.</param>
public sealed record ListOption(
    string Key,
    string Name,
    string CommandId = "",
    StateRule? State = null,
    IReadOnlyList<ListField>? Fields = null,
    IReadOnlyList<ListVariant>? Variants = null);

/// <summary>A choice field of a <see cref="ListOption" />, e.g. Unit: Measure / Beat.</summary>
/// <param name="Key">Part of the stored parameter name ("fwd" + "unit" → "fwdUnit") - never rename.</param>
/// <param name="Label">Shown above the field.</param>
/// <param name="Choices">Stored value and label of each choice. The first one is the default.</param>
public sealed record ListField(string Key, string Label, IReadOnlyList<(string Value, string Label)> Choices);

/// <summary>The command for one combination of field values of a <see cref="ListOption" />.</summary>
/// <param name="Key">State id prefix - never rename after first use; unique across the whole action
/// (also accepted as "Command" or option value, so buttons from before the fields existed keep working).</param>
/// <param name="Name">Button label.</param>
/// <param name="CommandId">REAPER command id.</param>
/// <param name="When">The field values this variant stands for, in the order of the option's fields.</param>
/// <param name="State">Where "on" comes from. Null: the REAPER action's own toggle state.</param>
public sealed record ListVariant(
    string Key, string Name, string CommandId, IReadOnlyList<string> When, StateRule? State = null);

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

    /// <summary>
    /// False: the buttons show no on/off state - only the command name (in the off colors) - and
    /// "Show text" is not offered. For plain toggles like opening windows. Single entries that set
    /// <c>State:</c> still show their state, e.g. <c>State: StateRule.Toggle("41884")</c>.
    /// </summary>
    protected virtual bool ShowsState => true;

    /// <summary>Whether this button shows on/off: always, or - in an action without states - only when
    /// the entry asks for it with <c>State:</c>.</summary>
    private bool HasState(ListCommand command) => ShowsState || command.State is not null;

    private IReadOnlyList<ActionParameter>? _parameters;

    // Built on first use: Commands comes from the subclass.
    public IReadOnlyList<ActionParameter> Parameters => _parameters ??=
    [
        ActionParameter.Choice(CommandParameter,
            Commands.Select(c => new ActionParameterOption { Value = c.Key, Label = c.Name }).ToList(),
            "Command", defaultValue: Commands[0].Key, required: true),
        // One extra field per command with variants, shown only while that command is picked.
        .. Commands.Where(c => c.Options is { Count: > 0 }).Select(c =>
            ActionParameter.Choice(OptionParameter(c),
                    c.Options!.Select(o => new ActionParameterOption { Value = o.Key, Label = o.Name }).ToList(),
                    c.OptionsLabel ?? c.Name, defaultValue: c.Options![0].Key)
                .OnlyWhen(CommandParameter, c.Key)),
        // The fields of options with variants, shown only while that option is picked.
        .. Commands.Where(c => c.Options is { Count: > 0 }).SelectMany(c => c.Options!
            .Where(o => o.Fields is { Count: > 0 })
            .SelectMany(o => o.Fields!.Select(f =>
                ActionParameter.Choice(FieldParameter(o, f),
                        f.Choices.Select(ch => new ActionParameterOption { Value = ch.Value, Label = ch.Label }).ToList(),
                        f.Label, defaultValue: f.Choices[0].Value)
                    .OnlyWhen(OptionParameter(c), o.Key)))),
        .. (ShowsState
            ? [ActionParameter.Toggle(ShowLabelParameter, "Show text",
                "Show the command name on the button. Turn off for icon-only buttons.", defaultValue: true)]
            : Array.Empty<ActionParameter>())
    ];

    /// <summary>Parameter name of a command's variant field: "lock" → "lockOption".</summary>
    private static string OptionParameter(ListCommand command) => Camel(command.Key) + "Option";

    /// <summary>Parameter name of an option's field: "fwd" + "unit" → "fwdUnit".</summary>
    private static string FieldParameter(ListOption option, ListField field) =>
        Camel(option.Key) + char.ToUpperInvariant(field.Key[0]) + Camel(field.Key)[1..];

    /// <summary>"play-cursor" → "playCursor".</summary>
    private static string Camel(string key) =>
        string.Concat(key.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select((part, i) => i == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public TimeSpan IconPollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (Macro Deck offers "use provided states" only when the action is
        // added). The ids carry the command, so choosing another command brings its label.
        var command = CommandOf(parameters);
        var showLabel = ShowsLabel(parameters);
        if (!HasState(command))
        {
            // One state, always active: just the label.
            var only = StatesFor(command, showLabel: true)[1];
            return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([only], only.Id));
        }

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

    /// <summary>
    /// The command a button runs, walking down the menu: Command → Option → Fields → Variant.
    /// The result carries the command's icon and colors and the key, name, id and state of the deepest
    /// level reached. A deeper key in place of a higher one also works (an option or variant key as
    /// "Command" value, a variant key as option value) - buttons from before a level existed, e.g.
    /// "lock-controls" as its own entry or "fwd-one-measure" as a Go To option, keep working unchanged.
    /// </summary>
    private ListCommand CommandOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var key = Values.Get(parameters, CommandParameter);
        if (Commands.FirstOrDefault(c => c.Key == key) is { } command)
        {
            if (command.Options is not { Count: > 0 } options)
            {
                return command;
            }

            var optionKey = Values.Get(parameters, OptionParameter(command));
            if (options.FirstOrDefault(o => o.Key == optionKey) is { } option)
            {
                return WithOption(command, option, parameters);
            }

            return FindDeeper(command, optionKey) ?? WithOption(command, options[0], parameters);
        }

        foreach (var parent in Commands)
        {
            if (parent.Options?.FirstOrDefault(o => o.Key == key) is { } option)
            {
                return WithOption(parent, option, parameters);
            }

            if (FindDeeper(parent, key) is { } found)
            {
                return found;
            }
        }

        return Commands[0] is { Options: { Count: > 0 } first } c0
            ? WithOption(c0, first[0], parameters)
            : Commands[0];
    }

    /// <summary>A variant of one of <paramref name="command" />'s options with this key, or null.</summary>
    private static ListCommand? FindDeeper(ListCommand command, string? key)
    {
        foreach (var option in command.Options ?? [])
        {
            if (option.Variants?.FirstOrDefault(v => v.Key == key) is { } variant)
            {
                return WithVariant(command, variant);
            }
        }

        return null;
    }

    /// <summary>The option itself, or - when it has fields - the variant its field values pick.</summary>
    private static ListCommand WithOption<T>(
        ListCommand command, ListOption option, IReadOnlyDictionary<string, T> parameters)
    {
        if (option.Variants is not { Count: > 0 } variants)
        {
            return command with
            {
                Key = option.Key, Name = option.Name, CommandId = option.CommandId, State = option.State, Options = null
            };
        }

        var fields = option.Fields ?? [];
        var chosen = fields
            .Select(f => Values.Get(parameters, FieldParameter(option, f)) ?? f.Choices[0].Value)
            .ToList();
        var variant = variants.FirstOrDefault(v => v.When.SequenceEqual(chosen, StringComparer.OrdinalIgnoreCase))
                      ?? variants[0];
        return WithVariant(command, variant);
    }

    private static ListCommand WithVariant(ListCommand command, ListVariant variant) =>
        command with
        {
            Key = variant.Key, Name = variant.Name, CommandId = variant.CommandId, State = variant.State, Options = null
        };

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
                [command.CommandId], action.HasState(command) ? RuleFor(command) : null,
                connection, stateMonitor, logger, context.CancellationToken);

            // RunAsync names the expected state "on"/"off"; this action's states carry the command key.
            return result is { Status: ActionResultStatus.Succeeded, ExpectedStateId: { } expected }
                ? ActionResult.Success(StateId(command, expected, ShowsLabel(context.Parameters)))
                : result;
        }
    }
}
