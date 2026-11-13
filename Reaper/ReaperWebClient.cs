using System.Net.Http.Headers;
using System.Text;
using MacroDeck.Sdk.Http;

namespace ReaperControl.Reaper;

/// <summary>
/// Thin client for REAPER's web interface API.
/// Every request is <c>GET /_/cmd;cmd;cmd</c>; REAPER answers with tab-separated lines.
/// Commands can be numeric action ids (40044), named ids of custom actions, scripts and
/// extensions (_RS…, _SWS_…, _&lt;hash&gt;), or API commands (TRANSPORT, TRACK, MARKER, SET/…).
/// </summary>
public sealed class ReaperWebClient : IDisposable
{
    private readonly HttpClient _http;

    public ReaperWebClient(ReaperSettings settings, IHostHttp hostHttp, TimeSpan? timeout = null)
    {
        Settings = settings;
        _http = new HttpClient(new UserAgentHandler(hostHttp))
        {
            BaseAddress = settings.BaseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(3)
        };

        // REAPER's web interface access control uses HTTP basic auth.
        if (!string.IsNullOrEmpty(settings.Username))
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public ReaperSettings Settings { get; }

    /// <summary>Sends one or more commands in a single request and returns REAPER's raw reply.</summary>
    public Task<string> SendAsync(IEnumerable<string> commands, CancellationToken cancellationToken) =>
        SendEncodedAsync(
            commands
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                // Keep '/' (API paths like SET/TRACK/1/MUTE/-1); escape everything else.
                .Select(c => string.Join('/', c.Split('/').Select(Uri.EscapeDataString))),
            cancellationToken);

    /// <summary>Like <see cref="SendAsync(IEnumerable{string},CancellationToken)" />, parts already URL-encoded.</summary>
    public async Task<string> SendEncodedAsync(IEnumerable<string> encodedParts, CancellationToken cancellationToken)
    {
        var parts = encodedParts.ToArray();
        if (parts.Length == 0)
        {
            throw new ArgumentException("No command given.", nameof(encodedParts));
        }

        using var response = await _http.GetAsync("_/" + string.Join(';', parts), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// The encoded request part that sets an ext state. Section, key and value are encoded as a
    /// whole, so they may contain '/' or ';' (e.g. preset names).
    /// </summary>
    public static string EncodeSetExtState(string section, string key, string value) =>
        $"SET/EXTSTATE/{Uri.EscapeDataString(section)}/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(value)}";

    public Task<string> SendAsync(string commandList, CancellationToken cancellationToken) =>
        SendAsync(SplitCommandList(commandList), cancellationToken);

    /// <summary>Cheap round-trip used to test the connection: asks for the transport state.</summary>
    public async Task<TransportState> GetTransportAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(["TRANSPORT"], cancellationToken);
        return ReaperResponseParser.ParseTransport(reply)
               ?? throw new InvalidDataException("REAPER did not answer with a TRANSPORT line.");
    }

    public async Task<IReadOnlyList<ReaperTrack>> GetTracksAsync(CancellationToken cancellationToken) =>
        ReaperResponseParser.ParseTracks(await SendAsync(["TRACK"], cancellationToken));

    public async Task<IReadOnlyList<ReaperMarker>> GetMarkersAsync(CancellationToken cancellationToken) =>
        ReaperResponseParser.ParseMarkers(await SendAsync(["MARKER"], cancellationToken));

    /// <summary>
    /// Reads several values in one request (action toggle states, ext states), keyed by
    /// <see cref="StateSource.Key" />. Toggle states without an answer are missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetStatesAsync(
        IReadOnlyList<StateSource> sources, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(sources.Select(s => s.Request).Distinct(), cancellationToken);
        return ReaperResponseParser.ParseStates(reply, sources);
    }

    /// <summary>
    /// Runs commands and reads values in one request. REAPER handles the parts in order,
    /// so the values are the ones after the commands ran.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SendAndGetStatesAsync(
        IReadOnlyList<string> commands, IReadOnlyList<StateSource> sources, CancellationToken cancellationToken)
    {
        var reply = await SendAsync([.. commands, .. sources.Select(s => s.Request).Distinct()], cancellationToken);
        return ReaperResponseParser.ParseStates(reply, sources);
    }

    /// <summary>
    /// Writes a value into REAPER's ExtState (not persisted). A ReaScript can pick it up with
    /// reaper.GetExtState(section, key) - the way to hand parameters to scripts, e.g. for marker edits.
    /// </summary>
    public Task SetExtStateAsync(string section, string key, string value, CancellationToken cancellationToken) =>
        SendEncodedAsync([EncodeSetExtState(section, key, value)], cancellationToken);

    /// <summary>"40044; _SWS_ABOUT ;_RS123" → ["40044", "_SWS_ABOUT", "_RS123"]</summary>
    public static IReadOnlyList<string> SplitCommandList(string commandList) =>
        commandList.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose() => _http.Dispose();

    /// <summary>Sends the User-Agent the user configured in Macro Deck, read per request.</summary>
    private sealed class UserAgentHandler(IHostHttp hostHttp) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.UserAgent.Count == 0)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", hostHttp.UserAgent);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
