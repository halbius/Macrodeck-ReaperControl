using MacroDeck.Localization;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Transport": runs a transport command chosen in the button's settings. "On" mostly comes from
/// the transport state (playing / paused), which REAPER's web interface reports - no script needed.
/// </summary>
public sealed class TransportAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<TransportAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "transport";

    // On = REAPER is playing or recording (the play actions themselves have no on/off state).
    private static readonly StateRule Playing = new(new TransportPlaying(), "1");

    // On = REAPER is paused (not while playing or stopped).
    private static readonly StateRule Paused = new(new TransportPaused(), "1");

    public override string Id => ActionId;

    public override LocalizedText Name => "Transport";

    public override LocalizedText Description => "Runs a REAPER transport command and shows whether it is on.";

    // To add a command: one line here. Icons: put e.g. record.png (+ record-dim.png) into Assets/Icons
    // and write Icon: "record".
    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        // Play buttons show what a press does: "play" while stopped, "stop" while playing.
        new("play-stop", "Play/Stop", "40044", Icon: "play", IconWhenOn: "stop", State: Playing),
        new("play-stop-move-editcursor", "Play/Stop, Move Edit Cursor", "40328",
            Icon: "play", IconWhenOn: "stop-move-editcursor", State: Playing),
        // Lit (full icon) only while paused; dimmed while playing or stopped.
        new("pause", "Pause", "1008", Icon: "pause", State: Paused),
        new("repeat", "Repeat", "1068", Icon: "repeat"),
        new("stop-at-loop-end", "Stop at loop end", "41834"),
        // Same icon either way (IconWhenOn = Icon: never dimmed); lit while playing.
        new("play-skip-selection", "Play, Skip Timeline Selection", "40317",
            Icon: "play", IconWhenOn: "play", State: Playing),
    ];
}
