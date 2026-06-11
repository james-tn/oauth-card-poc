// OAuth POC (.NET / new Microsoft Agents SDK) agent.
//
// Two purposes:
// 1. Channel_id diagnostics:
//    /showurl                — log + echo activity.ChannelId and the sign-in
//                              URL IUserTokenClient.GetSignInResourceAsync
//                              produces for the current activity.
//    /whoami                 — dump conversation/user/channel info.
//    /manualcard [override]  — build and send an OAuthCard manually. If
//                              `override` is set, the activity's ChannelId is
//                              save-mutate-restored before the call to
//                              GetSignInResourceAsync.
//
// 2. End-to-end sign-in (canonical SDK pattern):
//    /login                  — triggers the SDK's UserAuthorization auto-signin
//                              ("auto" handler), then calls GitHub /user with
//                              the resulting token.
//    /me                     — passive token check via IUserTokenClient. Does
//                              NOT trigger sign-in. Use after /signout to
//                              confirm token was cleared.
//    /signout                — UserAuthorization.SignOutUserAsync (proper).
//    /signout-buggy          — Paycor-style override: sets a flag, sends a
//                              message, but DOES NOT call
//                              UserAuthorization.SignOutUserAsync and does
//                              NOT clear OAuth flow state. Used to prove the
//                              bug from the Paycor incident report.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Storage;

namespace OAuthPocDotnet;

public class OAuthPocAgent : AgentApplication
{
    private readonly ILogger<OAuthPocAgent> _log;
    private readonly string _connectionName;
    private readonly string _handlerName;
    private readonly IStorage _storage;

    public OAuthPocAgent(AgentApplicationOptions options, ILogger<OAuthPocAgent> log,
        IConfiguration config, IStorage storage)
        : base(options)
    {
        _log = log;
        _storage = storage;
        _connectionName = config["OAuthConnectionName"]
            ?? throw new InvalidOperationException("OAuthConnectionName not configured");
        _handlerName = config["AgentApplication:UserAuthorization:DefaultHandlerName"] ?? "auto";

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, OnMembersAddedAsync);

        // Channel-id diagnostic commands. These do NOT trigger auto-signin —
        // they just inspect the live activity / produce a sign-in URL.
        OnMessage("/showurl", ShowSignInUrlAsync);
        OnMessage("/whoami",  WhoAmIAsync);

        // /diag — dumps the live OAuth state for the current
        // (channelId, conversationId, handler) tuple. Tells you which of
        // FlowState (SDK in-flight sign-in) vs. token cache (BF Token
        // Service) is stuck when symptoms appear. Does NOT trigger sign-in.
        // (Disabled during testing of the cea-oauth-diagnostics helper —
        // the helper's Register() below provides its own /diag route.)
        // OnMessage("/diag", OnDiagAsync);

        // End-to-end sign-in proof:
        //  /login — triggers the SDK's auto-signin via the "auto" handler.
        //  /me    — passive token check: reports current token state WITHOUT
        //           triggering sign-in. Lets you observe `/signout` cleanly.
        OnMessage("/login", OnLoginAsync, autoSignInHandlers: ["auto"]);
        OnMessage("/me",    OnMeAsync);

        // Feature A — on-demand support reference (opaque, deterministic per
        // conversation). Logs the code -> backend-id mapping for support lookup.
        OnMessage("/support", OnSupportAsync);

        // Features B/C test hook — write a synthetic stale pending intent so the
        // self-healing recovery (OnBeforeTurn) can be validated deterministically
        // without reproducing the real intermittent SDK resume drop. Handled
        // inside OnAnyMessageAsync (StartsWith) so it accepts a trailing argument:
        //   /dropsim [text]   (defaults to "list employees")
        // then send any message to trigger recovery.

        // Features B/C — resume diagnostics + self-healing recovery. Runs on every
        // Message turn after auth (and on the SDK's post-sign-in resume turn).
        OnBeforeTurn((ctx, state, ct) => ResumeRecovery.OnBeforeTurnAsync(_storage, this, _log, ctx, ct));

