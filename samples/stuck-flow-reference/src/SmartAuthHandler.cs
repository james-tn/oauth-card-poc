// Workaround B reference implementation: a custom IUserAuthorization that
// short-circuits the SDK's OAuthFlow when the user types a plain text message
// during a pending sign-in flow.
//
// The SDK's default AzureBotUserAuthorization treats any incoming Message
// activity (while FlowStarted=true) as a possible magic-code submission.
// If the text doesn't contain 6 digits, ContinueCount++ and the SDK sends
// "Invalid sign in code" to the user, then throws AuthException("Retry max")
// after InvalidSignInRetryMax (default 2) such turns.
//
// This wrapper intercepts SignInUserAsync: when the activity is a Message
// whose Trim()'d Text isn't exactly ^\d{6}$ (i.e. not a magic code), it does NOT
// delegate to the stock continue-flow path (which would send "Invalid sign in
// code"). Instead, controlled by SMARTAUTH_STRAY_BEHAVIOR:
//   - "smart" (default): the robust behavior. Recovers a stuck flow and avoids
//     card spam. On a stray non-code message it:
//       * The /signout escape command fully clears the pending sign-in — inner
//         FlowState, the outer (user+channel) SignInState banked by
//         UserAuthorization, and our pending/card ledgers — so the next message
//         routes normally. (During a pending flow the SDK runs auth before routes
//         and returns early, so the agent's own /signout route is unreachable;
//         intercepting it here makes that command work while stuck.) This also
//         unsticks the "You're almost signed in…" loop that otherwise persists
//         across new chats and page reloads (the outer SignInState is user+channel
//         scoped, NOT conversation scoped).
//       * Otherwise it makes the current message what replays after sign-in, then
//         either nudges (if a card was issued in this conversation within
//         SMARTAUTH_RECARD_THROTTLE_SECONDS, default 8s) or re-presents a fresh
//         sign-in card here (new chat / reload / stale card). A per-conversation
//         cardState ledger drives the throttle. Per-user+channel locking
//         serializes these mutations (single-instance / MemoryStorage).
//   - "resume-latest": leaves the pending flow intact, overwrites the
//     SDK-banked ContinuationActivity with the *current* (latest) message, and
//     sends a brief "no need to retype" nudge. Once the user completes sign-in,
//     the SDK replays the user's MOST RECENT request instead of the stale first
//     message that originally started the flow. The SDK's UserAuthorization banks
//     ContinuationActivity only at flow start (line ~220 of UserAuthorization.cs)
//     and never updates it, so messages sent before finishing sign-in are lost.
//   - "hold": same nudge, but does NOT touch SDK state. The originally banked
//     (first) message resumes after sign-in. Portable across SDK versions /
//     storage providers since it depends on no internal types.
//   - "recard": resets the pending FlowState and re-starts the flow (re-presents
//     the Sign in button). NOTE: this resets the *inner* handler flow while the
//     *outer* SignInState.ActiveHandler stays set, an inconsistency that can
//     contribute to stalls; kept only for A/B comparison.
//   - "nudge": legacy plain-text reminder that asks the user to resend.
//
// With every behavior except a hard reset, the app-level SignInState stays
// active, so a banked user message is replayed automatically once sign-in
// completes.
//
// To activate, configure in env vars (or appsettings.json):
//   AgentApplication:UserAuthorization:Handlers:auto:Assembly = "OAuthPocDotnet"
//   AgentApplication:UserAuthorization:Handlers:auto:Type     = "OAuthPocDotnet.SmartAuthHandler"
//   AgentApplication:UserAuthorization:Handlers:auto:Settings:AzureBotOAuthConnectionName = <same as before>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OAuthPocDotnet;

// Records when we last presented a sign-in card in a conversation, so we can
// throttle re-carding (avoid card spam) yet still recover a stuck flow.
public class CardState
{
    public long LastCardIssuedUtcTicks { get; set; }
}

public class SmartAuthHandler : IUserAuthorization
{
    private static readonly Regex SixDigits = new Regex(@"^\d{6}$", RegexOptions.Compiled);
    private static readonly HashSet<string> EscapeCommands =
        new(StringComparer.OrdinalIgnoreCase) { "/signout" };

