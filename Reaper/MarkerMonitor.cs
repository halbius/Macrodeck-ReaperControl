using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>Markers of the current project (no regions) and the play/edit cursor position.</summary>
/// <param name="Markers">Sorted by position.</param>
/// <param name="Position">Play position while playing, edit cursor otherwise (seconds).</param>
/// <param name="CurrentMarkerId">The last marker at or before <paramref name="Position" />, if any.</param>
public sealed record MarkerSnapshot(IReadOnlyList<ReaperMarker> Markers, double Position, int? CurrentMarkerId)
{
    /// <summary>Tolerance for "the cursor stands exactly on a marker" (float positions).</summary>
    public const double Tolerance = 0.0005;

    /// <summary>First marker right of the cursor (matching <paramref name="filter" /> if given).</summary>
    public ReaperMarker? Next(MarkerFilter? filter) =>
        Markers.FirstOrDefault(m => m.Start > Position + Tolerance && (filter is null || filter.Matches(m)));

    /// <summary>Last marker left of the cursor; a marker exactly at the cursor does not count.</summary>
    public ReaperMarker? Previous(MarkerFilter? filter) =>
        Markers.LastOrDefault(m => m.Start < Position - Tolerance && (filter is null || filter.Matches(m)));
}

/// <summary>How a filter text is compared with marker names (case-insensitive).</summary>
public enum MarkerMatch
{
    Contains,
    StartsWith
}

public sealed record MarkerFilter(string Text, MarkerMatch Match)
{
    public bool Matches(ReaperMarker marker) => Match == MarkerMatch.StartsWith
        ? marker.Name.StartsWith(Text, StringComparison.OrdinalIgnoreCase)
        : marker.Name.Contains(Text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The marker filter that the filter buttons set and the marker buttons follow (one, shared).</summary>
public sealed class MarkerFilterState
{
    private MarkerFilter? _current;

    public MarkerFilter? Current => Volatile.Read(ref _current);

    /// <summary>Activates <paramref name="filter" />, or clears it if it is already active. Returns the new filter.</summary>
    public MarkerFilter? Toggle(MarkerFilter filter)
    {
        while (true)
        {
            var current = Current;
            var next = filter.Equals(current) ? null : filter;
            if (Interlocked.CompareExchange(ref _current, next, current) == current)
            {
                return next;
            }
        }
    }
}

/// <summary>
/// Polls REAPER's marker list and cursor position in the background while marker buttons are shown,
/// and tells Macro Deck to re-read those buttons when a marker or the current marker changes.
/// </summary>
public sealed class MarkerMonitor(
    ReaperConnection connection,
    IPluginCatalogNotifier notifier,
    ILogger<MarkerMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(75);

    // Markers count as "current" from their position on; a small tolerance for float positions.
    private const double PositionTolerance = 0.0005;

    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private long _lastWanted;
    private MarkerSnapshot? _snapshot;
    private string? _signature;
    private bool _reachable = true;

    /// <summary>The last known markers, and keep polling them. Null until the first answer.</summary>
    public MarkerSnapshot? Get()
    {
        var first = Interlocked.Exchange(ref _lastWanted, Environment.TickCount64) == 0;
        if (first)
        {
            RequestRefresh();
        }

        return Volatile.Read(ref _snapshot);
    }

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

    /// <summary>Makes Macro Deck re-read this plugin's buttons now (e.g. after the filter changed).</summary>
    public void NotifyButtonsChanged() => notifier.CatalogChanged("actions", reason: "markers");

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
                logger.LogError(ex, "Polling REAPER markers failed");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var client = connection.Client;
        if (client is null || Environment.TickCount64 - Interlocked.Read(ref _lastWanted) > WatchTimeout.TotalMilliseconds)
        {
            return;
        }

        string reply;
        try
        {
            reply = await client.SendAsync(["MARKER", "TRANSPORT"], cancellationToken);
            if (!_reachable)
            {
                _reachable = true;
                logger.LogInformation("REAPER is reachable again (markers)");
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (_reachable)
            {
                _reachable = false;
                logger.LogWarning("REAPER does not answer the marker request ({Message})", ex.Message);
            }

            return;
        }

        var markers = ReaperResponseParser.ParseMarkers(reply)
            .Where(m => !m.IsRegion)
            .OrderBy(m => m.Start)
            .ThenBy(m => m.Id)
            .ToList();
        var position = ReaperResponseParser.ParseTransport(reply)?.PositionSeconds ?? 0;
        var current = markers.LastOrDefault(m => m.Start <= position + PositionTolerance)?.Id;

        Volatile.Write(ref _snapshot, new MarkerSnapshot(markers, position, current));

        // Only what buttons show: names, ids, order and the current marker - not every cursor move.
        var snapshotNow = new MarkerSnapshot(markers, position, current);
        var signature = string.Join('\u0001', markers.Select(m => $"{m.Id}\u0002{m.Name}\u0002{m.Color}"))
                        + "\u0003" + current
                        + "\u0003" + snapshotNow.Previous(null)?.Id + "/" + snapshotNow.Next(null)?.Id;
        if (signature != _signature)
        {
            logger.LogInformation("REAPER markers: {Count} markers (IDs {Ids}), current {Current}",
                markers.Count, string.Join(", ", markers.Select(m => m.Id)), current?.ToString() ?? "none");
            if (markers.Count == 0)
            {
                // Shows what REAPER actually answered, in case the format is not what the parser expects.
                logger.LogInformation("REAPER answer to MARKER;TRANSPORT: {Reply}",
                    reply.Length > 500 ? reply[..500] : reply);
            }

            _signature = signature;
            NotifyButtonsChanged();
        }
    }

    public override void Dispose()
    {
        _wakeUp.Dispose();
        base.Dispose();
    }
}
