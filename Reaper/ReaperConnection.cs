using MacroDeck.Sdk.Http;

namespace ReaperControl.Reaper;

/// <summary>
/// The current REAPER client, shared by the integration and all actions (DI singleton).
/// The integration swaps it on every (re-)initialize; actions only read it. Capability
/// invocations run concurrently, so the swap is a single volatile reference write.
/// </summary>
public sealed class ReaperConnection : IDisposable
{
    private ReaperWebClient? _client;

    /// <summary>null until the integration has been initialized with a configuration.</summary>
    public ReaperWebClient? Client => Volatile.Read(ref _client);

    public ReaperActionCatalog Catalog { get; } = new();

    public void Configure(ReaperSettings settings, IHostHttp hostHttp)
    {
        var old = Interlocked.Exchange(ref _client, new ReaperWebClient(settings, hostHttp));
        old?.Dispose();
        Catalog.SetSource(settings.KeyBindingsFile);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _client, null)?.Dispose();
        Catalog.SetSource(null);
    }

    public void Dispose() => Reset();
}
