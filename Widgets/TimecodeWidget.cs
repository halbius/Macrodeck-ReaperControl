using System.Globalization;
using System.Text.Json;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using ReaperControl.Reaper;

namespace ReaperControl.Widgets;

/// <summary>
/// "REAPER timecode": a deck tile showing the play/edit cursor position. The format is chosen in the
/// widget's configuration; the default follows REAPER's transport window.
/// </summary>
public static class TimecodeWidget
{
    // Widget type id: stored in every placed widget - never rename after release.
    public const string LocalId = "reaper-timecode";

    private const string FormatKey = "format";
    private const string ShowPlayRateKey = "showPlayRate";
    private const string ShowRecordModeKey = "showRecordMode";
    private const string FlowsKey = "flows";

    private static readonly JsonElement EmptyFlows = JsonDocument.Parse("[]").RootElement.Clone();
    private const string DefaultBackground = "#111827";

    /// <summary>What a tile shows: the timecode, the info line (or a hint) and REAPER's play state.</summary>
    private sealed record TileText(string Main, string Info = "", int PlayState = 0);

    /// <summary>Which extras the tile shows besides the time.</summary>
    private sealed record TileOptions(bool ShowPlayRate, bool ShowRecordMode);

    // Timecode color per play state; stopped (0) uses the label color.
    private static string? PlayStateColor(int playState) => playState switch
    {
        1 => "#48bb78", // playing: green
        2 => "#ecc94b", // paused: yellow
        5 => "#e53e3e", // recording: red
        6 => "#ed8936", // record-paused: orange
        _ => null
    };

    /// <param name="Key">Stored value - never rename.</param>
    /// <param name="ScriptMode">REAPER time format mode the script supplies; null = no script needed.</param>
    private sealed record Format(string Key, string Name, int? ScriptMode, string Sample);

    // To add a format: one line here (and the mode in ReaperControl_Timecode.lua / TimecodeMonitor.ScriptModes).
    private static readonly IReadOnlyList<Format> Formats =
    [
        new("transport", "Like transport window", null, "1.1.00"),
        new("time", "Minutes:Seconds", 0, "0:00.000"),
        new("beats-time", "Measures.Beats + Time", 1, "1.1.00 / 0:00.000"),
        new("beats", "Measures.Beats", 2, "1.1.00"),
        new("seconds", "Seconds", 3, "0.000"),
        new("samples", "Samples", 4, "0"),
        new("hmsf", "Hours:Minutes:Seconds:Frames", 5, "00:00:00:00"),
        new("frames", "Absolute frames", 8, "0"),
    ];

    private const string DataSchema = """
        {
          "type": "object",
          "properties": {
            "format": { "type": "string" },
            "showPlayRate": { "type": "boolean" },
            "showRecordMode": { "type": "boolean" },
            "border": { "type": "object" },
            "backgroundColor": { "type": "string" },
            "labelColor": { "type": "string" },
            "flows": { "anyOf": [ { "type": "string" }, { "type": "array" } ] }
          }
        }
        """;

    public static WidgetTypeDescriptor Descriptor { get; } = new(
        LocalId,
        "REAPER timecode",
        "Shows REAPER's play/edit cursor position.",
        DefaultData: """{"format":"transport","showPlayRate":true,"showRecordMode":true}""",
        DataSchema: DataSchema,
        HasConfiguration: true)
    {
        AppearanceProperties = [WidgetAppearanceProperty.BackgroundColor, WidgetAppearanceProperty.LabelColor],
        // A press runs the widget's own action flows (Short Press, Long Press, ...) like a button.
        SupportsFlows = true
    };

