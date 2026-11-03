using System.Globalization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// Where a button's on/off state comes from and how the raw value maps to it.
/// </summary>
/// <param name="Source">The value to read from REAPER.</param>
/// <param name="OnWhen">Null: the value is an action toggle state (&gt;0 on, 0 off, -1 none).
/// Otherwise the button is on while the value equals this text.</param>
public sealed record StateRule(StateSource Source, string? OnWhen = null)
{
    /// <summary>On/off as REAPER shows it for the action (checkmark in menus, "State" column).</summary>
    public static StateRule Toggle(string commandId) => new(new CommandToggleState(commandId));

    /// <summary>On while a ReaScript's ext state equals <paramref name="value" />.</summary>
    public static StateRule ExtStateEquals(string section, string name, string value) =>
        new(new ExtStateValue(section, name), value);

    public bool IsToggle => OnWhen is null;

    /// <summary>The state id for a raw value, or null when there is nothing to show.</summary>
    public string? Evaluate(string raw)
    {
        if (IsToggle)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var state) || state < 0)
            {
                return null; // the action has no on/off state
            }

            return state > 0 ? CommandStates.On : CommandStates.Off;
        }

        // An ext state that was never set: the publishing script is not running.
        if (raw.Length == 0)
        {
            return CommandStates.Unavailable;
        }

        return string.Equals(raw, OnWhen, StringComparison.OrdinalIgnoreCase) ? CommandStates.On : CommandStates.Off;
    }
}

/// <summary>
/// Shared by all actions that run REAPER commands and show an on/off state on the button:
/// the state set, the state read from the monitor's cache, and running commands.
/// </summary>
internal static class CommandStates
{
    // State ids are persisted in profiles once a button adopts them - never rename.
    public const string On = "on";
    public const string Off = "off";
    public const string Unavailable = "unavailable";

    public static IReadOnlyList<ActionStateDefinition> Definitions { get; } =
    [
        new(On, "On")
        {
            DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2f855a", LabelColor = "#ffffff" }
        },
        new(Off, "Off"),
        new(Unavailable, "Unavailable")
        {
            DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568", LabelColor = "#cbd5e0" }
        }
    ];

    /// <summary>
    /// Snapshot for <paramref name="rule" /> from the monitor's cache only - never talks to
    /// REAPER (SDK rule for GetActionStateAsync). Null when there is nothing to show.
    /// </summary>
    public static ActionStateSnapshot? Snapshot(
        StateRule? rule, ReaperConnection connection, CommandStateMonitor monitor)
    {
        if (rule is null || connection.Client is null)
        {
            return null;
        }

        if (!monitor.IsReachable)
        {
            return new ActionStateSnapshot(Definitions, Unavailable);
        }

        var raw = monitor.GetValue(rule.Source);
        if (raw is null)
        {
            return new ActionStateSnapshot(Definitions, null); // first answer still pending
        }

        return rule.Evaluate(raw) is { } active ? new ActionStateSnapshot(Definitions, active) : null;
    }

    /// <summary>
    /// Runs <paramref name="commands" />. For a toggle rule the state is read in the same request -
    /// REAPER handles the parts in order, so it is the state after the commands ran. Ext states are
    /// updated by their script a moment later; the monitor picks that up.
    /// </summary>
    public static async Task<ActionResult> RunAsync(
        IReadOnlyList<string> commands,
        StateRule? rule,
        ReaperConnection connection,
        CommandStateMonitor monitor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var client = connection.Client;
        if (client is null)
        {
            return ActionResult.Failed(ActionErrorCodes.NotConfigured, "REAPER connection is not set up yet.");
        }

        try
        {
            var readNow = rule is { IsToggle: true } ? new[] { rule.Source } : Array.Empty<StateSource>();
            var values = await client.SendAndGetStatesAsync(commands, readNow, cancellationToken);

            monitor.Update(values);
            // Other buttons may have changed too (the other modes of an option group).
            monitor.RequestRefresh();

            return rule is { IsToggle: true } && values.TryGetValue(rule.Source.Key, out var raw)
                   && rule.Evaluate(raw) is { } expected
                ? ActionResult.Success(expected)
                : ActionResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ActionResult.Failed(ActionErrorCodes.Timeout, "REAPER did not answer in time.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            return ActionResult.Failed(ActionErrorCodes.PermissionDenied,
                "REAPER rejected the login. Check username and password.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Sending '{Commands}' to REAPER failed", string.Join(';', commands));
            return ActionResult.Failed(ActionErrorCodes.NotConnected,
                "REAPER is not reachable. Is it running with the web interface enabled?");
        }
    }
}
