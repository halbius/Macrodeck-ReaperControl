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
public sealed class ItemAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<ItemAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "item";

    public override string Id => ActionId;

    public override LocalizedText Name => "Item";

    public override LocalizedText Description =>
        "Changes a setting of the selected items and shows whether it is on for them.";

    // To add a command: one line here (see the remarks above for the feedback).
    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        // "Item properties: Toggle mute" - check the id in your action list.
        // Red while the selected items are muted, dark grey-red otherwise.
        new("mute", "Mute", "40175", State: SelectedItems("ItemMute"), OnColor: "#c53030", OffColor: "#4a2a2a"),
        new("loop-source", "Loop Source", "40636", State: SelectedItems("ItemLoop"), OnColor: "#306cc5", OffColor: "#3e4a5c"),
        new("lock", "Lock", "40687", Icon: "lock-open", IconWhenOn: "lock-closed", State: SelectedItems("ItemLock"), OnColor: "#b82323", OffColor: "#7c5151"),
        new("play-all-takes", "Play All Takes", "40437", State: SelectedItems("ItemPlayAllTakes"), OnColor: "#b6c530", OffColor: "#717547"),
        new("fx-chain", "FX Chain", "40638", State: SelectedItems("ItemFXChain"), OnColor: "#b6c530", OffColor: "#717547"),

    ];

    /// <summary>On while all selected items have this setting on (published by the Lua script).</summary>
    private static StateRule SelectedItems(string extStateKey) =>
        StateRule.ExtStateEquals("ReaperControl", extStateKey, "1");
}
