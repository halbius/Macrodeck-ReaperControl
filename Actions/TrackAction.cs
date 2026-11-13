using MacroDeck.Localization;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Item": item settings of the selected items (mute, ...), chosen in the button's settings.
/// </summary>
/// <remarks>
/// Item settings are stored per item, so REAPER's item actions have no on/off state of their own.
/// "On" comes from ReaperScripts/ReaperControl_ItemState.lua instead: it publishes the settings of
/// the selected items as ExtState "ReaperControl"/"&lt;name&gt;" with "1" (all selected items on), "0"
/// (off or nothing selected) or "mixed".
///
/// A new item setting with feedback takes two lines:
/// 1. here: new("lock", "Lock", "&lt;command id&gt;", State: SelectedItems("ItemLock")),
/// 2. in the Lua script, table ITEM_PROPERTIES: ItemLock = "C_LOCK" (the GetMediaItemInfo_Value name).
/// Without feedback, leave State out: the button then shows REAPER's toggle state, if the action has one.
/// </remarks>
public sealed class TrackAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<TrackAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "track";

    public override string Id => ActionId;

    public override LocalizedText Name => "Track";

    public override LocalizedText Description =>
        "Changes a setting of the selected tracks and shows whether it is on for them.";

    // To add a command: one line here (see the remarks above for the feedback).
    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        // "Item properties: Toggle mute" - check the id in your action list.
        // Red while the selected items are muted, dark grey-red otherwise.
        new("track-mute", "Track Mute", "6", State: SelectedTracks("TrackMute"), OnColor: "#c53030", OffColor: "#4a2a2a"),
        new("track-solo", "Track Solo", "7", State: SelectedTracks("TrackSolo"), OnColor: "#d4d800", OffColor: "#a0a156"),
        new("track-unsolo-all", "Track Unsolo All", "40340"),
        new("track-pin", "Pin Tracks", "40000"),
        new("lock-controls", "Lock Track Controls", "41314", Icon: "lock-open", IconWhenOn: "lock-closed",
            State: SelectedTracks("TrackLockControls"), OnColor: "#b82323", OffColor: "#7c5151"),
        new("lock-height", "Lock Track Height", "42336", Icon: "lock-open", IconWhenOn: "lock-closed",
            State: SelectedTracks("TrackLockHeight"), OnColor: "#b82323", OffColor: "#7c5151"),
        new("lock-items", "Lock Track Items", "43696", Icon: "lock-open", IconWhenOn: "lock-closed",
            State: SelectedTracks("TrackLockItems"), OnColor: "#b82323", OffColor: "#7c5151"),
        new("phase", "Track Phase", "40282", State: SelectedTracks("TrackPhase"), OnColor: "#308ac5", OffColor: "#818e97"),

    ];

    /// <summary>On while all selected tracks have this setting on (published by the Lua script).</summary>
    private static StateRule SelectedTracks(string extStateKey) =>
        StateRule.ExtStateEquals("ReaperControl", extStateKey, "1");
}
