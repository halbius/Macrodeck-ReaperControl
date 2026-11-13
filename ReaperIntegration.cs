using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Variables;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using Microsoft.Extensions.Logging;
using ReaperControl.Actions;
using ReaperControl.ConfigFlow;
using ReaperControl.Reaper;
using ReaperControl.Widgets;

namespace ReaperControl;

/// <summary>
/// The plugin's integration: declares the actions and the setup flow, and connects
/// to REAPER from the stored configuration.
/// </summary>
/// <remarks>
/// InitializeAsync/ShutdownAsync can run several times per process (reconnects, config
/// changes), so both are idempotent.
/// </remarks>
public sealed class ReaperIntegration : IPluginIntegration, IConfigFlowProvider, IWidgetTypeProvider, IUiProvider, IVariableProvider
{
    private readonly ReaperConnection _connection;
    private readonly ILogger<ReaperIntegration> _logger;
    private readonly TimecodeMonitor _timecode;
    private readonly MarkerFilterState _markerFilter;

    public ReaperIntegration(
        ReaperConnection connection,
        CommandStateMonitor stateMonitor,
        MarkerMonitor markerMonitor,
        MarkerFilterState markerFilter,
        TimecodeMonitor timecode,
        ILoggerFactory loggerFactory)
    {
        _connection = connection;
        _timecode = timecode;
        _markerFilter = markerFilter;
        _logger = loggerFactory.CreateLogger<ReaperIntegration>();

        // Read by the host before InitializeAsync: build without I/O.
        var fixedLogger = loggerFactory.CreateLogger<FixedCommandAction>();
        var markerLogger = loggerFactory.CreateLogger<MarkerButtonAction>();
        Actions =
        [
            new RunCommandAction(connection, stateMonitor, loggerFactory.CreateLogger<RunCommandAction>()),
            new LoadRenderPresetAction(connection, loggerFactory.CreateLogger<LoadRenderPresetAction>()),
            new MarkerButtonAction(connection, markerMonitor, markerFilter, markerLogger),
            new MarkerFilterAction(markerFilter, markerMonitor),
            new NextPreviousMarkerAction(connection, markerMonitor, markerFilter,
                loggerFactory.CreateLogger<NextPreviousMarkerAction>()),
            new ItemRulerAction(connection, stateMonitor, loggerFactory.CreateLogger<ItemRulerAction>()),
            new TransportAction(connection, stateMonitor, loggerFactory.CreateLogger<TransportAction>()),
            new ItemAction(connection, stateMonitor, loggerFactory.CreateLogger<ItemAction>()),
            new TrackAction(connection, stateMonitor, loggerFactory.CreateLogger<TrackAction>()),
            .. FixedCommands.All.Select(c => new FixedCommandAction(c, connection, stateMonitor, fixedLogger))
        ];
    }

    public IReadOnlyList<IActionDefinition> Actions { get; }

    // One REAPER per Macro Deck is enough for now.
    public bool AllowsMultipleConfigurations => false;

    public IConfigFlow CreateConfigFlow() => new ReaperConfigFlow();

    // ----- Variables -----

    // Id persisted in profiles - never rename.
    private const string NavFilteredVariableId = "marker-nav-filtered";

    public IReadOnlyList<VariableDefinition> Variables { get; } =
    [
        VariableDefinition.Eager("reaper_marker_nav_filtered", VariableType.Boolean,
                refreshInterval: TimeSpan.FromSeconds(1))
            with
            {
                Id = NavFilteredVariableId,
                DisplayName = "REAPER: next/previous marker filtered",
                Description = "True while \"Go to next / previous marker\" buttons follow the marker filter."
            }
    ];

    public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(localId == NavFilteredVariableId
            ? VariableReading.Of(_markerFilter.NavigationFollowsFilter)
            : VariableReading.Unavailable);

    // ----- Widget types (the "REAPER timecode" tile) -----

    public string ProviderName => "REAPER Control";

    public Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default) =>
        context.RegisterWidgetTypeAsync(TimecodeWidget.Descriptor, cancellationToken);

    public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => [TimecodeWidget.Descriptor];

    public IReadOnlyList<UiSurfaceDeclaration> Surfaces => TimecodeWidget.Surfaces;

    public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(TimecodeWidget.CreateSession(request.Surface, _timecode));

    public async Task InitializeAsync(IIntegrationContext context)
    {
        var settings = await LoadSettingsAsync(context.Config);
        _connection.Configure(settings, context.Http, context);
        _logger.LogInformation("REAPER target: {Uri}", settings.BaseUri);
        _logger.LogInformation("Button icons: {Icons}", IconLibrary.Describe());
    }

    public Task ShutdownAsync()
    {
        _connection.Reset();
        return Task.CompletedTask;
    }

    private static async Task<ReaperSettings> LoadSettingsAsync(IIntegrationConfig config)
    {
        var entries = await config.GetEntriesAsync();
        if (entries.Count == 0)
        {
            return ReaperSettings.Default;
        }

        var id = entries[0].Id;
        var host = Values.NullIfBlank(await config.GetStringAsync(id, ReaperConfigFlow.HostField))
                   ?? ReaperSettings.DefaultHost;
        var port = int.TryParse(await config.GetStringAsync(id, ReaperConfigFlow.PortField), out var p)
            ? p
            : ReaperSettings.DefaultPort;

        return new ReaperSettings(
            host,
            port,
            Values.NullIfBlank(await config.GetStringAsync(id, ReaperConfigFlow.UsernameField)),
            await config.GetSecretAsync(id, ReaperConfigFlow.PasswordField),
            Values.NullIfBlank(await config.GetStringAsync(id, ReaperConfigFlow.ResourcePathField)));
    }
}
