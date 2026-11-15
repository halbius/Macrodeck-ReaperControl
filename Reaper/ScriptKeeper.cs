using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// Starts the background Lua scripts when the plugin needs their values, and keeps them alive with a
/// heartbeat - the scripts stop themselves when the heartbeat stops (Macro Deck closed, no button
/// needs them any more). So no script runs while Macro Deck is not used, and nothing goes into
/// REAPER's __startup.lua.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Needed: something asked for one of the script's values within the last <see cref="KeepAlive" />
/// (buttons via CommandStateMonitor, the timecode tile via TimecodeMonitor).</item>
/// <item>Every <see cref="Interval" />, for each needed script, one request writes its heartbeat
/// (ExtState "ReaperControl"/"&lt;Name&gt;Heartbeat", a changing counter) and reads its
/// "&lt;Name&gt;Running" flag, which the script sets at start and clears on exit.</item>
/// <item>Not running: the script is started by its command id from reaper-kb.ini - at most every
/// <see cref="RetryAfter" />, so REAPER never gets a second start of a script that is just starting
/// (that would ask "terminate / new instance?").</item>
/// </list>
/// The scripts must be in REAPER's action list once (ReaperControl_Setup.lua does that).
/// </remarks>
public sealed class ScriptKeeper(ReaperConnection connection, ILogger<ScriptKeeper> logger) : BackgroundService
{
    private const string Section = "ReaperControl";

    // Macro Deck reads hidden buttons only about every 30 s: keep scripts alive well beyond that.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<string, long> _lastWanted = new();
    private readonly Dictionary<string, long> _lastLaunch = [];
    private readonly HashSet<string> _warnedMissing = [];
    private long _beat;

    /// <summary>Someone needs ExtState "ReaperControl"/<paramref name="key" />: keep its script running.</summary>
    public void NeedKey(string key)
    {
        if (ReaperScriptFiles.ForKey(key) is { } script)
        {
            _lastWanted[script.Name] = Environment.TickCount64;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // REAPER not reachable: the state monitors report that already.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Keeping the REAPER scripts alive failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        var now = Environment.TickCount64;
        var needed = ReaperScriptFiles.Background
            .Where(s => _lastWanted.TryGetValue(s.Name, out var t) && now - t < KeepAlive.TotalMilliseconds)
            .ToList();
        if (needed.Count == 0)
        {
            return;
        }

        // Heartbeats first, then the running flags - REAPER handles the parts in order.
        var beat = (++_beat).ToString();
        var running = needed.Select(s => new ExtStateValue(Section, s.RunningKey)).ToList();
        var reply = await client.SendEncodedAsync(
            [
                .. needed.Select(s => ReaperWebClient.EncodeSetExtState(Section, s.HeartbeatKey, beat)),
                .. running.Select(r => r.Request)
            ],
            cancellationToken);
        var states = ReaperResponseParser.ParseStates(reply, running);

        foreach (var script in needed)
        {
            var flag = states.GetValueOrDefault(new ExtStateValue(Section, script.RunningKey).Key, "");
            if (flag.Length > 0)
            {
                continue; // running
            }

            if (_lastLaunch.TryGetValue(script.Name, out var last) && now - last < RetryAfter.TotalMilliseconds)
            {
                continue; // just started, give it time
            }

            var commandId = connection.Catalog.FindScriptCommandId(script.FileName, ReaperScriptFiles.InstallFolder);
            if (commandId is null)
            {
                if (_warnedMissing.Add(script.Name))
                {
                    logger.LogWarning(
                        "{Script} is not in REAPER's action list, so the plugin cannot start it. Install the " +
                        "scripts (plugin settings) and run ReaperControl_Setup.lua once in REAPER.",
                        script.FileName);
                }

                continue;
            }

            _warnedMissing.Remove(script.Name);
            _lastLaunch[script.Name] = now;
            logger.LogInformation("Starting {Script} in REAPER", script.FileName);
            await client.SendEncodedAsync([Uri.EscapeDataString(commandId)], cancellationToken);
        }
    }
}
