// OAuthDiagnostics.cs
//
// Drop-in diagnostic helpers for Custom Engine Agents built on
// Microsoft.Agents.Builder. Single file, no extra NuGet dependencies beyond
// what an Agents SDK app already references (Microsoft.Agents.Builder,
// Microsoft.Agents.Connector, Microsoft.Agents.Core,
// Microsoft.Agents.Storage, Microsoft.AspNetCore.Http).
//
// Provides four diagnostics that together answer "why is OAuth getting stuck?":
//
//   1. /diag    — a chat command that dumps live OAuth state for the current
//                 conversation: ChannelId (raw/.Channel/.SubChannel), the
//                 SDK's FlowState in IStorage at its canonical key, the
//                 current BF Token Service token state, and whether the
//                 ProtocolJsonSerializer.ChannelIdIncludesProduct workaround
//                 is active.
//
//   2. Sign-in failure logger — registers a UserAuthorization
//                 OnUserSignInFailure handler that logs every sign-in failure
//                 (cause + error). Without this, failures can be silent.
//
//   3. Inbound HTTP logger    — middleware that logs every incoming activity
//                 hitting /api/messages with its Type, Name, ChannelId,
//                 Conversation.Id and Value-payload keys. Critically, this
//                 catches whether the bot ever receives the
//                 `signin/tokenExchange` invoke or `tokens/response` event
//                 that should complete a sign-in flow.
//
//   4. OnTurnError logger     — installs an adapter-level error handler that
//                 logs unhandled exceptions during turn processing (e.g. an
//                 exception while processing the sign-in completion invoke
//                 that would otherwise be swallowed).
//
// How to wire it in — three changes to your project:
//
//   (a) Add this file to your project.
//
//   (b) In Program.cs, BEFORE `var app = builder.Build();`, register the
//       middleware service:
//
//           builder.Services.AddTransient<OAuthDiagnostics.InboundActivityLoggerMiddleware>();
//
//       And AFTER `var app = builder.Build();`, add the middleware to the
//       HTTP pipeline (before MapAgentApplicationEndpoints):
//
//           app.UseMiddleware<OAuthDiagnostics.InboundActivityLoggerMiddleware>();
//
//   (c) In your AgentApplication-derived class constructor, after `base(options)`,
//       call:
//
//           OAuthDiagnostics.Register(
//               this,
//               storage:        storage,           // your IStorage instance
//               handlerName:    "<your-handler-name>", // your DefaultHandlerName
//               connectionName: "<your-oauth-connection-name>",
//               logger:         log);              // your ILogger
//
//       To get IStorage into the constructor, add `IStorage storage` to the
//       constructor parameters — the SDK already registers it in DI.
//
// To reproduce and capture data:
//
//   1. Deploy with this file in place + ProtocolJsonSerializer.ChannelIdIncludesProduct = false.
//   2. Sign out (or use a fresh conversation).
//   3. Send your trigger message (e.g. "Hello").
//   4. Click the sign-in card and complete sign-in.
//   5. THE MOMENT the conversation appears frozen, send `/diag` and copy the bot's reply.
//   6. Send your trigger message again, observe "Invalid sign in code".
//   7. Send `/diag` again and copy the reply.
//   8. From your app logs, copy ALL lines tagged "OAUTH-DIAG" between steps 3 and 7.
//   9. Send all of the above back.

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging;

namespace CeaOAuthDiagnostics;

public static class OAuthDiagnostics
{
    public const string LogTag = "OAUTH-DIAG";

    // ─────────────────────────────────────────────────────────────────────
    // 1. /diag command + 2. sign-in failure logger + 4. OnTurnError logger.
    // ─────────────────────────────────────────────────────────────────────

    public static void Register(
        AgentApplication app,
        IStorage storage,
        string handlerName,
        string connectionName,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(handlerName);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);
        ArgumentNullException.ThrowIfNull(logger);

        // (1) /diag chat command.
        app.OnMessage("/diag", async (ctx, _, ct) =>
            await OnDiagAsync(ctx, storage, handlerName, connectionName, logger, ct));

