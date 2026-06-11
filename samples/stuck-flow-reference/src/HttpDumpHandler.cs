// DelegatingHandler that logs the full URL + request body (and response body
// for botframework.com) for every outbound HTTP call. Used to capture exactly
// what channelId value the SDK is sending to BF Token Service so we can prove
// where the "msteams:COPILOT" leak happens.
//
// Registered via IHttpMessageHandlerBuilderFilter so it applies to every named
// HttpClient the Agents SDK creates (RestChannelServiceClientFactory etc.).

using Microsoft.Extensions.Http;

namespace OAuthPocDotnet;

public class HttpDumpHandler : DelegatingHandler
{
    private readonly ILogger<HttpDumpHandler> _log;
    public HttpDumpHandler(ILogger<HttpDumpHandler> log) { _log = log; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        var isBf = host.EndsWith("botframework.com", StringComparison.OrdinalIgnoreCase);

        if (isBf)
        {
            string? reqBody = null;
            if (request.Content != null)
            {
                try { reqBody = await request.Content.ReadAsStringAsync(ct); }
                catch (Exception ex) { reqBody = $"(error reading body: {ex.Message})"; }
            }
            _log.LogInformation("[HTTP DUMP REQ] {Method} {Url}\n  body: {Body}",
                request.Method, request.RequestUri, reqBody ?? "(none)");
        }

        var resp = await base.SendAsync(request, ct);

        if (isBf)
        {
            string? respBody = null;
            try
            {
                respBody = await resp.Content.ReadAsStringAsync(ct);
                resp.Content = new StringContent(respBody,
                    System.Text.Encoding.UTF8,
                    resp.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
            catch (Exception ex) { respBody = $"(error reading body: {ex.Message})"; }

            _log.LogInformation("[HTTP DUMP RESP] {Status} {Url}\n  body: {Body}",
                (int)resp.StatusCode, request.RequestUri, respBody ?? "(none)");
        }

        return resp;
    }
}

public class HttpDumpFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly ILoggerFactory _loggerFactory;
    public HttpDumpFilter(ILoggerFactory loggerFactory) { _loggerFactory = loggerFactory; }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Add(
                new HttpDumpHandler(_loggerFactory.CreateLogger<HttpDumpHandler>()));
        };
    }
}
