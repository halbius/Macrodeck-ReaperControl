using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using ReaperControl.Reaper;

namespace ReaperControl.ConfigFlow;

/// <summary>
/// Setup dialog in Macro Deck: where REAPER's web interface is reachable.
/// The host stores every submitted field under its name; secrets encrypted.
/// </summary>
internal sealed class ReaperConfigFlow : IConfigFlow
{
    public const string HostField = "host";
    public const string PortField = "port";
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string ResourcePathField = "resourcePath";

    private const string StepId = "connection";

    public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ConfigFlowResult.Step(BuildStep()));

    public async Task<ConfigFlowResult> SubmitAsync(
        string stepId,
        IReadOnlyDictionary<string, object?> input,
        IConfigFlowContext context,
        CancellationToken cancellationToken)
    {
        if (stepId != StepId)
        {
            return ConfigFlowResult.Error(BuildStep(), "Unknown step.");
        }

        var host = Values.Get(input, HostField) ?? ReaperSettings.DefaultHost;
        var portText = Values.Get(input, PortField) ?? ReaperSettings.DefaultPort.ToString();

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            return ConfigFlowResult.Error(BuildStep(), "Invalid port.",
                new Dictionary<string, LocalizedText> { [PortField] = "1 - 65535" });
        }

        var resourcePath = Values.Get(input, ResourcePathField);
        if (resourcePath is not null && !File.Exists(Path.Combine(resourcePath, "reaper-kb.ini")))
        {
            return ConfigFlowResult.Error(BuildStep(), "reaper-kb.ini not found in this folder.",
                new Dictionary<string, LocalizedText>
                {
                    [ResourcePathField] = "Options → Show REAPER resource path in explorer/finder"
                });
        }

        var settings = new ReaperSettings(host, port,
            Values.Get(input, UsernameField), Values.Get(input, PasswordField), resourcePath);

        // Test the connection now: a field error here beats a dead button later.
        try
        {
            using var client = new ReaperWebClient(settings, context.Http);
            await client.GetTransportAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ConfigFlowResult.Error(BuildStep(),
                $"REAPER is not reachable at {settings.BaseUri}. Is REAPER running and the web " +
                "browser interface enabled (Preferences → Control/OSC/web)?",
                new Dictionary<string, LocalizedText> { [PortField] = "No answer from REAPER" });
        }

        return ConfigFlowResult.Complete($"REAPER ({host}:{port})");
    }

    private static ConfigFlowStep BuildStep() => new()
    {
        StepId = StepId,
        Title = "Connect to REAPER",
        Description =
            "In REAPER: Options → Preferences → Control/OSC/web → Add → \"Web browser interface\". " +
            "Note the port (default 8080). Leave host at 127.0.0.1 if REAPER runs on this computer.",
        Fields =
        [
            ActionParameter.Text(HostField, "Host", placeholder: ReaperSettings.DefaultHost,
                defaultValue: ReaperSettings.DefaultHost, required: true),
            ActionParameter.Text(PortField, "Port", placeholder: "8080",
                defaultValue: ReaperSettings.DefaultPort.ToString(), required: true,
                validationRegex: @"^\d{1,5}$", maxLength: 5),
        ],
        AdvancedFields =
        [
            ActionParameter.Text(UsernameField, "Username",
                "Only if access control is set for the web interface in REAPER."),
            ActionParameter.Password(PasswordField, "Password"),
            ActionParameter.Folder(ResourcePathField, "REAPER resource path",
                "Folder containing reaper-kb.ini. Lets you pick custom actions and scripts by name. " +
                "Only works if REAPER runs on this computer."),
        ]
    };
}