        // (2) Sign-in failure logger.
        app.UserAuthorization.OnUserSignInFailure(async (ctx, state, h, response, initiating, ct) =>
        {
            logger.LogError(
                "[{Tag}] sign-in failure: handler={Handler} cause={Cause} error={Error} initiating_activity_id={InitId} channel={Channel} conv={Conv}",
                LogTag, h, response.Cause, response.Error?.Message,
                initiating?.Id, ctx.Activity.ChannelId?.ToString(), ctx.Activity.Conversation?.Id);
            await ctx.SendActivityAsync(
                $"⚠️ sign-in failed (handler={h}): {response.Cause} / {response.Error?.Message}",
                cancellationToken: ct);
        });

        // (4) OnTurnError — installed on the channel adapter so unhandled
        // exceptions during turn processing (including invoke processing for
        // signin/tokenExchange) get logged instead of swallowed.
        //
        // The Options.Adapter property is marked [Obsolete] in newer SDK
        // versions; if you prefer the forward-compatible path, skip this
        // branch and call OAuthDiagnostics.InstallTurnErrorLogger(adapter,
        // logger) explicitly from Program.cs after resolving IChannelAdapter
        // from DI.
#pragma warning disable CS0618
        if (app.Options.Adapter is ChannelAdapter adapter)
#pragma warning restore CS0618
        {
            InstallTurnErrorLogger(adapter, logger);
        }
        else
        {
            logger.LogWarning(
                "[{Tag}] could not install OnTurnError handler: app.Options.Adapter is not a ChannelAdapter. " +
                "Call OAuthDiagnostics.InstallTurnErrorLogger(adapter, logger) explicitly from Program.cs instead.",
                LogTag);
        }
    }

    /// <summary>
    /// Installs an OnTurnError handler on the given adapter that logs the
    /// exception with the active Activity. Call from Program.cs after the
    /// adapter is resolved from DI if you prefer that over the implicit
    /// install Register(...) does via the (obsolete) Options.Adapter path.
    /// </summary>
    public static void InstallTurnErrorLogger(IChannelAdapter adapter, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(logger);
        if (adapter is not ChannelAdapter ch)
        {
            logger.LogWarning(
                "[{Tag}] InstallTurnErrorLogger: adapter is not a ChannelAdapter ({Type}); skipping.",
                LogTag, adapter.GetType().FullName);
            return;
        }
        var existing = ch.OnTurnError;
        ch.OnTurnError = async (ctx, ex) =>
        {
            logger.LogError(ex,
                "[{Tag}] OnTurnError: activity_type={Type} activity_name={Name} channel={Channel} conv={Conv}",
                LogTag, ctx.Activity?.Type, ctx.Activity?.Name,
                ctx.Activity?.ChannelId?.ToString(), ctx.Activity?.Conversation?.Id);
            if (existing is not null) await existing(ctx, ex);
        };
    }

    private static async Task OnDiagAsync(
        ITurnContext ctx, IStorage storage, string handlerName, string connectionName,
        ILogger logger, CancellationToken ct)
    {
        var cid = ctx.Activity.ChannelId;
        var convId = ctx.Activity.Conversation?.Id ?? "(null)";
        var userId = ctx.Activity.From?.Id ?? "(null)";

        // Mirrors AzureBotUserAuthorization.GetStorageKey verbatim:
        //   $"oauth/{Name}/{channelId}/{conversationId}/flowState"
        // Note: {channelId} interpolates via .ToString() which DOES include
        // sub-channel by default. The workaround
        // (ChannelIdIncludesProduct = false) makes this fall back to just
        // the base channel — and that affects what key we read here too.
        var flowKey = $"oauth/{handlerName}/{cid}/{convId}/flowState";
        var altFlowKey = $"oauth/{handlerName}/{cid?.Channel}/{convId}/flowState";

        string flowDump = await ReadStorageAsync(storage, flowKey, ct);
        string altFlowDump = (flowKey != altFlowKey)
            ? "\n  - also tried base-channel key `" + altFlowKey + "` → " + await ReadStorageAsync(storage, altFlowKey, ct)
            : "";

        string tokenDump;
        var tokenClient = ctx.Services.Get<IUserTokenClient>();
        if (tokenClient is null)
        {
            tokenDump = "(IUserTokenClient not in turn services)";
        }
        else
        {
            try
            {
                var resp = await tokenClient.GetUserTokenAsync(userId, connectionName, cid, magicCode: null!, ct);
                tokenDump = (resp is null || string.IsNullOrEmpty(resp.Token))
                    ? "(no cached token at BF Token Service)"
                    : $"(present, length={resp.Token.Length}, expiration={resp.Expiration?.ToString("u") ?? "(null)"})";
            }
            catch (Exception ex)
            {
                tokenDump = $"(GetUserTokenAsync threw: {ex.GetType().Name}: {ex.Message})";
            }
        }

        var report = new StringBuilder()
            .AppendLine("### `/diag` — live OAuth state")
            .AppendLine()
            .AppendLine("**activity**")
            .AppendLine($"- channel_id raw: `{cid?.ToString() ?? "(null)"}`")
            .AppendLine($"- channel base: `{cid?.Channel ?? "(null)"}`")
            .AppendLine($"- channel sub: `{cid?.SubChannel ?? "(none)"}`")
            .AppendLine($"- conversation_id: `{convId}`")
            .AppendLine($"- user_id: `{userId}`")
            .AppendLine()
            .AppendLine("**workaround**")
            .Append($"- `ProtocolJsonSerializer.ChannelIdIncludesProduct = {ProtocolJsonSerializer.ChannelIdIncludesProduct}`")
            .AppendLine(ProtocolJsonSerializer.ChannelIdIncludesProduct ? " (default — Copilot silent SSO may fail upstream)" : " (workaround ENABLED)")
            .AppendLine()
            .AppendLine($"**FlowState** (storage key `{flowKey}`)")
            .AppendLine($"- {flowDump}{altFlowDump}")
            .AppendLine()
            .AppendLine($"**BF Token Service** (connection `{connectionName}`)")
            .AppendLine($"- {tokenDump}")
            .ToString();

        logger.LogInformation(
            "[{Tag}] /diag: cid_raw={Raw} cid_base={Base} cid_sub={Sub} conv={Conv} workaround_off={WorkaroundOff} flow_present={FlowPresent} token_present={TokenPresent}",
            LogTag, cid?.ToString(), cid?.Channel, cid?.SubChannel, convId,
            !ProtocolJsonSerializer.ChannelIdIncludesProduct,
            !flowDump.StartsWith("(no entry"),
            !tokenDump.StartsWith("(no cached"));

        await ctx.SendActivityAsync(report, cancellationToken: ct);
    }

    private static async Task<string> ReadStorageAsync(IStorage storage, string key, CancellationToken ct)
    {
        try
        {
            var items = await storage.ReadAsync(new[] { key }, ct);
            if (!items.TryGetValue(key, out var raw)) return "(no entry — FlowState is clear)";
            try
            {
                return $"(present)\n```json\n{ProtocolJsonSerializer.ToJson(raw)}\n```";
            }
            catch
            {
                return $"(present, value-type={raw?.GetType().FullName ?? "null"}; serialization failed)";
            }
        }
        catch (Exception ex)
        {
            return $"(storage read failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Inbound activity HTTP logger.
    //
    // Catches the critical question: does the bot endpoint ever receive a
    // signin/tokenExchange invoke or tokens/response event after the user
    // signs in? If no, the bug is upstream of the bot (BF / Copilot routing).
    // If yes but processing fails, OnTurnError (above) will surface why.
    // ─────────────────────────────────────────────────────────────────────

    public sealed class InboundActivityLoggerMiddleware : Microsoft.AspNetCore.Http.IMiddleware
    {
        private readonly ILogger<InboundActivityLoggerMiddleware> _log;

        public InboundActivityLoggerMiddleware(ILogger<InboundActivityLoggerMiddleware> log) => _log = log;

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            // Only care about the bot endpoint. Adjust if your endpoint path differs.
            var path = context.Request.Path.Value ?? "";
            if (!(context.Request.Method == "POST" && path.Contains("/api/messages", StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            context.Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? type = Get(root, "type");
                string? name = Get(root, "name");
                string? channelId = Get(root, "channelId");
                string? convId = root.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.Object
                    ? Get(conv, "id") : null;
                string? fromId = root.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
                    ? Get(from, "id") : null;
                string? text = Get(root, "text");
                string? valueKeys = root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Object
                    ? string.Join(",", val.EnumerateObject().Select(p => p.Name)) : null;

                // Extra surfacing for context discovery — Word/Excel/PowerPoint
                // may stuff document references into channelData, entities, or
                // attachments. Dump just the shape (top-level keys + sizes) so
                // we can see at a glance whether any document context is being
                // delivered. Full dump only on activities likely to carry it.
                string? channelDataKeys = root.TryGetProperty("channelData", out var cd) && cd.ValueKind == JsonValueKind.Object
                    ? string.Join(",", cd.EnumerateObject().Select(p => p.Name)) : null;
                int entitiesCount = root.TryGetProperty("entities", out var ent) && ent.ValueKind == JsonValueKind.Array
                    ? ent.GetArrayLength() : 0;
                string? entityTypes = entitiesCount > 0
                    ? string.Join(",", ent.EnumerateArray()
                        .Select(e => e.TryGetProperty("type", out var t) ? t.GetString() : "(no-type)")
                        .Distinct()) : null;
                int attachmentsCount = root.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array
                    ? att.GetArrayLength() : 0;
                string? attachmentTypes = attachmentsCount > 0
                    ? string.Join(",", att.EnumerateArray()
                        .Select(a => a.TryGetProperty("contentType", out var ct2) ? ct2.GetString() : "(no-type)")
                        .Distinct()) : null;

                _log.LogInformation(
                    "[{Tag}] inbound: type={Type} name={Name} channel={Channel} conv={Conv} from={From} text={Text} value_keys={ValueKeys} channel_data_keys={ChannelDataKeys} entities={EntitiesCount}({EntityTypes}) attachments={AttachmentsCount}({AttachmentTypes}) content_length={Len}",
                    LogTag, type, name, channelId, convId, fromId,
                    string.IsNullOrEmpty(text) ? "(none)" : (text.Length > 80 ? text[..80] + "…" : text),
                    valueKeys ?? "(none)",
                    channelDataKeys ?? "(none)",
                    entitiesCount, entityTypes ?? "(none)",
                    attachmentsCount, attachmentTypes ?? "(none)",
                    body.Length);

                // If we see ANY hint of document context (channelData with
                // content beyond the standard fields, OR entities of unusual
                // types, OR attachments), dump the FULL relevant payload at
                // Information level so we can study the shape. This is the
                // capture we need for Word/Excel/PowerPoint surface discovery.
                bool hasInterestingContext =
                    (channelDataKeys != null && !IsBoringChannelData(channelDataKeys))
                    || entitiesCount > 0
                    || attachmentsCount > 0
                    || (valueKeys != null && valueKeys.Contains("document", StringComparison.OrdinalIgnoreCase));

                if (hasInterestingContext)
                {
                    if (root.TryGetProperty("channelData", out var cdFull))
                        _log.LogInformation("[{Tag}] inbound.channelData (id={Id}):\n{Json}",
                            LogTag, Get(root, "id"), cdFull.GetRawText());
                    if (root.TryGetProperty("entities", out var entFull) && entFull.GetArrayLength() > 0)
                        _log.LogInformation("[{Tag}] inbound.entities (id={Id}):\n{Json}",
                            LogTag, Get(root, "id"), entFull.GetRawText());
                    if (root.TryGetProperty("attachments", out var attFull) && attFull.GetArrayLength() > 0)
                        _log.LogInformation("[{Tag}] inbound.attachments (id={Id}):\n{Json}",
                            LogTag, Get(root, "id"), attFull.GetRawText());
                    if (root.TryGetProperty("value", out var valFull) && valFull.ValueKind == JsonValueKind.Object)
                        _log.LogInformation("[{Tag}] inbound.value (id={Id}):\n{Json}",
                            LogTag, Get(root, "id"), valFull.GetRawText());
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[{Tag}] inbound: (failed to parse body as JSON: {Ex}) content_length={Len}",
                    LogTag, ex.Message, body.Length);
            }

            await next(context);
        }

        // channelData on every Teams/Copilot activity carries some standard
        // bookkeeping (tenant info, etc.) that isn't useful for document
        // context discovery. Treat the activity as "interesting" only when
        // channelData has fields beyond these well-known ones.
        private static readonly HashSet<string> BoringChannelDataKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "tenant", "channel", "team", "settings", "notification",
            "source", "streamId", "streamSequence", "streamType",
            "clientActivityId", "deviceId"
        };

        private static bool IsBoringChannelData(string keysCsv)
        {
            var keys = keysCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
            return keys.All(k => BoringChannelDataKeys.Contains(k.Trim()));
        }

        private static string? Get(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var p) && p.ValueKind != JsonValueKind.Null && p.ValueKind != JsonValueKind.Undefined
                ? p.ToString() : null;
    }
}
