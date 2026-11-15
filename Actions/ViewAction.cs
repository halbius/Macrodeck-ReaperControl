using MacroDeck.Localization;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "View": shows/hides one of REAPER's windows from the View menu (Routing Matrix, Track Manager,
/// ...), chosen in the button's settings. Plain toggle without on/off feedback; the button shows the
/// window's name.
/// </summary>
public sealed class ViewAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<ViewAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "view";

    public override string Id => ActionId;

    public override LocalizedText Name => "View";

    public override LocalizedText Description => "Shows or hides a REAPER window from the View menu.";

    protected override bool ShowsState => false;

    // The two fields of Go To → Fwd / Back. The first choice of each is the default.
    private static readonly IReadOnlyList<ListField> MoveFields =
    [
        new("unit", "Unit", [("measure", "Measure"), ("beat", "Beat")]),
        new("seek", "Seek", [("normal", "Normal"), ("noseek", "No seek")]),
    ];

    // In the order of REAPER's View menu. To add a window: one line (key, label, command id).
    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        new("master-track", "Master Track", "40075"),
        new("tempo-envelope", "Tempo Envelope", "41046"),
        new("floatin-mixer-master", "Floating Mixer Master", "41609"),
        new("monitoring-fx", "Monitoring FX", "41882", OnColor: "#c53030", OffColor: "#248842",
            OptionsLabel: "Options",
            Options:
            [
                new("monitoring-fx-show-chain", "Monitoring FX - Show Chain", "41882"),
                // Lit while the monitoring FX are bypassed (REAPER's own toggle state of 41884).
                new("monitoring-fx-toggle-bypass", "Monitoring FX - Toggle Bypass", "41884",
                    StateRule.Toggle("41884") ),
            ]
        
        ),
        new("transport", "Transport", "40259"),
        new("video", "Video", "50125"),
        new("docker", "Docker", "40279"),
        new("toolbar-docker", "Toolbar Docker", "41084"),

        new("media-explorer", "Media Explorer", "50124"),
        new("routing-matrix", "Routing Matrix", "40251"),
        new("grouping-matrix", "Track Grouping Matrix", "40768"),
        new("track-wiring", "Track Wiring", "42031"),
        new("project-bay", "Project Bay", "41157"),
        new("track-manager", "Track Manager", "40906"),
        new("group-manager", "Track Group Manager", "40327"),
        new("envelope-manager", "Envelope Manager", "42678"),
        new("screensets", "Screensets / Layouts", "40422"),
        new("marker-manager", "Region / Marker Manager", "40326"),
        new("ruler-lanes", "Ruler Lane Manager", "43542"),
        new("item-properties", "Media Item Properties", "41589"),
        new("big-clock", "Big Clock", "40378",
            OptionsLabel: "Options",
            Options:
            [
                new("show-big-clock", "Show Big Clock", "40378"),
                new("show-big-clock-extended", "Big Clock Extended Display", "1101"),
            ]
        
        ),
        new("goto", "Go To", "40150",
            OptionsLabel: "Options",
            Options:
            [
                new("goto-playcursor", "Go To Play Cursor", "40150"),
                new("goto-editcursor", "Go To Edit Cursor", "40151"),
                new("jumpto", "Jump To", "40069"),
                // Fwd / Back: two more fields (Unit, Seek) pick one of four commands.
                new("fwd", "Fwd", Fields: MoveFields, Variants:
                [
                    //  key                 button label                  command id   Unit       Seek
                    new("fwd-one-measure", "Fwd One Measure",             "41042", ["measure", "normal"]),
                    new("fwd-measure-ns",  "Fwd One Measure (no seek)",   "TODO",  ["measure", "noseek"]),
                    new("fwd-one-beat",    "Fwd One Beat",                "TODO",  ["beat",    "normal"]),
                    new("fwd-beat-ns",     "Fwd One Beat (no seek)",      "TODO",  ["beat",    "noseek"]),
                ]),
                new("back", "Back", Fields: MoveFields, Variants:
                [
                    new("back-one-measure", "Back One Measure",           "41043", ["measure", "normal"]),
                    new("back-measure-ns",  "Back One Measure (no seek)", "TODO",  ["measure", "noseek"]),
                    new("back-one-beat",    "Back One Beat",              "TODO",  ["beat",    "normal"]),
                    new("back-beat-ns",     "Back One Beat (no seek)",    "TODO",  ["beat",    "noseek"]),
                ]),
            ]),
    ];
}
