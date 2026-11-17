using System.Globalization;
using MacroDeck.Sdk.Variables;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// Writable variables for Macro Deck's Slider (fader) widget: volume of the selected tracks, of the
/// selected items, or of whichever of the two was clicked last in REAPER ("follows context").
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Value: the loudest selected track/item in dB (-60 … +12, 0.1 steps; -60 = -inf). Nothing
/// selected: unavailable, the fader is inactive.</item>
/// <item>Moving the fader sets the loudest one to the fader value and moves all other selected ones by
/// the same amount (trim). The scripts ReaperControl_TrackState.lua / ReaperControl_ItemState.lua do
/// that ("&lt;Kind&gt;VolumeSet" = "&lt;seq&gt;|&lt;dB&gt;"); they publish the value as "&lt;Kind&gt;Volume".</item>
/// <item>One undo step per fader movement: <see cref="CommitDelay" /> after the last value the plugin
/// sends "&lt;Kind&gt;VolumeCommit", and the script then adds the undo point.</item>
/// <item>"Follows context" uses ReaperControl_Context.lua (tracks / items; envelopes: unavailable).</item>
/// </list>
/// </remarks>
public sealed class VolumeVariables(
    ReaperConnection connection,
    CommandStateMonitor monitor,
    ILogger<VolumeVariables> logger) : IDisposable
{
    // Ids persisted in profiles (widgets bound to them) - never rename.
    public const string TrackVolumeId = "track-volume";
    public const string ItemVolumeId = "item-volume";
    public const string ContextVolumeId = "context-volume";
    public const string TrackNameId = "track-name";
    public const string TrackNumberId = "track-number";
    public const string ItemNameId = "item-name";
    public const string ContextId = "context";
    public const string VolumeTargetId = "volume-target";

    private const string Section = "ReaperControl";
    private const double MinDb = -60, MaxDb = 12, StepDb = 0.1;

    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(700);

    private static readonly ExtStateValue ContextSource = new(Section, "Context");

    private readonly Dictionary<string, Timer> _commitTimers = [];
    private readonly Lock _sync = new();
    private long _seq;

    public static IReadOnlyList<VariableDefinition> Definitions { get; } =
    [
        Define("reaper_track_volume", TrackVolumeId, "REAPER: Track volume",
            "Volume of the selected tracks (loudest one shown; moving trims all of them)."),
        Define("reaper_item_volume", ItemVolumeId, "REAPER: Item volume",
            "Volume of the selected items (loudest one shown; moving trims all of them)."),
        Define("reaper_volume", ContextVolumeId, "REAPER: Volume (follows context)",
            "Track or item volume, depending on what was clicked last in REAPER."),
        // Texts for fader labels, e.g. Label "{{ vars.reaper_track_number }}: {{ vars.reaper_track_name }}".
        DefineText("reaper_track_name", TrackNameId, "REAPER: Track name",
            "Name of the loudest selected track (the one the track fader shows), \"(+n)\" for more selected."),
        DefineText("reaper_track_number", TrackNumberId, "REAPER: Track number",
            "Number of the loudest selected track."),
        DefineText("reaper_item_name", ItemNameId, "REAPER: Item name",
            "Active take name of the loudest selected item, \"(+n)\" for more selected."),
        DefineText("reaper_context", ContextId, "REAPER: Context",
            "\"Track\", \"Item\" or \"Envelope\": what was clicked last in REAPER."),
        DefineText("reaper_volume_target", VolumeTargetId, "REAPER: Volume target",
            "Track or item name the context volume fader controls right now."),
    ];

    private static VariableDefinition DefineText(string name, string id, string displayName, string description) =>
        VariableDefinition.Eager(name, VariableType.Text, refreshInterval: TimeSpan.FromMilliseconds(500))
            with
            {
                Id = id,
                DisplayName = displayName,
                Description = description
            };

    private static VariableDefinition Define(string name, string id, string displayName, string description) =>
        VariableDefinition.Eager(name, VariableType.Numeric, decimalPlaces: 1,
                refreshInterval: TimeSpan.FromMilliseconds(250))
            with
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                Unit = "dB",
                // Volume wants live feedback while dragging, not only on release.
                Write = new VariableWriteCapability { CommitOnRelease = false }
            };

    public static bool Owns(string localId) =>
        localId is TrackVolumeId or ItemVolumeId or ContextVolumeId
            or TrackNameId or TrackNumberId or ItemNameId or ContextId or VolumeTargetId;

    /// <summary>The current value, from the state monitor's cache (never talks to REAPER itself).</summary>
    public VariableReading Read(string localId)
    {
        switch (localId)
        {
            case TrackNameId:
                return Text("TrackName");
            case TrackNumberId:
                return Text("TrackNumber");
            case ItemNameId:
                return Text("ItemName");
            case ContextId:
                return VariableReading.Of(monitor.GetValue(ContextSource) switch
                {
                    "tracks" => "Track",
                    "items" => "Item",
                    "envelopes" => "Envelope",
                    _ => ""
                });
            case VolumeTargetId:
                return KindOf(ContextVolumeId) is { } target ? Text(target + "Name") : VariableReading.Of("");
        }

        if (KindOf(localId) is not { } kind)
        {
            return VariableReading.Unavailable;
        }

        var raw = monitor.GetValue(new ExtStateValue(Section, kind + "Volume"));
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            ? VariableReading.Of(Math.Round(db, 1), MinDb, MaxDb, StepDb)
            : VariableReading.Unavailable; // script not running yet, or "-" = nothing selected
    }

    /// <summary>A text the scripts publish; empty while nothing is selected or the script is not running.</summary>
    private VariableReading Text(string key) =>
        VariableReading.Of(monitor.GetValue(new ExtStateValue(Section, key)) ?? "");

    public async ValueTask<VariableWriteResult> WriteAsync(
        string localId, object? value, CancellationToken cancellationToken)
    {
        if (KindOf(localId) is not { } kind)
        {
            return VariableWriteResult.Unavailable("Nothing to control in this REAPER context.");
        }

        if (!double.TryParse(Values.AsString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
        {
            return VariableWriteResult.InvalidValue("Not a number.");
        }

        if (connection.Client is not { } client)
        {
            return VariableWriteResult.Unavailable("REAPER connection is not set up yet.");
        }

        db = Math.Clamp(db, MinDb, MaxDb);
        var seq = Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
        try
        {
            await client.SendEncodedAsync(
                [ReaperWebClient.EncodeSetExtState(Section, kind + "VolumeSet",
                    seq + "|" + db.ToString("0.00", CultureInfo.InvariantCulture))],
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return VariableWriteResult.Unavailable("REAPER is not reachable.");
        }

        ScheduleCommit(kind, seq);
        monitor.RequestRefresh(); // show the new value soon
        return VariableWriteResult.Applied();
    }

    /// <summary>"Track" / "Item" for the variable - for "follows context" from REAPER's last clicked area.</summary>
    private string? KindOf(string localId) => localId switch
    {
        TrackVolumeId => "Track",
        ItemVolumeId => "Item",
        ContextVolumeId => monitor.GetValue(ContextSource) switch
        {
            "tracks" => "Track",
            "items" => "Item",
            _ => null // envelopes, or the context script not running yet
        },
        _ => null
    };

    /// <summary>After the last value of a fader movement: one undo step for the whole movement.</summary>
    private void ScheduleCommit(string kind, string seq)
    {
        lock (_sync)
        {
            if (_commitTimers.Remove(kind, out var previous))
            {
                previous.Dispose();
            }

            _commitTimers[kind] = new Timer(_ => _ = CommitAsync(kind, seq), null, CommitDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CommitAsync(string kind, string seq)
    {
        try
        {
            if (connection.Client is { } client)
            {
                await client.SendEncodedAsync(
                    [ReaperWebClient.EncodeSetExtState(Section, kind + "VolumeCommit", seq)], CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sending the {Kind} volume undo point failed", kind);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var timer in _commitTimers.Values)
            {
                timer.Dispose();
            }

            _commitTimers.Clear();
        }
    }
}
