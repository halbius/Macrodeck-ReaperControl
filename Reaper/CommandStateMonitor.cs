using System.Collections.Concurrent;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// Keeps the REAPER values that buttons show up to date: action toggle states and ext states
/// published by ReaScripts (see <see cref="StateSource" />).
/// </summary>
/// <remarks>
/// Macro Deck asks for a button's state via GetActionStateAsync, which must answer from held state and
/// never talk to REAPER itself. So this service polls REAPER in the background - only for the values
/// a button asked about recently, all in one request - and caches the answers. When a value
/// changes it sends a state.update, so Macro Deck re-reads the buttons immediately instead of
/// waiting for its next poll (which is at least 1 s).
/// </remarks>
public sealed class CommandStateMonitor(
    ReaperConnection connection,
    IPluginCatalogNotifier notifier,
    ILogger<CommandStateMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Macro Deck polls hidden buttons about every 30 s; stop watching values nobody asked for in longer.
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(75);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private volatile bool _reachable = true;
    private bool _warnedNoAnswer;

    /// <summary>False while REAPER does not answer (not running, web interface off, wrong port).</summary>
    public bool IsReachable => _reachable && connection.Client is not null;

    /// <summary>
    /// Last known raw value of <paramref name="source" />, and from now on keep watching it.
    /// Null until the first answer from REAPER.
    /// </summary>
    public string? GetValue(StateSource source)
    {
        var entry = _entries.GetOrAdd(source.Key, _ => new Entry());
        entry.Source ??= source;
        var firstAsk = entry.LastWanted == 0;
        entry.LastWanted = Environment.TickCount64;
        if (firstAsk)
        {
            RequestRefresh();
        }

        return entry.Value;
    }

    /// <summary>Stores values read elsewhere (e.g. right after an action ran), keyed by source key.</summary>
    public void Update(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            _entries.GetOrAdd(key, _ => new Entry()).Value = value;
        }
    }

    /// <summary>Poll now instead of at the next interval, e.g. after a button changed something.</summary>
    public void RequestRefresh()
    {
        try
        {
            _wakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
            // A refresh is already pending.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeUp.WaitAsync(PollInterval, stoppingToken);
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling REAPER states failed");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var client = connection.Client;
        var now = Environment.TickCount64;
        var sources = _entries.Values
            .Where(e => e.Source is not null && now - e.LastWanted < WatchTimeout.TotalMilliseconds)
            .Select(e => e.Source!)
            .ToList();

        if (client is null || sources.Count == 0)
        {
            return;
        }

        var changed = false;
        try
        {
            var values = await client.GetStatesAsync(sources, cancellationToken);

            var missing = sources.Where(s => !values.ContainsKey(s.Key)).Select(s => s.Key).ToList();
            if (missing.Count > 0 && !_warnedNoAnswer)
            {
                _warnedNoAnswer = true;
                logger.LogWarning("REAPER gave no answer for {Missing}", string.Join(", ", missing));
            }

            foreach (var (key, value) in values)
            {
                var entry = _entries.GetOrAdd(key, _ => new Entry());
                if (entry.Value != value)
                {
                    logger.LogInformation("REAPER {Key}: '{Old}' -> '{New}'", key, entry.Value ?? "unknown", value);
                    entry.Value = value;
                    changed = true;
                }
            }

            if (!_reachable)
            {
                logger.LogInformation("REAPER is reachable again");
                _reachable = true;
                changed = true;
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (_reachable)
            {
                logger.LogWarning("REAPER does not answer ({Message}); buttons show 'unavailable'", ex.Message);
                _reachable = false;
                changed = true;
            }
        }

        if (changed)
        {
            // Brings Macro Deck's next state read forward for all of this plugin's action buttons
            // (no local id: several actions show states - "run-command" and the fixed ones).
            notifier.CatalogChanged("actions", reason: "reaper-state");
            // Icons are polled separately; bring the read forward for actions with state-driven icons.
            connection.InvalidateIcons(ReaperControl.Actions.TransportAction.ActionId, ReaperControl.Actions.ItemAction.ActionId);
        }
    }

    public override void Dispose()
    {
        _wakeUp.Dispose();
        base.Dispose();
    }

    private sealed class Entry
    {
        private long _lastWanted;
        private volatile string? _value;
        private volatile StateSource? _source;

        public long LastWanted
        {
            get => Interlocked.Read(ref _lastWanted);
            set => Interlocked.Exchange(ref _lastWanted, value);
        }

        public string? Value
        {
            get => _value;
            set => _value = value;
        }

        public StateSource? Source
        {
            get => _source;
            set => _source = value;
        }
    }
}
