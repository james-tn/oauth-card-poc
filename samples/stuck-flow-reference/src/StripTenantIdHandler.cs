// DelegatingHandler that strips `conversation.tenantId` from the `state=`
// query parameter of outbound calls to BF Token Service (api.botframework.com).
// Used to test the hypothesis that BF Token Service's PostSignInCallback page
// chooses between silent postMessage and the static magic-code page based on
// whether the state payload contains a tenantId (enterprise context detection).
//
// Empirically, the legacy Python botbuilder SDK omits tenantId from the
// serialized state payload, while the new Microsoft.Agents.Builder SDK
// includes it. CEAs built on the new SDK in M365 Copilot Web group threads
// see the magic-code page where legacy bots see silent SSO. This handler
// reproduces the legacy behavior (omit tenantId) at the wire level so we can
// check whether the magic-code page disappears.
//
// Gated on STRIP_TENANT_ID=true env var so the default behavior is unchanged.
// Registered via IHttpMessageHandlerBuilderFilter alongside HttpDumpHandler.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Http;

namespace OAuthPocDotnet;

public class StripTenantIdHandler : DelegatingHandler
{
    private readonly ILogger<StripTenantIdHandler> _log;

    public StripTenantIdHandler(ILogger<StripTenantIdHandler> log) { _log = log; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            if (request.RequestUri is { } uri
                && uri.Host.EndsWith("botframework.com", StringComparison.OrdinalIgnoreCase))
            {
                var modified = TryStripTenantIdFromState(uri);
                if (modified is not null)
                {
                    _log.LogInformation(
                        "[STRIP-TENANT-ID] rewrote {Path}: tenantId/requestId removed from state=",
                        uri.AbsolutePath);
                    request.RequestUri = modified;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("[STRIP-TENANT-ID] error while rewriting request: {Ex}", ex.Message);
        }

        return await base.SendAsync(request, ct);
    }

    private static Uri? TryStripTenantIdFromState(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var state = query["state"];
        if (string.IsNullOrEmpty(state)) return null;

        // Decode base64 (state can be either standard or url-safe base64; try standard first).
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(state);
        }
        catch
        {
            // Try url-safe / padded fallback.
            var s = state.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            bytes = Convert.FromBase64String(s);
        }

        var json = Encoding.UTF8.GetString(bytes);
        var node = JsonNode.Parse(json);
        if (node is null) return null;

        // The shape (verified empirically) is:
        //   { connectionName, conversation: { ..., conversation: { tenantId, ... }, ... }, msAppId }
        // i.e., the OUTER "conversation" is a ConversationReference, the INNER one is a
        // ConversationAccount and it's the one that carries tenantId.
        var innerConv = node["conversation"]?["conversation"] as JsonObject;
        var outerConv = node["conversation"] as JsonObject;

        bool changed = false;
        if (innerConv is not null && innerConv.ContainsKey("tenantId"))
        {
            innerConv.Remove("tenantId");
            changed = true;
        }
        // Also strip the top-level conversation.requestId — the new SDK
        // includes a per-request trace id here that the legacy Python SDK
        // omits. Testing whether BF Token Service's PostSignInCallback
        // treats its presence as a signal.
        if (outerConv is not null && outerConv.ContainsKey("requestId"))
        {
            outerConv.Remove("requestId");
            changed = true;
        }
        if (!changed) return null;

        var modifiedJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var modifiedState = Convert.ToBase64String(Encoding.UTF8.GetBytes(modifiedJson));

        query["state"] = modifiedState;
        var newQuery = query.ToString();
        var builder = new UriBuilder(uri) { Query = newQuery };
        return builder.Uri;
    }
}

public class StripTenantIdFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly ILoggerFactory _loggerFactory;
    public StripTenantIdFilter(ILoggerFactory loggerFactory) { _loggerFactory = loggerFactory; }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Add(
                new StripTenantIdHandler(_loggerFactory.CreateLogger<StripTenantIdHandler>()));
        };
    }
}
