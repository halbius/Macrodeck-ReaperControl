using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// The play/edit cursor position in every format the timecode widget can show.
/// </summary>
/// <param name="TransportFormat">As REAPER's transport window shows it (from TRANSPORT).</param>
/// <param name="Beats">Measures.beats (from TRANSPORT).</param>
/// <param name="Seconds">Position in seconds.</param>
/// <param name="ScriptFormats">Formatted by ReaperControl_Timecode.lua, keyed by REAPER's time format
/// mode (0 time, 1 measures.beats + time, 2 measures.beats, 3 seconds, 4 samples, 5 h:m:s:f, 8 frames).
/// Empty while the script is not running.</param>
/// <param name="Recording">REAPER records (also record-paused).</param>
public sealed record TimecodeSnapshot(
    string TransportFormat,
    string Beats,
    double Seconds,
    IReadOnlyDictionary<int, string> ScriptFormats,
    bool Recording = false);

/// <summary>
/// Polls the cursor position while at least one timecode widget is open (100 ms) and tells the
/// widgets about every change.
/// </summary>
public sealed class TimecodeMonitor(ReaperConnection connection, ILogger<TimecodeMonitor> logger) : BackgroundService
{
    public const string ExtStateSection = "ReaperControl";
    public const string ExtStateKey = "Timecode";

    /// <summary>The time format modes the script writes, in this order, separated by '|'.</summary>
    public static readonly int[] ScriptModes = [0, 1, 2, 3, 4, 5, 8];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(500);

    private int _subscribers;
    private bool _reachable = true;

    /// <summary>The last known position, or null when REAPER is not reachable.</summary>
    public TimecodeSnapshot? Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }

    private TimecodeSnapshot? _current;

    /// <summary>Raised on a background thread whenever <see cref="Current" /> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Keeps the monitor polling while the returned handle is not disposed.</summary>
    public IDisposable Subscribe()
    {
        Interlocked.Increment(ref _subscribers);
        return new Subscription(this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = Volatile.Read(ref _subscribers) > 0;
            try
            {
                await Task.Delay(active ? PollInterval : IdleInterval, stoppingToken);
                if (active)
                {
                    await PollOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling the REAPER position failed");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        TimecodeSnapshot? next;
        try
        {
            var reply = await client.SendAsync(
                ["TRANSPORT", $"GET/EXTSTATE/{ExtStateSection}/{ExtStateKey}"], cancellationToken);
            next = Parse(reply);
            _reachable = true;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (_reachable)
            {
                logger.LogWarning("REAPER does not answer the position request ({Message})", ex.Message);
                _reachable = false;
            }

            next = null;
        }

        var previous = Current;
        if ((next is null) != (previous is null) || (next is not null && !SameContent(next, previous)))
        {
            Current = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TimecodeSnapshot? Parse(string reply)
    {
        if (ReaperResponseParser.ParseTransport(reply) is not { } transport)
        {
            return null;
        }

        var ext = new ExtStateValue(ExtStateSection, ExtStateKey);
        var raw = ReaperResponseParser.ParseStates(reply, [ext]).GetValueOrDefault(ext.Key, "");

        var formats = new Dictionary<int, string>();
        if (raw.Length > 0)
        {
            var parts = raw.Split('|');
            for (var i = 0; i < ScriptModes.Length && i < parts.Length; i++)
            {
                formats[ScriptModes[i]] = parts[i].Trim();
            }
        }

        return new TimecodeSnapshot(transport.PositionString, transport.PositionBeats, transport.PositionSeconds,
            formats, Recording: transport.PlayState is 5 or 6);
    }

    private static bool SameContent(TimecodeSnapshot? a, TimecodeSnapshot? b) =>
        a is not null && b is not null
        && a.TransportFormat == b.TransportFormat
        && a.Beats == b.Beats
        && a.Recording == b.Recording
        && a.Seconds.ToString("F4", CultureInfo.InvariantCulture) == b.Seconds.ToString("F4", CultureInfo.InvariantCulture)
        && a.ScriptFormats.Count == b.ScriptFormats.Count
        && a.ScriptFormats.All(kv => b.ScriptFormats.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private sealed class Subscription(TimecodeMonitor monitor) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref monitor._subscribers);
            }
        }
    }
}
