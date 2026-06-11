// Feature A — user-visible support reference code.
//
// External employees (users in other tenants) who hit a problem can't see, and
// shouldn't have to read out, raw backend identifiers (conversationId,
// activityId). This produces a short, opaque, *deterministic* code from the
// conversationId that a user can quote in a support request. Support staff
// recover the real conversationId by grepping the logs for the code -> ids
// mapping that the bot writes when the user runs `/support` (and on every
// turn at debug level).
//
// ComputeRef(conversationId) = "PW1-" + base32(HMACSHA256(secret, conversationId))[:10]
//   - deterministic: same conversation always yields the same code, so the
//     user can quote it at any point in the session.
//   - opaque: HMAC means the code reveals nothing about the conversationId and
//     can't be forged without the secret.
//   - reversible by support only: there is no math inversion, but the bot logs
//     the code -> {conversationId,...} mapping, so support can look it up.
//
// Secret comes from env SUPPORT_REF_SECRET. A POC fallback is used when unset
// so the demo still works; production deployments MUST set the env var (a
// fallback secret is logged as a warning by the agent at /support time).

using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging;

namespace OAuthPocDotnet;

// Idempotency marker: records that we've already logged the support-ref mapping
// for a conversation.
public class SupportRefMarker
{
    public string? Code { get; set; }
    public long UtcTicks { get; set; }
}

public static class SupportRef
{
    // Crockford-style base32 alphabet (no I, L, O, U to avoid ambiguity).
    private const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string PocFallbackSecret = "";

    public static bool UsingFallbackSecret =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPPORT_REF_SECRET"));

    private static string Secret =>
        Environment.GetEnvironmentVariable("SUPPORT_REF_SECRET") ?? PocFallbackSecret;

    public static string Compute(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return "PW1-UNKNOWN";
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(conversationId));
        return "PW1-" + ToBase32(hash, 10);
    }

    // A small, unobtrusive footer to append to error/anomaly messages so the
    // user has something to quote to support — without exposing raw backend ids.
    // (The code -> conversationId mapping is logged once at conversation start by
    // LogMappingOnceAsync, so no logging happens here.)
    public static string Footer(string? conversationId) =>
        $"\n\n_support ref: {Compute(conversationId)}_";

    // Logs the code -> backend-id mapping exactly once per conversation, the first
    // time we see it (conversation-start membersAdded, or first message as a
    // fallback for surfaces that don't raise membersAdded). A storage marker
    // keyed by {channel}/{conversationId} makes it idempotent. After this line is
    // written, a user can quote the code at any time and support can grep back to
    // the conversation — no need to re-log it on every error.
    public static async Task LogMappingOnceAsync(IStorage storage, ITurnContext ctx, ILogger? log, CancellationToken ct)
    {
        try
        {
            var convId = ctx.Activity.Conversation?.Id;
            if (string.IsNullOrEmpty(convId))
            {
                return;
            }

            var markerKey = $"oauthpoc/supportRefLogged/{ctx.Activity.ChannelId?.Channel}/{convId}";
            var existing = await storage.ReadAsync(new[] { markerKey }, ct).ConfigureAwait(false);
            if (existing.ContainsKey(markerKey))
            {
                return;
            }

            var code = Compute(convId);
            if (UsingFallbackSecret)
            {
                log?.LogWarning("[Support] SUPPORT_REF_SECRET is not set — using POC fallback secret. Set it in production.");
            }
            log?.LogInformation(
                "[Support] ref={Code} -> conversationId={Conv} aadObjectId={Aad} channel={Channel} utc={Utc:o} shown=conversation-start",
                code, convId, ctx.Activity.From?.AadObjectId, ctx.Activity.ChannelId?.Channel, DateTime.UtcNow);

            await storage.WriteAsync(
                new Dictionary<string, object> { { markerKey, new SupportRefMarker { Code = code, UtcTicks = DateTime.UtcNow.Ticks } } },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "[Support] failed to log code->conversationId mapping");
        }
    }

    private static string ToBase32(byte[] bytes, int chars)
    {
        var sb = new StringBuilder(chars);
        int bitBuffer = 0;
        int bitCount = 0;
        foreach (var b in bytes)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5 && sb.Length < chars)
            {
                bitCount -= 5;
                sb.Append(Base32[(bitBuffer >> bitCount) & 0x1F]);
            }
            if (sb.Length >= chars)
            {
                break;
            }
        }
        return sb.ToString();
    }
}
