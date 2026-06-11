// ASP.NET middleware with two functions, both controlled by env vars:
//
// 1. INBOUND_HTTP_DUMP=true (existing): log every inbound POST body so we can
//    observe the exact sequence of activities Copilot Chat sends (notably
//    signin/tokenExchange and signin/verifyState) after silent SSO.
//
// 2. INBOUND_MUTATE_VERIFYSTATE_CODE=<value> (new): when a real
//    signin/verifyState invoke arrives, REWRITE its value.state field to the
//    configured value before the SDK sees it. This reliably reproduces the
//    Paycor symptom: silent SSO completes browser-side, the real magic code
//    arrives on the wire, but the SDK's GetToken call now uses our bogus code
//    -> BF Token Service returns null -> SDK emits InvalidSignInRetryMessage
//    ("Invalid sign in code. Please enter the 6-digit code.").
//
// Mutating ONLY the value.state field (not channelId, conversation, from,
// etc.) preserves all auth/conversation routing so the activity still
// dispatches through AzureBotUserAuthorization.OnContinueFlow exactly like
// Paycor's failing case.

using System.Text;
using System.Text.Json;

namespace OAuthPocDotnet;

public class InboundHttpDump
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InboundHttpDump> _log;
    private readonly string? _mutateCode;

    public InboundHttpDump(RequestDelegate next, ILogger<InboundHttpDump> log)
    {
        _next = next;
        _log = log;
        _mutateCode = Environment.GetEnvironmentVariable("INBOUND_MUTATE_VERIFYSTATE_CODE");
        if (!string.IsNullOrEmpty(_mutateCode))
        {
            _log.LogWarning(
                "[INBOUND-HTTP] verifyState mutator ARMED — will rewrite value.state to '{Code}' on every signin/verifyState invoke.",
                _mutateCode);
        }
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            await _next(ctx);
            return;
        }

        _log.LogInformation("[INBOUND-HTTP] PATH={Path} CT={CT}", path, ctx.Request.ContentType);

        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;

        string? type    = null;
        string? name    = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            type            = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            name            = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? text    = root.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            string? chanId  = root.TryGetProperty("channelId", out var ch) ? ch.GetString() : null;
            string? convId  = root.TryGetProperty("conversation", out var c) && c.TryGetProperty("id", out var ci) ? ci.GetString() : null;
            string? fromId  = root.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fi) ? fi.GetString() : null;
            string? replyTo = root.TryGetProperty("replyToId", out var r) ? r.GetString() : null;

            string? valuePreview = null;
            if (root.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null)
            {
                var raw = v.GetRawText();
                valuePreview = raw.Length > 800 ? raw[..800] + "..." : raw;
            }

            _log.LogInformation(
                "[INBOUND-HTTP] type={Type} name={Name} text={Text} channel={Chan} conv={Conv} from={From} reply_to={ReplyTo} value={Value}",
                type, name, text, chanId, convId, fromId, replyTo, valuePreview);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[INBOUND-HTTP] could not parse body: {Err}; raw={Body}", ex.Message,
                body.Length > 400 ? body[..400] + "..." : body);
        }

        // Mutator: rewrite signin/verifyState's value.state -> bogus code.
        if (!string.IsNullOrEmpty(_mutateCode)
            && string.Equals(type, "invoke", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "signin/verifyState", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rewritten = RewriteVerifyStateCode(body, _mutateCode);
                if (rewritten is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(rewritten);
                    var ms = new MemoryStream(bytes);
                    ctx.Request.Body = ms;
                    ctx.Request.ContentLength = bytes.LongLength;
                    _log.LogWarning(
                        "[INBOUND-HTTP] MUTATED signin/verifyState value.state -> '{Code}' (body now {Len} bytes). Expecting SDK to emit InvalidSignInRetryMessage.",
                        _mutateCode, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[INBOUND-HTTP] failed to mutate verifyState body; passing original through.");
                ctx.Request.Body.Position = 0;
            }
        }

        await _next(ctx);
    }

    // Parses the activity JSON, replaces value.state with the supplied bogus
    // code, and reserializes. Preserves all other fields.
    private static string? RewriteVerifyStateCode(string originalBody, string newStateCode)
    {
        using var doc = JsonDocument.Parse(originalBody);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("value") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName(prop.Name);
                    writer.WriteStartObject();
                    bool wroteState = false;
                    foreach (var inner in prop.Value.EnumerateObject())
                    {
                        if (inner.NameEquals("state"))
                        {
                            writer.WriteString("state", newStateCode);
                            wroteState = true;
                        }
                        else
                        {
                            inner.WriteTo(writer);
                        }
                    }
                    if (!wroteState)
                    {
                        writer.WriteString("state", newStateCode);
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
