using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// Event "REAPER context changed": raised when the area last clicked in REAPER changes between tracks,
/// items and envelopes - e.g. for a trigger that opens the matching deck folder ("Change folder").
/// </summary>
/// <remarks>
/// Runs only while at least one trigger is bound to the event (the host tells the plugin its bindings),
/// so without such a trigger neither this monitor polls nor ReaperControl_Context.lua runs. The value
/// has to stay the same for <see cref="Settle" /> before the event fires, so quick clicks back and forth
/// do not make the deck jump.
/// </remarks>
public sealed class ContextMonitor(
    ReaperConnection connection,
    ScriptKeeper scripts,
    ILogger<ContextMonitor> logger) : BackgroundService
{
    // Event id and payload/config parameter name: persisted in users' triggers - never rename.
    public const string EventId = "context-changed";
    private const string ContextParameter = "context";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static readonly IReadOnlyList<ActionParameterOption> Contexts =
    [
        new() { Value = "tracks", Label = "Tracks" },
        new() { Value = "items", Label = "Items" },
        new() { Value = "envelopes", Label = "Envelopes" },
    ];

    public static EventDefinition Definition { get; } = new()
    {
        Id = EventId,
        Name = "REAPER context changed",
        Description = "The area last clicked in REAPER changed (tracks, items or envelopes). " +
                      "Needs the REAPER scripts (plugin settings → Install/update REAPER scripts).",
        Category = "REAPER",
        // Same name on both sides: the trigger's choice filters the occurrence's value.
        ConfigurationParameters =
        [
            ActionParameter.Choice(ContextParameter, Contexts, "Context", required: true)
        ],
        PayloadParameters =
        [
            ActionParameter.Choice(ContextParameter, Contexts, "Context")
        ]
    };

    private readonly ExtStateValue _source = new("ReaperControl", "Context");
    private string? _candidate;
    private long _candidateSince;
    private string? _published;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = false;
            try
            {
                active = IsBound();
                await Task.Delay(active ? PollInterval : IdleInterval, stoppingToken);
                if (active)
                {
                    await PollOnceAsync(stoppingToken);
                }
                else
                {
                    _candidate = _published = null; // start fresh when a trigger is bound again
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // REAPER not reachable: try again on the next round.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watching the REAPER context failed");
            }
        }
    }

    private bool IsBound() =>
        connection.HostContext?.Events.GetBindings().Any(b => b.EventId == EventId) == true;

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (connection.Client is not { } client || connection.HostContext is not { } host)
        {
            return;
        }

        scripts.NeedKey(_source.Name); // keeps ReaperControl_Context.lua running

        var reply = await client.SendAsync([_source.Request], cancellationToken);
        var value = ReaperResponseParser.ParseStates(reply, [_source]).GetValueOrDefault(_source.Key, "");
        if (value.Length == 0)
        {
            return; // script not running yet
        }

        var now = Environment.TickCount64;
        if (value != _candidate)
        {
            _candidate = value;
            _candidateSince = now;
            return;
        }

        if (value != _published && now - _candidateSince >= Settle.TotalMilliseconds)
        {
            _published = value;
            logger.LogDebug("REAPER context: {Context}", value);
            host.Events.Publish(EventId, new Dictionary<string, object?> { [ContextParameter] = value });
        }
    }
}
