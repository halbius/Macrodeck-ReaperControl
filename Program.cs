using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;
using ReaperControl;
using ReaperControl.Reaper;

var builder = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .RegisterIntegration<ReaperIntegration>();

// One shared connection: the integration fills it from the stored config, the actions use it.
builder.Services.AddSingleton<ReaperConnection>();

// Polls the on/off state of the REAPER actions shown on buttons (one instance, also a hosted service).
builder.Services.AddSingleton<CommandStateMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandStateMonitor>());

// Marker list + cursor position for the marker buttons, and the shared marker filter.
builder.Services.AddSingleton<MarkerMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarkerMonitor>());
builder.Services.AddSingleton<MarkerFilterState>();

// Cursor position for the timecode widget (polls only while a timecode tile is open).
builder.Services.AddSingleton<TimecodeMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TimecodeMonitor>());

var plugin = builder.Build();
await plugin.RunAsync();