        // Feature A — log the support-ref -> conversationId mapping ONCE per
        // conversation. Conversation start (membersAdded) is the primary trigger;
        // this before-turn is the first-seen fallback for surfaces that don't
        // raise membersAdded. Idempotent via a storage marker.
        OnBeforeTurn(async (ctx, state, ct) =>
        {
            await SupportRef.LogMappingOnceAsync(_storage, ctx, _log, ct);
            return true;
        });

        // Mirrors the Paycor symptom: any non-command user phrase that requires
        // auth. Repros "Invalid sign in code" appearing after silent SSO.
        OnMessage("list employees", OnLoginAsync, autoSignInHandlers: ["auto"]);
        OnMessage("list 10 employees", OnLoginAsync, autoSignInHandlers: ["auto"]);

        OnMessage("/signout", OnSignOutAsync, rank: RouteRank.Last);
        OnMessage("/signout-buggy", OnSignOutBuggyAsync, rank: RouteRank.Last);

        // Catch-all for messages — also handles /manualcard.
        OnActivity(ActivityTypes.Message, OnAnyMessageAsync, rank: RouteRank.Last);

        UserAuthorization.OnUserSignInFailure(OnSignInFailureAsync);

        // Integration test of the generic diagnostics helper
        // (https://github.com/james-tn/cea-oauth-diagnostics). Toggled via
        // OAUTH_DIAG env var to avoid affecting normal POC operation. When
        // enabled, registers the helper's /diag handler (which may shadow or
        // collide with this file's existing /diag — proving whether the helper
        // composes safely with a host that already has its own diag routes).
        if (string.Equals(Environment.GetEnvironmentVariable("OAUTH_DIAG"),
                          "true", StringComparison.OrdinalIgnoreCase))
        {
            CeaOAuthDiagnostics.OAuthDiagnostics.Register(
                app:            this,
                storage:        _storage,
                handlerName:    _handlerName,
                connectionName: _connectionName,
                logger:         _log);
        }
    }

    private async Task OnMembersAddedAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        // Feature A — log the support-ref -> conversationId mapping once at
        // conversation start so it's always available for later trace-back.
        await SupportRef.LogMappingOnceAsync(_storage, ctx, _log, ct);

        foreach (var m in ctx.Activity.MembersAdded ?? new List<ChannelAccount>())
        {
            if (m.Id != ctx.Activity.Recipient.Id)
            {
                await ctx.SendActivityAsync(
                    "OAuth POC (.NET / Microsoft.Agents SDK).\n\n" +
                    "Diagnostic commands:\n" +
                    "- `/showurl` — log + echo channel_id and the sign-in URL the SDK produces.\n" +
                    "- `/whoami` — dump conversation/user/channel info.\n" +
                    "- `/diag` — dump live OAuth state (FlowState + BF token cache) for this conversation. **Use this during a broken session.**\n" +
                    "- `/manualcard [override_channel_id]` — send a hand-rolled OAuthCard; if `override` is given the activity's ChannelId is mutated before calling `GetSignInResourceAsync`.\n\n" +
                    "End-to-end sign-in:\n" +
                    "- `/login` — auto-signs you in to GitHub then calls /user to prove the token works.\n" +
                    "- `/me` — passively checks token state (does NOT trigger sign-in).\n" +
                    "- `/signout` — proper sign-out via `UserAuthorization.SignOutUserAsync`.\n" +
                    "- `/signout-buggy` — Paycor-style override that NO-OPs; the token stays cached. Send `/me` after to observe.",
                    cancellationToken: ct);
            }
        }
    }

    private Task OnAnyMessageAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var text = (ctx.Activity.Text ?? "").Trim();
        LogInbound(ctx);
        if (text.StartsWith("/manualcard", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string? overrideId = parts.Length > 1 ? parts[1].Trim() : null;
            return SendManualOAuthCardAsync(ctx, overrideId, ct);
        }
        if (text.StartsWith("/dropsim", StringComparison.OrdinalIgnoreCase))
        {
            return OnDropSimAsync(ctx, state, ct);
        }
        var cid = ctx.Activity.ChannelId;
        return ctx.SendActivityAsync(
            $"echo (no command). channel_id raw: `{cid?.ToString() ?? "(null)"}` " +
            $"(.Channel='{cid?.Channel ?? "(null)"}', " +
            $".SubChannel='{cid?.SubChannel ?? "(none)"}'). Try `/me` to sign in.",
            cancellationToken: ct);
    }

    private async Task ShowSignInUrlAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        LogInbound(ctx);
        var tokenClient = ctx.Services.Get<IUserTokenClient>();
        if (tokenClient is null)
        {
            await ctx.SendActivityAsync("IUserTokenClient not in turn services.", cancellationToken: ct);
            return;
        }

        var resource = await tokenClient.GetSignInResourceAsync(_connectionName, ctx.Activity, finalRedirect: null!, ct);
        var link = resource?.SignInLink ?? "(null)";
        var cid = ctx.Activity.ChannelId;
        _log.LogInformation("/showurl: channel_id_raw={Raw} channel_base={Base} channel_sub={Sub} link={Link}",
            cid?.ToString(), cid?.Channel, cid?.SubChannel, link);

        await ctx.SendActivityAsync(
            $"channel_id raw: `{cid?.ToString() ?? "(null)"}`\n\n" +
            $"channel base: `{cid?.Channel ?? "(null)"}`\n\n" +
            $"channel sub: `{cid?.SubChannel ?? "(none)"}`\n\n" +
            $"sign_in_link:\n```\n{link}\n```",
            cancellationToken: ct);
    }

    private async Task WhoAmIAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        LogInbound(ctx);
        var cid = ctx.Activity.ChannelId;

        // Serialize channelData (whatever shape it arrives in) — this is where the
        // Teams-vs-M365-Copilot distinction survives even when the normalized
        // ChannelId has had its product/sub-channel stripped by
        // ProtocolJsonSerializer.ChannelIdIncludesProduct = false.
        object channelData = null;
        try
        {
            channelData = ctx.Activity.ChannelData is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ctx.Activity.ChannelData));
        }
        catch (Exception ex)
        {
            channelData = $"(unserializable: {ex.Message})";
        }

        var entityTypes = ctx.Activity.Entities is { Count: > 0 }
            ? ctx.Activity.Entities.Select(e => e?.Type ?? "(no-type)").Distinct().ToArray()
            : System.Array.Empty<string>();

        var surface = ClassifySurface(ctx.Activity);

        var info = new
        {
            surfaceGuess   = surface,           // teams | copilot | non-teams:<channel> | unknown
            channelBase    = cid?.Channel,      // coarse channel: msteams | slack | directline | …
            channelSub     = cid?.SubChannel,   // product/sub-channel (empty when product stripped)
            channelIdRaw   = cid?.ToString(),
            isSubChannel   = cid?.IsSubChannel() ?? false,
            conversationType = ctx.Activity.Conversation?.ConversationType,
            conversationId = ctx.Activity.Conversation?.Id,
            tenantId       = ctx.Activity.Conversation?.TenantId,
            userId         = ctx.Activity.From?.Id,
            userAadObjectId = ctx.Activity.From?.AadObjectId,
            recipientId    = ctx.Activity.Recipient?.Id,
            serviceUrl     = ctx.Activity.ServiceUrl,
            entityTypes    = entityTypes,
            channelData    = channelData,
        };

        _log.LogInformation(
            "/whoami: surface={Surface} base={Base} sub={Sub} convType={ConvType} serviceUrl={Service} entities={Entities} channelData={ChannelData}",
            surface, cid?.Channel, cid?.SubChannel, ctx.Activity.Conversation?.ConversationType,
            ctx.Activity.ServiceUrl, string.Join(",", entityTypes),
            channelData is JsonElement je ? je.GetRawText() : channelData?.ToString());

        await ctx.SendActivityAsync($"```json\n{JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true })}\n```",
            cancellationToken: ct);
    }

    // Surface classifier — confirmed empirically against live Teams + M365 Copilot
    // payloads. Main-channel detection (Slack/Direct Line/Web Chat/etc.) is reliable
    // straight off ChannelId.Channel. Teams vs M365 Copilot both ride base channel
    // "msteams"; the distinguishing signal is the product context, recovered (in
    // priority order) from:
    //   1. channelData.productContext  — wire-level, survives ChannelIdIncludesProduct=false
    //   2. ChannelId.SubChannel        — SDK-native (present only when product not stripped)
    // Absent both → plain Teams. (conversationType/serviceUrl are NOT reliable
    // product signals and are deliberately not used here.)
    private static string ClassifySurface(IActivity activity)
    {
        var channel = activity.ChannelId?.Channel;
        if (string.IsNullOrEmpty(channel))
        {
            return "unknown";
        }
        if (!string.Equals(channel, "msteams", StringComparison.OrdinalIgnoreCase))
        {
            return $"non-teams:{channel}";   // slack, directline, webchat, …
        }

        // Base channel is msteams → Teams OR an M365 host (Copilot, Word, Excel…).
        var product = ProductContext(activity) ?? activity.ChannelId?.SubChannel;
        if (string.IsNullOrEmpty(product))
        {
            return "teams";
        }
        return product.Equals("COPILOT", StringComparison.OrdinalIgnoreCase)
            ? "copilot"
            : $"m365:{product.ToLowerInvariant()}";
    }

    // Reads channelData.productContext (e.g. "COPILOT") regardless of the concrete
    // type channelData deserialized to. Returns null if absent/unreadable.
    private static string ProductContext(IActivity activity)
    {
        try
        {
            if (activity.ChannelData is null)
            {
                return null;
            }
            var je = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(activity.ChannelData));
            if (je.ValueKind == JsonValueKind.Object
                && je.TryGetProperty("productContext", out var pc)
                && pc.ValueKind == JsonValueKind.String)
            {
                return pc.GetString();
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private async Task SendManualOAuthCardAsync(ITurnContext ctx, string? overrideChannelId, CancellationToken ct)
    {
        var tokenClient = ctx.Services.Get<IUserTokenClient>();
        if (tokenClient is null)
        {
            await ctx.SendActivityAsync("IUserTokenClient not in turn services.", cancellationToken: ct);
            return;
        }

        ChannelId? savedChannelId = ctx.Activity.ChannelId;
        SignInResource? resource;
        try
        {
            if (!string.IsNullOrEmpty(overrideChannelId))
            {
                ctx.Activity.ChannelId = overrideChannelId;
                _log.LogInformation("/manualcard: mangled channel_id {From} -> {To}",
                    savedChannelId?.ToString(), overrideChannelId);
            }
            resource = await tokenClient.GetSignInResourceAsync(_connectionName, ctx.Activity, finalRedirect: null!, ct);
        }
        finally
        {
            ctx.Activity.ChannelId = savedChannelId;
        }

        var link = resource?.SignInLink ?? "(null)";
        var effectiveCid = overrideChannelId ?? savedChannelId?.ToString();
        _log.LogInformation("/manualcard: effective channel_id={Cid} link={Link}", effectiveCid, link);

        var oauthCard = new OAuthCard
        {
            Text = $"Sign in (manual card, channel_id={effectiveCid})",
            ConnectionName = _connectionName,
            Buttons = new List<CardAction>
            {
                new CardAction { Type = ActionTypes.Signin, Title = "Sign In", Value = link }
            }
        };

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.oauth",
            Content = oauthCard
        };

        var reply = MessageFactory.Attachment(attachment);
        await ctx.SendActivityAsync(reply, ct);
    }

    private async Task OnLoginAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        LogInbound(ctx);

        // SDK has already auto-signed the user in via the "auto" handler at
        // this point (autoSignInHandlers: ["auto"] on the route). Pull the
        // token via the turn-context extension.
        string? accessToken;
        try
        {
            accessToken = await ctx.GetTurnTokenAsync("auto");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "/login: GetTurnTokenAsync failed");
            await ctx.SendActivityAsync($"Could not retrieve token: {ex.Message}" +
                SupportRef.Footer(ctx.Activity.Conversation?.Id), cancellationToken: ct);
            return;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            await ctx.SendActivityAsync("Auto-signin completed but no token was attached. Try `/signout` then `/login` again." +
                SupportRef.Footer(ctx.Activity.Conversation?.Id),
                cancellationToken: ct);
            return;
        }

        await ReportGitHubIdentityAsync(ctx, accessToken, ct);
    }

    private async Task OnMeAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        LogInbound(ctx);

        // Passive token check — does NOT trigger sign-in. Hits the BF Token
        // Service's GetUserToken (returns null if no cached token).
        var tokenClient = ctx.Services.Get<IUserTokenClient>();
        if (tokenClient is null)
        {
            await ctx.SendActivityAsync("IUserTokenClient not in turn services.", cancellationToken: ct);
            return;
        }

        TokenResponse? tokenResp;
        try
        {
            tokenResp = await tokenClient.GetUserTokenAsync(
                ctx.Activity.From?.Id,
                _connectionName,
                ctx.Activity.ChannelId,
                magicCode: null,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "/me: GetUserTokenAsync failed");
            await ctx.SendActivityAsync($"Token lookup failed: {ex.Message}" +
                SupportRef.Footer(ctx.Activity.Conversation?.Id), cancellationToken: ct);
            return;
        }

        if (tokenResp is null || string.IsNullOrEmpty(tokenResp.Token))
        {
            await ctx.SendActivityAsync("Not signed in. Send `/login` to authenticate.", cancellationToken: ct);
            return;
        }

        await ReportGitHubIdentityAsync(ctx, tokenResp.Token, ct);
    }

    private async Task ReportGitHubIdentityAsync(ITurnContext ctx, string accessToken, CancellationToken ct)
    {
        var tokenPreview = accessToken.Length <= 12
            ? accessToken
            : $"{accessToken[..6]}...{accessToken[^4..]} (length={accessToken.Length})";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("oauth-poc-dotnet/1.0");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", accessToken);
            var resp = await http.GetAsync("https://api.github.com/user", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                await ctx.SendActivityAsync(
                    $"Got token (`{tokenPreview}`) but GitHub /user returned {(int)resp.StatusCode}:\n```\n{body}\n```",
                    cancellationToken: ct);
                return;
            }
            var user = JsonNode.Parse(body)!;
            await ctx.SendActivityAsync(
                $"✅ Signed in.\n\n" +
                $"- token: `{tokenPreview}`\n" +
                $"- github login: `{user["login"]?.GetValue<string>()}`\n" +
                $"- github name: `{user["name"]?.GetValue<string>() ?? "(none)"}`\n" +
                $"- github id: `{user["id"]?.GetValue<long>()}`",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ReportGitHubIdentityAsync: GitHub call failed");
            await ctx.SendActivityAsync($"Got token (`{tokenPreview}`) but GitHub call failed: {ex.Message}" +
                SupportRef.Footer(ctx.Activity.Conversation?.Id), cancellationToken: ct);
        }
    }

    private async Task OnSignOutAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        try
        {
            await UserAuthorization.SignOutUserAsync(ctx, state, "auto", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "/signout: SignOutUserAsync threw (continuing)");
        }
        await ctx.SendActivityAsync("Signed out (proper). Send `/me` to verify, or `/login` to sign in again.", cancellationToken: ct);
    }

    // /diag — read-only dump of the live OAuth state for the current
    // (channel_id, conversation_id, handler) tuple. Use this during a broken
    // session to see whether FlowState (the SDK's in-flight sign-in flag) or
    // the BF Token Service token cache (or both) are stuck.
    //
    // Why it exists: with the correct `UserAuthorization.SignOutUserAsync(...)`
    // call, both halves SHOULD be cleared on logout. If a user reports
    // "Invalid sign in code" after logout (Paycor Scenarios 1, 3, 4), one or
    // both of these is still populated — usually because a previous
    // auto-signin set FlowStarted=true but never completed (often a Copilot
    // channel-id silent-SSO failure). This command makes that observable.
    private async Task OnDiagAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        LogInbound(ctx);

        var cid = ctx.Activity.ChannelId;
        var convId = ctx.Activity.Conversation?.Id ?? "(null)";
        var userId = ctx.Activity.From?.Id ?? "(null)";

        // Matches AzureBotUserAuthorization.GetStorageKey verbatim:
        //   $"oauth/{Name}/{channelId}/{conversationId}/flowState"
        var flowKey = $"oauth/{_handlerName}/{cid}/{convId}/flowState";

        string flowDump;
        try
        {
            var items = await _storage.ReadAsync(new[] { flowKey }, ct);
            if (items.TryGetValue(flowKey, out var raw))
            {
                var json = ProtocolJsonSerializer.ToJson(raw);
                flowDump = $"(present)\n```json\n{json}\n```";
            }
            else
            {
                flowDump = "(no entry — FlowState is clear)";
            }
        }
        catch (Exception ex)
        {
            flowDump = $"(read failed: {ex.GetType().Name}: {ex.Message})";
        }

        // Passive token check: does BF Token Service have a cached token
        // for (userId, connection, channelId)? Doesn't trigger sign-in.
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
                var resp = await tokenClient.GetUserTokenAsync(userId, _connectionName, cid, magicCode: null!, ct);
                tokenDump = (resp is null || string.IsNullOrEmpty(resp.Token))
                    ? "(no cached token at BF Token Service)"
                    : $"(present, length={resp.Token.Length}, expiration={resp.Expiration?.ToString("u") ?? "(null)"})";
            }
            catch (Exception ex)
            {
                tokenDump = $"(GetUserTokenAsync threw: {ex.GetType().Name}: {ex.Message})";
            }
        }

        var report =
            $"### `/diag` — live OAuth state\n\n" +
            $"**activity**\n" +
            $"- channel_id raw: `{cid?.ToString() ?? "(null)"}`\n" +
            $"- channel base: `{cid?.Channel ?? "(null)"}`\n" +
            $"- channel sub: `{cid?.SubChannel ?? "(none)"}`\n" +
            $"- conversation_id: `{convId}`\n" +
            $"- user_id: `{userId}`\n\n" +
            $"**workaround**\n" +
            $"- `ChannelIdIncludesProduct = {ProtocolJsonSerializer.ChannelIdIncludesProduct}` " +
            $"({(ProtocolJsonSerializer.ChannelIdIncludesProduct ? "default — Copilot silent SSO may fail" : "workaround ENABLED")})\n\n" +
            $"**FlowState** (storage key `{flowKey}`)\n" +
            $"- {flowDump}\n\n" +
            $"**BF Token Service** (connection `{_connectionName}`)\n" +
            $"- {tokenDump}\n";

        _log.LogInformation(
            "/diag: cid_raw={Raw} cid_base={Base} cid_sub={Sub} conv={Conv} flow_key={Key} flow_present={FlowPresent} token_present={TokenPresent}",
            cid?.ToString(), cid?.Channel, cid?.SubChannel, convId, flowKey,
            !flowDump.StartsWith("(no entry"),
            !tokenDump.StartsWith("(no cached"));

        await ctx.SendActivityAsync(report, cancellationToken: ct);
    }

    // Reproduces the Paycor incident-report pattern:
    //   protected override Task SignOutUserAsync(...) {
    //       SignOutCalled = true;
    //       return Task.CompletedTask;   // never delegates, never revokes token
    //   }
    // Followed up by sending a "you are signed out" message to the user.
    // The user APPEARS signed out, but the BF Token Service still has a cached
    // token for this (userId, connection, channelId), so the next turn that
    // needs auth silently reuses it — until it goes stale, at which point the
    // user gets prompted to sign in again with NO logout having visibly
    // happened. This is the smoking gun for Scenario 2 in the incident report.
    private async Task OnSignOutBuggyAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        state.Conversation.SetValue("SignOutCalled", true);
        _log.LogWarning(
            "/signout-buggy: set SignOutCalled flag but did NOT call UserAuthorization.SignOutUserAsync. " +
            "BF token cache for user={UserId} connection={Connection} channel={Channel} is unchanged.",
            ctx.Activity.From?.Id, _connectionName, ctx.Activity.ChannelId?.ToString());
        await ctx.SendActivityAsync(
            "Signed out (BUGGY — Paycor pattern). Now send `/me` and observe the token is STILL cached.",
            cancellationToken: ct);
    }

    private async Task OnSignInFailureAsync(ITurnContext ctx, ITurnState state, string handlerName,
        SignInResponse response, IActivity initiatingActivity, CancellationToken ct)
    {
        _log.LogError("UserAuthorization sign-in failed: handler={Handler} cause={Cause} error={Error}",
            handlerName, response.Cause, response.Error?.Message);
        await ctx.SendActivityAsync(
            $"Sign-in failed (handler='{handlerName}'): {response.Cause} / {response.Error?.Message}" +
            SupportRef.Footer(ctx.Activity.Conversation?.Id),
            cancellationToken: ct);
    }

    // Feature A — /support: reply with the conversation's support reference code
    // and the current message id, and log the code -> backend-id mapping so
    // support can recover the real conversationId/activityId from the code a
    // user quotes.
    private async Task OnSupportAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var convId = ctx.Activity.Conversation?.Id;
        var code = SupportRef.Compute(convId);
        if (SupportRef.UsingFallbackSecret)
        {
            _log.LogWarning("[Support] SUPPORT_REF_SECRET is not set — using POC fallback secret. Set it in production.");
        }
        _log.LogInformation(
            "[Support] ref={Code} -> conversationId={Conv} activityId={Act} aadObjectId={Aad} utc={Utc:o} shown=command",
            code, convId, ctx.Activity.Id, ctx.Activity.From?.AadObjectId, DateTime.UtcNow);

        await ctx.SendActivityAsync(
            $"Your support reference for this conversation is **{code}**.\n\n" +
            "Quote it if you report an issue — it lets support locate this exact conversation " +
            "without exposing any private identifiers.",
            cancellationToken: ct);
    }

    // Features B/C test hook — /dropsim [text]: write a synthetic *stale* pending
    // intent so the next message you send triggers the self-healing recovery path
    // (OnBeforeTurn step 3). Lets us validate recovery deterministically without
    // reproducing the real intermittent SDK resume drop.
    private async Task OnDropSimAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var text = (ctx.Activity.Text ?? "").Trim();
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var simText = parts.Length > 1 ? parts[1].Trim() : "list employees";

        var synthetic = new Activity
        {
            Type = ActivityTypes.Message,
            Id = Guid.NewGuid().ToString("N"),
            Text = simText,
            ChannelId = ctx.Activity.ChannelId,
            ServiceUrl = ctx.Activity.ServiceUrl,
            Conversation = ctx.Activity.Conversation,
            From = ctx.Activity.From,
            Recipient = ctx.Activity.Recipient,
        };

        var pending = new PendingIntent
        {
            ActivityId = synthetic.Id,
            Text = simText,
            ConversationId = ctx.Activity.Conversation?.Id,
            // Backdate so it is already past the drop threshold.
            UtcTicks = DateTime.UtcNow.AddSeconds(-60).Ticks,
            Activity = synthetic,
        };

        await _storage.WriteAsync(
            new Dictionary<string, object> { { ResumeRecovery.PendingKey(ctx.Activity), pending } }, ct);

        _log.LogInformation("[Resume] /dropsim wrote synthetic stale pending intent text='{Text}' id={Id}",
            simText, synthetic.Id);

        await ctx.SendActivityAsync(
            $"Simulated a dropped resume for `{simText}`. Send any message now and I'll auto-recover it.",
            cancellationToken: ct);
    }


    private void LogInbound(ITurnContext ctx)
    {
        var cid = ctx.Activity.ChannelId;
        _log.LogInformation(
            "inbound activity: type={Type} text={Text} channel_id_raw={Raw} channel_base={Base} channel_sub={Sub} conv_id={Conv} user_id={User} service_url={Service}",
            ctx.Activity.Type,
            ctx.Activity.Text,
            cid?.ToString(),
            cid?.Channel,
            cid?.SubChannel,
            ctx.Activity.Conversation?.Id,
            ctx.Activity.From?.Id,
            ctx.Activity.ServiceUrl);
    }
}