    // Serializes auth-flow mutations per user+channel (single-instance / MemoryStorage).
    // Multi-instance deployments would need storage ETags or a distributed lock.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private readonly AzureBotUserAuthorization _inner;
    private readonly IStorage _storage;
    private readonly ILogger _logger;
    private readonly string _name;

    public SmartAuthHandler(
        string name,
        IStorage storage,
        IConnections connections,
        IConfigurationSection configurationSection,
        ILogger logger = null)
    {
        _inner = new AzureBotUserAuthorization(name, storage, connections, configurationSection, logger);
        _storage = storage;
        _logger = logger;
        _name = name;
    }

    public string Name => _inner.Name;

    public async Task<TokenResponse> SignInUserAsync(
        ITurnContext context,
        bool forceSignIn = false,
        string exchangeConnection = null,
        IList<string> exchangeScopes = null,
        CancellationToken cancellationToken = default)
    {
        // Test mode: simulate Mirko's customer's apparent double-invocation pattern.
        // When SIMULATE_DOUBLE_INVOKE=true, call the inner handler TWICE per logical
        // invocation. First call sends the card (FlowStarted=true). Second call sees
        // FlowStarted=true → OnContinueFlow → message text (e.g. "/login") doesn't
        // match the 6-digit regex → ContinueCount++ → "Invalid sign in code" message
        // is sent to the user alongside the card. Reproduces the demo symptom.
        if (string.Equals(System.Environment.GetEnvironmentVariable("SIMULATE_DOUBLE_INVOKE"),
                          "true", System.StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("[SmartAuth] SIMULATE_DOUBLE_INVOKE: calling inner twice");
            var first = await _inner.SignInUserAsync(context, forceSignIn, exchangeConnection, exchangeScopes, cancellationToken).ConfigureAwait(false);
            var second = await _inner.SignInUserAsync(context, forceSignIn: false, exchangeConnection, exchangeScopes, cancellationToken).ConfigureAwait(false);
            return second ?? first;
        }

        if (!forceSignIn && context.Activity.IsType(ActivityTypes.Message))
        {
            var text = (context.Activity.Text ?? "").Trim();
            if (!SixDigits.IsMatch(text))
            {
                // The user typed a (non-code) message while a sign-in is pending.
                // Behavior is env-selectable (see file header):
                //   smart (default) | resume-latest | hold | recard | nudge
                var behavior = (System.Environment.GetEnvironmentVariable("SMARTAUTH_STRAY_BEHAVIOR")
                                ?? "smart").Trim().ToLowerInvariant();

                if (behavior == "smart")
                {
                    return await HandleStraySmartAsync(
                        context, text, exchangeConnection, exchangeScopes, cancellationToken).ConfigureAwait(false);
                }

                if (behavior == "recard")
                {
                    _logger?.LogInformation(
                        "[SmartAuth] non-code message during pending flow; re-presenting sign-in card. text='{Text}'",
                        text);
                    // Clear the in-flight FlowState so the next call starts (not continues)
                    // the flow, which re-sends the OAuth card via BeginFlowAsync.
                    await _inner.ResetStateAsync(context, cancellationToken).ConfigureAwait(false);
                    return await _inner.SignInUserAsync(
                        context, forceSignIn: true, exchangeConnection, exchangeScopes, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (behavior == "nudge")
                {
                    _logger?.LogInformation(
                        "[SmartAuth] non-code message during pending flow; nudge. text='{Text}'", text);
                    await context.SendActivityAsync(
                        "Please complete sign-in by clicking the **Sign in** button above, then send your request again.",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return null;
                }

                // resume-latest: update the SDK-banked ContinuationActivity to this
                // (latest) message so it is what replays after sign-in. Falls back to
                // "hold" semantics if the update can't be applied.
                if (behavior == "resume-latest")
                {
                    var updated = await TryUpdateBankedContinuationAsync(context, cancellationToken).ConfigureAwait(false);
                    _logger?.LogInformation(
                        "[SmartAuth] non-code message during pending flow; resume-latest (banked updated={Updated}). text='{Text}'",
                        updated, text);
                    // Keep the self-heal pending intent in sync with what the SDK will
                    // now replay (the latest message). Only when the banked update
                    // actually applied — otherwise "hold" semantics apply and the
                    // original first-message pending intent must stand.
                    if (updated)
                    {
                        await ResumeRecovery.WritePendingAsync(_storage, context, _logger, cancellationToken).ConfigureAwait(false);
                    }
                }
                else // "hold" or any unrecognized value
                {
                    _logger?.LogInformation(
                        "[SmartAuth] non-code message during pending flow; hold. text='{Text}'", text);
                }

                // Leave the pending flow intact and reassure the user: no retype needed.
                await context.SendActivityAsync(
                    "You're almost signed in — just click the **Sign in** button above. " +
                    "I'll run your request automatically once you're signed in; no need to retype it.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        var result = await _inner.SignInUserAsync(context, forceSignIn, exchangeConnection, exchangeScopes, cancellationToken).ConfigureAwait(false);

        // First-trigger path: the inner handler just began a sign-in flow (returns
        // null = pending) and banked THIS activity as the ContinuationActivity.
        // Record the matching self-heal pending intent (same Activity.Id) so the
        // agent's OnBeforeTurn can detect whether the SDK resume fires or drops.
        if (result is null && context.Activity.IsType(ActivityTypes.Message))
        {
            await ResumeRecovery.WritePendingAsync(_storage, context, _logger, cancellationToken).ConfigureAwait(false);
            // A begin-flow (forceSignIn) that returned pending just presented a card;
            // record the issuance time so smart re-carding can throttle correctly.
            if (forceSignIn)
            {
                await WriteCardStateAsync(context, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            }
        }
        return result;
    }

    // ---- smart stray-message handling (default) --------------------------------
    // Goal: never leave the user stuck. If a card was just shown in THIS
    // conversation, nudge (don't spam). Otherwise — new chat, page reload, or the
    // card went stale — re-present a fresh card here. Escape commands fully clear
    // the pending sign-in (inner + outer + ledgers) so the user can start over.
    private async Task<TokenResponse> HandleStraySmartAsync(
        ITurnContext context,
        string text,
        string exchangeConnection,
        IList<string> exchangeScopes,
        CancellationToken cancellationToken)
    {
        var sem = GetLock(context);
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Escape hatch: fully cancel the pending sign-in.
            if (EscapeCommands.Contains(text))
            {
                await _inner.ResetStateAsync(context, cancellationToken).ConfigureAwait(false);
                await DeleteOuterSignInStateAsync(context, cancellationToken).ConfigureAwait(false);
                await DeletePendingAndCardStateAsync(context, cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation(
                    "[SmartAuth] smart: escape '{Text}' — cleared inner+outer sign-in state.", text);
                await context.SendActivityAsync(
                    "Okay — I've cancelled the pending sign-in. Send your request again to start fresh.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return null;
            }

            // Make THIS message (in THIS conversation) what replays after sign-in.
            var updated = await TryUpdateBankedContinuationAsync(context, cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                await ResumeRecovery.WritePendingAsync(_storage, context, _logger, cancellationToken).ConfigureAwait(false);
            }

            var throttle = ThrottleWindow();
            var card = await ReadCardStateAsync(context, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var recentlyCarded = card != null
                && (now - new DateTime(card.LastCardIssuedUtcTicks, DateTimeKind.Utc)) < throttle;

            if (recentlyCarded)
            {
                _logger?.LogInformation(
                    "[SmartAuth] smart: recent card in this conversation; nudging (banked updated={Updated}). text='{Text}'",
                    updated, text);
                await context.SendActivityAsync(
                    "You're almost signed in — just click the **Sign in** button above. " +
                    "I'll run your request automatically once you're signed in; no need to retype it. " +
                    "(If you don't see the button, type **/signout** and start over.)",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return null;
            }

            // No recent card here → new chat, reload, or stale: present a fresh one.
            _logger?.LogInformation(
                "[SmartAuth] smart: re-presenting sign-in card in this conversation (banked updated={Updated}). text='{Text}'",
                updated, text);
            await _inner.ResetStateAsync(context, cancellationToken).ConfigureAwait(false);
            await WriteCardStateAsync(context, now, cancellationToken).ConfigureAwait(false);
            return await _inner.SignInUserAsync(
                context, forceSignIn: true, exchangeConnection, exchangeScopes, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static SemaphoreSlim GetLock(ITurnContext context)
    {
        var channel = context.Activity.ChannelId?.Channel ?? "unknown";
        var userId = context.Activity.From?.Id ?? "unknown";
        return _locks.GetOrAdd($"{channel}/{userId}", _ => new SemaphoreSlim(1, 1));
    }

    private static TimeSpan ThrottleWindow()
    {
        var raw = System.Environment.GetEnvironmentVariable("SMARTAUTH_RECARD_THROTTLE_SECONDS");
        if (int.TryParse(raw, out var s) && s > 0)
        {
            return TimeSpan.FromSeconds(s);
        }
        return TimeSpan.FromSeconds(8);
    }

    private static string OuterKey(ITurnContext context)
    {
        var channel = context.Activity.ChannelId?.Channel;
        var userId = context.Activity.From?.Id;
        return $"oauth/{channel}/{userId}/userAuthorizationState";
    }

    private static string CardStateKey(ITurnContext context)
    {
        var channel = context.Activity.ChannelId?.Channel;
        var conv = context.Activity.Conversation?.Id;
        var userId = context.Activity.From?.Id;
        return $"oauthpoc/cardState/{channel}/{conv}/{userId}";
    }

    private async Task<CardState> ReadCardStateAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            var key = CardStateKey(context);
            var items = await _storage.ReadAsync(new[] { key }, cancellationToken).ConfigureAwait(false);
            return items.TryGetValue(key, out var v) ? v as CardState : null;
        }
        catch (System.Exception ex)
        {
            _logger?.LogWarning(ex, "[SmartAuth] smart: failed to read card state");
            return null;
        }
    }

    private async Task WriteCardStateAsync(ITurnContext context, DateTime utc, CancellationToken cancellationToken)
    {
        try
        {
            var key = CardStateKey(context);
            await _storage.WriteAsync(
                new Dictionary<string, object> { { key, new CardState { LastCardIssuedUtcTicks = utc.Ticks } } },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            _logger?.LogWarning(ex, "[SmartAuth] smart: failed to write card state");
        }
    }

    // Deletes the UserAuthorization-banked outer SignInState (user+channel scoped).
    // This is what keeps a flow "active" across conversations/reloads; removing it
    // lets the next turn route normally instead of being treated as a continuation.
    private async Task DeleteOuterSignInStateAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(new[] { OuterKey(context) }, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            _logger?.LogWarning(ex, "[SmartAuth] smart: failed to delete outer sign-in state");
        }
    }

    private async Task DeletePendingAndCardStateAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(
                new[] { ResumeRecovery.PendingKey(context.Activity), CardStateKey(context) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            _logger?.LogWarning(ex, "[SmartAuth] smart: failed to delete pending/card ledgers");
        }
    }

    // Overwrites the UserAuthorization-banked ContinuationActivity (stored by the
    // SDK at "oauth/{channel}/{userId}/userAuthorizationState") with the current
    // activity, so the user's most recent request is what the SDK replays after
    // sign-in. SignInState is an internal SDK type, so the public ContinuationActivity
    // property is set via reflection. Best-effort: any failure is swallowed and the
    // originally banked message remains (equivalent to "hold").
    private async Task<bool> TryUpdateBankedContinuationAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            var channel = context.Activity.ChannelId?.Channel;
            var userId = context.Activity.From?.Id;
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(userId))
            {
                return false;
            }

            var key = $"oauth/{channel}/{userId}/userAuthorizationState";
            var items = await _storage.ReadAsync(new[] { key }, cancellationToken).ConfigureAwait(false);
            if (!items.TryGetValue(key, out var state) || state is null)
            {
                return false;
            }

            var prop = state.GetType().GetProperty("ContinuationActivity");
            if (prop is null || !prop.CanWrite)
            {
                return false;
            }

            prop.SetValue(state, context.Activity);
            await _storage.WriteAsync(
                new Dictionary<string, object> { { key, state } }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (System.Exception ex)
        {
            _logger?.LogWarning(ex, "[SmartAuth] resume-latest: failed to update banked ContinuationActivity");
            return false;
        }
    }

    public Task SignOutUserAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
        => _inner.SignOutUserAsync(turnContext, cancellationToken);

    public Task ResetStateAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
        => _inner.ResetStateAsync(turnContext, cancellationToken);

    public Task<TokenResponse> GetRefreshedUserTokenAsync(
        ITurnContext turnContext,
        string exchangeConnection = null,
        IList<string> exchangeScopes = null,
        CancellationToken cancellationToken = default)
        => _inner.GetRefreshedUserTokenAsync(turnContext, exchangeConnection, exchangeScopes, cancellationToken);
}