    public static IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
    [
        new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
        new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
        new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive }
    ];

    /// <summary>A session for one of this widget's surfaces, or null when the surface is not ours.</summary>
    public static IUiSession? CreateSession(UiSurface surface, TimecodeMonitor monitor)
    {
        if (IsConfiguration(surface))
        {
            return new ViewSession(new UiView(surface, BuildConfig(UiWidgetSurface.Data(surface))));
        }

        if (!IsWidget(surface))
        {
            return null;
        }

        var data = UiWidgetSurface.Data(surface);
        var format = FormatOf(data);
        var options = OptionsOf(data);
        var appearance = UiWidgetAppearance.Read(data);

        if (UiWidgetSurface.IsSampleRequested(surface))
        {
            var sample = new UiState<TileText>(new TileText(format.Sample,
                Info(options, new TimecodeSnapshot("", "", 0, new Dictionary<int, string>(), 0, "1.00", "normal"))));
            return new ViewSession(new UiView(surface, BuildView(sample, options, appearance)));
        }

        return new TimecodeSession(surface, format, options, appearance, monitor);
    }

    // The qualified id is "<plugin id>::reaper-timecode"; matching the local part keeps it working
    // whatever id the manifest gives the plugin.
    private static bool IsWidget(UiSurface surface) =>
        surface.Kind is UiSurfaceKinds.Widget or UiSurfaceKinds.Preview
        && ReadAttribute(surface, UiWidgetSurfaceAttributes.WidgetType)?.EndsWith("::" + LocalId) == true;

    private static bool IsConfiguration(UiSurface surface) =>
        surface.Kind == UiSurfaceKinds.Config
        && ReadAttribute(surface, UiConfigSurfaceAttributes.EntryPoint) == UiConfigEntryPoints.WidgetConfig
        && ReadAttribute(surface, UiConfigSurfaceAttributes.WidgetType)?.EndsWith("::" + LocalId) == true;

    private static TileOptions OptionsOf(JsonElement data) => new(
        ReadBool(data, ShowPlayRateKey) ?? true,
        ReadBool(data, ShowRecordModeKey) ?? true);

    private static UiElement BuildView(
        UiState<TileText> text, TileOptions options, UiWidgetAppearanceValues appearance)
    {
        var color = appearance.LabelColor ?? "#f9fafb";
        UiElement main = new UiTextRun
        {
            Key = "timecode",
            Text = UiText.From(() => text.Value.Main),
            // Same width for every digit and a fixed reservation, so the time does not jiggle while
            // the milliseconds count.
            Digits = UiValue.From(() => DigitWidth(text.Value.Main)),
            Size = 0.26,
            MinSize = 0.06,
            Weight = UiComponentTextWeights.Bold,
            // Green while playing, yellow paused, red recording, orange record-paused.
            Color = UiValue.From(() => PlayStateColor(text.Value.PlayState) ?? color),
            Align = UiComponentAlignments.Center,
            MaxLines = 1
        };

        // Playback rate / record mode, or a hint (offline, script missing). Always there, so the
        // hints show even with both extras switched off.
        UiElement info = new UiTextRun
        {
            Key = "info",
            Text = UiText.From(() => text.Value.Info),
            Size = 0.08,
            MinSize = 0.05,
            Role = UiComponentTextRoles.Secondary,
            Color = color,
            Align = UiComponentAlignments.Center,
            MaxLines = 1
        };
        var children = new List<UiElement> { main, info };

        return new UiStack
        {
            Key = "root",
            Justify = UiComponentJustify.Center,
            Align = UiComponentAlignments.Center,
            Gap = 0.03,
            Padding = 0.06,
            Background = appearance.BackgroundColor ?? DefaultBackground,
            Children = children
        };
    }

    private static UiElement BuildConfig(JsonElement data)
    {
        var format = new UiState<string>(FormatOf(data).Key);
        var showPlayRate = new UiState<bool>(ReadBool(data, ShowPlayRateKey) ?? true);
        var showRecordMode = new UiState<bool>(ReadBool(data, ShowRecordModeKey) ?? true);
        var flows = new UiState<JsonElement>(ReadFlows(data));

        return new UiWidgetConfiguration
        {
            // Macro Deck's own actions editor (Short Press, Long Press, Double Tap, ...).
            Editor = new UiWidgetEditor
            {
                Key = "editor",
                Children = [new UiActionsListEditor { Key = FlowsKey, Binding = Bind.To(flows), CanRun = true }]
            },
            Key = "root",
            Properties = new UiWidgetProperties
            {
                Key = "properties",
                Children =
                [
                    new UiHeading { Key = "heading", Text = "REAPER timecode" },
                    new UiChoiceInput
                    {
                        Key = FormatKey,
                        Label = "Format",
                        Description = "Formats other than \"Like transport window\", \"Measures.Beats\", " +
                                      "\"Minutes:Seconds\" and \"Seconds\" need ReaperControl_Timecode.lua running in REAPER.",
                        Binding = Bind.To(format),
                        Options = UiValue.Of<IReadOnlyList<UiOption>>([.. Formats.Select(f => UiOption.Of(f.Key, f.Name))])
                    },
                    new UiBooleanInput
                    {
                        Key = ShowPlayRateKey,
                        Label = "Show playback rate",
                        Description = "Needs ReaperControl_Timecode.lua running in REAPER.",
                        Binding = Bind.To(showPlayRate)
                    },
                    new UiBooleanInput
                    {
                        Key = ShowRecordModeKey,
                        Label = "Show record mode",
                        Description = "Normal, time selection auto-punch or selected item auto-punch.",
                        Binding = Bind.To(showRecordMode)
                    },
                    UiWidgetAppearance.Section(data,
                        UiWidgetAppearanceFields.Border
                        | UiWidgetAppearanceFields.BackgroundColor
                        | UiWidgetAppearanceFields.TransparentBackground
                        | UiWidgetAppearanceFields.LabelColor)
                ]
            }
        };
    }

    /// <summary>The info line: playback rate and record mode, as far as they are switched on and known.</summary>
    private static string Info(TileOptions options, TimecodeSnapshot snapshot)
    {
        var parts = new List<string>(2);
        if (options.ShowPlayRate && snapshot.PlayRate.Length > 0)
        {
            parts.Add($"Rate {snapshot.PlayRate}");
        }

        if (options.ShowRecordMode && snapshot.RecordMode switch
            {
                "normal" => "Rec: Normal",
                "time" => "Rec: Time punch",
                "item" => "Rec: Item punch",
                _ => null
            } is { } mode)
        {
            parts.Add(mode);
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>Main text, caption and info line for the current position.</summary>
    private static TileText Render(Format format, TileOptions options, TimecodeSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return new TileText("--", "REAPER offline");
        }

        // Script values first: REAPER formats them itself, with the project's settings.
        if (format.ScriptMode is { } mode && snapshot.ScriptFormats.TryGetValue(mode, out var scripted)
            && scripted.Length > 0)
        {
            return new TileText(scripted, Info(options, snapshot), snapshot.PlayState);
        }

        var main = format.Key switch
        {
            "transport" => snapshot.TransportFormat,
            "beats" when snapshot.Beats.Length > 0 => snapshot.Beats,
            "seconds" => snapshot.Seconds.ToString("0.000", CultureInfo.InvariantCulture),
            "time" => MinutesSeconds(snapshot.Seconds),
            _ => null
        };
        return main is null
            ? new TileText("--", "needs ReaperControl_Timecode.lua", snapshot.PlayState)
            : new TileText(main, Info(options, snapshot), snapshot.PlayState);
    }

    /// <summary>Width of a time text in digit widths (separators count as half a digit).</summary>
    private static double DigitWidth(string text) =>
        text.Sum(c => char.IsDigit(c) ? 1.0 : c is '.' or ':' or ',' ? 0.5 : 0.8);

    private static string MinutesSeconds(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        var t = TimeSpan.FromSeconds(Math.Abs(seconds));
        return t.TotalHours >= 1
            ? $"{sign}{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
            : $"{sign}{t.Minutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static JsonElement ReadFlows(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(FlowsKey, out var flows))
        {
            return EmptyFlows;
        }

        if (flows.ValueKind == JsonValueKind.Array)
        {
            return flows.Clone();
        }

        if (flows.ValueKind == JsonValueKind.String && flows.GetString() is { Length: > 0 } raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // fall through: unreadable flows start empty
            }
        }

        return EmptyFlows;
    }

    private static Format FormatOf(JsonElement data) =>
        Formats.FirstOrDefault(f => f.Key == ReadString(data, FormatKey)) ?? Formats[0];

    private static string? ReadAttribute(UiSurface surface, string key) =>
        surface.Attributes.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? ReadBool(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v)
                                               && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    /// <summary>One live tile: follows the monitor until the deck closes it.</summary>
    private sealed class TimecodeSession : IUiSession
    {
        private readonly Format _format;
        private readonly TileOptions _options;
        private readonly TimecodeMonitor _monitor;
        private readonly UiState<TileText> _text;
        private readonly UiView _view;
        private readonly IDisposable _subscription;
        private readonly Lock _sync = new();
        private bool _disposed;

        public TimecodeSession(
            UiSurface surface, Format format, TileOptions options, UiWidgetAppearanceValues appearance, TimecodeMonitor monitor)
        {
            _format = format;
            _options = options;
            _monitor = monitor;
            _text = new UiState<TileText>(Render(format, options, monitor.Current));
            _view = new UiView(surface, BuildView(_text, options, appearance));
            _view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _view.HandlerFaulted += (_, fault)
                => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));

            _monitor.Changed += OnPositionChanged;
            _subscription = _monitor.Subscribe();
        }

        public event EventHandler? Changed;

        public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

        public UiTree BuildTree() => _view.Tree;

        public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

        public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                _disposed = true;
            }

            _monitor.Changed -= OnPositionChanged;
            _subscription.Dispose();
            _view.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnPositionChanged(object? sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var next = Render(_format, _options, _monitor.Current);
                if (next != _text.Value)
                {
                    using (_view.Batch())
                    {
                        _text.Set(next);
                    }
                }
            }
        }
    }

    /// <summary>Adapter between a UiView and IUiSession (static views: preview, configuration).</summary>
    private sealed class ViewSession : IUiSession
    {
        private readonly UiView _view;

        public ViewSession(UiView view)
        {
            _view = view;
            _view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _view.HandlerFaulted += (_, fault)
                => Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));
        }

        public event EventHandler? Changed;

        public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

        public UiTree BuildTree() => _view.Tree;

        public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

        public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

        public ValueTask DisposeAsync()
        {
            _view.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
