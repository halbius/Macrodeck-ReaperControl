using MacroDeck.Sdk;
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

    private IIntegrationContext? _hostContext;

    /// <summary>Macro Deck's context of the running integration; null between sessions.</summary>
    public IIntegrationContext? HostContext => Volatile.Read(ref _hostContext);

    /// <summary>
    /// Tells Macro Deck to re-read the icons of buttons running <paramref name="actionIds" /> now,
    /// instead of at its next icon poll. Fire-and-forget.
    /// </summary>
    public void InvalidateIcons(params string[] actionIds)
    {
        if (HostContext is not { } context)
        {
            return;
        }

        foreach (var id in actionIds)
        {
            _ = context.Widgets.InvalidateIconAsync(id).ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void Configure(ReaperSettings settings, IHostHttp hostHttp, IIntegrationContext? hostContext = null)
    {
        Volatile.Write(ref _hostContext, hostContext);
        var old = Interlocked.Exchange(ref _client, new ReaperWebClient(settings, hostHttp));
        old?.Dispose();
        Catalog.SetSource(settings.KeyBindingsFile);
    }

    public void Reset()
    {
        Volatile.Write(ref _hostContext, null);
        Interlocked.Exchange(ref _client, null)?.Dispose();
        Catalog.SetSource(null);
    }

    public void Dispose() => Reset();
}
