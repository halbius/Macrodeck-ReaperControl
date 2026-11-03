using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Load render preset": applies a render preset and opens the render dialog - without rendering.
/// The same action can sit on several buttons, each with its own preset.
/// </summary>
/// <remarks>
/// REAPER has no action or API call for render presets. So the plugin writes the preset name to
/// ExtState "ReaperControl"/"RenderPreset" and runs ReaperScripts/ReaperControl_LoadRenderPreset.lua
/// in the same request; that script applies the preset (via cfillion's "Apply render preset") and
/// opens the render dialog. The script's command id is looked up in reaper-kb.ini by file name.
/// </remarks>
public sealed class LoadRenderPresetAction(ReaperConnection connection, ILogger<LoadRenderPresetAction> logger)
    : IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "load-render-preset";

    public const string ScriptFileName = "ReaperControl_LoadRenderPreset.lua";

    private const string PresetParameter = "preset";

    // State id persisted in profiles - never rename.
    private const string NoPresetStateId = "no-preset";

    public string Id => ActionId;

    public LocalizedText Name => "Load render preset";

    public LocalizedText Description =>
        "Opens REAPER's render dialog with the chosen render preset loaded. Does not start rendering.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.DynamicChoice(PresetParameter, "Render preset",
                "Presets from reaper-render.ini (needs the REAPER resource path in the plugin settings).",
                required: true)
            .AsSearchable()
    ];

    public Task<DynamicOptionsResult> GetDynamicOptionsAsync(
        DynamicOptionsContext context, CancellationToken cancellationToken)
    {
        var resourcePath = connection.Client?.Settings.ResourcePath;
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return Task.FromResult(new DynamicOptionsResult
            {
                Options = [],
                AllowsCustomValue = true,
                Error = "Set the REAPER resource path in the plugin settings to list the render presets."
            });
        }

        var names = RenderPresetCatalog.GetNames(resourcePath)
            .Where(n => string.IsNullOrEmpty(context.Filter) ||
                        n.Contains(context.Filter, StringComparison.OrdinalIgnoreCase))
            .Select(n => new ActionParameterOption { Value = n, Label = n })
            .ToList();

        if (names.Count == 0 && string.IsNullOrEmpty(context.Filter))
        {
            return Task.FromResult(new DynamicOptionsResult
            {
                Options = [],
                AllowsCustomValue = true,
                Error = "No render presets found in reaper-render.ini."
            });
        }

        return Task.FromResult(new DynamicOptionsResult
        {
            Options = names,
            AllowsCustomValue = true,
            CacheSeconds = 10
        });
    }

    /// <summary>
    /// Puts the preset name on the button: one state whose default label is the preset name.
    /// Macro Deck applies a state's default appearance only to a state id it sees for the first
    /// time, so the id is derived from the name - choosing another preset gives a new id and with
    /// it the new label, while a label the user edited by hand stays as long as the preset does.
    /// </summary>
    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Also called with half-filled drafts while the button is being edited. Macro Deck offers
        // "use provided states" only when the action is added - before a preset is chosen - and only
        // if states come back then. So answer with a placeholder; choosing a preset replaces it.
        var preset = Values.Get(parameters, PresetParameter);
        if (preset is null)
        {
            return Task.FromResult<ActionStateSnapshot?>(
                new ActionStateSnapshot([new ActionStateDefinition(NoPresetStateId, "Render preset")], NoPresetStateId));
        }

        var stateId = PresetStateId(preset);
        var state = new ActionStateDefinition(stateId, preset)
        {
            DefaultAppearance = new ActionStateAppearance { Label = preset }
        };

        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([state], stateId));
    }

    // Nothing changes by itself; the host clamps this to at most 2 minutes.
    public TimeSpan StatePollInterval => TimeSpan.FromMinutes(2);

    /// <summary>"preset-" + FNV-1a hash of the name: stable, and valid lowercase kebab-case.</summary>
    internal static string PresetStateId(string preset)
    {
        var hash = 2166136261u;
        foreach (var c in preset)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return $"preset-{hash:x8}";
    }

    public IActionExecutor CreateExecutor() => new Executor(connection, logger);

    private sealed class Executor(ReaperConnection connection, ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var preset = Values.Get(context.Parameters, PresetParameter);
            if (preset is null)
            {
                return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "No render preset chosen.");
            }

            var client = connection.Client;
            if (client is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotConfigured, "REAPER connection is not set up yet.");
            }

            var scriptId = connection.Catalog.FindScriptCommandId(ScriptFileName);
            if (scriptId is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotFound,
                    $"Load {ScriptFileName} into REAPER's action list first (and set the resource path " +
                    "in the plugin settings).");
            }

            try
            {
                // One request, handled in order: hand over the name, then run the script.
                await client.SendEncodedAsync(
                    [
                        ReaperWebClient.EncodeSetExtState("ReaperControl", "RenderPreset", preset),
                        Uri.EscapeDataString(scriptId)
                    ],
                    context.CancellationToken);

                // REAPER ran the script, but cannot report whether the preset existed.
                return ActionResult.Success();
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return ActionResult.Failed(ActionErrorCodes.Timeout, "REAPER did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Loading render preset '{Preset}' failed", preset);
                return ActionResult.Failed(ActionErrorCodes.NotConnected,
                    "REAPER is not reachable. Is it running with the web interface enabled?");
            }
        }
    }
}
