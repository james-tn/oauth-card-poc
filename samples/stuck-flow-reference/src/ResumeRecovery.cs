// Features B + C — pending-intent tracking and self-healing post-sign-in resume.
//
// PROBLEM (Mirko / Paycor): after a user signs in, the SDK is supposed to
// "resume" the request the user typed before sign-in by replaying the banked
// activity on a fresh proactive turn (UserAuthorization replays
// SignInState.ContinuationActivity). Customers report this resume sometimes
// silently DROPS — the thread just stops and the user has to retype. We cannot
// reproduce the intermittent drop (it is timing-dependent — likely a BF Token
// Service propagation race on the replay turn, or the proactive turn not
// rendering), and there is no OnUserSignInSuccess hook to observe it.
//
// APPROACH:
//   1. PENDING INTENT (write): whenever the bot is about to leave the user in a
//      pending sign-in state for a Message, SmartAuthHandler records the exact
//      activity the SDK will replay (same Activity.Id) at a per-conversation
//      key. This mirrors what UserAuthorization banks (and what resume-latest
//      updates it to).
//   2. RESUME DIAGNOSTICS + SELF-HEAL (read): an OnBeforeTurn handler on the
//      agent inspects the pending intent on each Message turn:
//        - current.Id == pending.Id  -> the genuine SDK resume is running now:
//          log "RESUME OK", clear the pending intent.
//        - pending is older than the drop threshold and the current activity is
//          something else -> the resume DROPPED: log "RESUME DROPPED", recover
//          by replaying the pending activity ourselves (ProcessProactiveAsync),
//          so the user never has to retype.
//        - pending is young and different -> the real resume may still be
//          in-flight: leave it.
//   3. DEDUPE (recovered marker): when we self-heal we DELETE the pending intent
//      first (recursion guard) and write a short-lived "recovered" marker keyed
//      by the original Activity.Id. If the genuine SDK resume then arrives LATE
//      (the resume was merely slow, not dropped), we recognise it by that marker
//      and drop it, preventing double execution. Our own recovery replay is
//      given a fresh Activity.Id so it is never mistaken for the late resume.
//
// All keys are scoped to {channel}/{conversationId}/{userId} so a message in a
// different conversation can never recover another conversation's intent.

using System.Security.Claims;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging;

namespace OAuthPocDotnet;

// Stored record: the activity the SDK is expected to replay after sign-in.
public class PendingIntent
{
    public string? ActivityId { get; set; }
    public string? Text { get; set; }
    public string? ConversationId { get; set; }
    public long UtcTicks { get; set; }
    public Activity? Activity { get; set; }
}

// Stored record: marks an Activity.Id we already recovered, so a late genuine
// SDK resume carrying that same id can be deduped instead of double-run.
public class RecoveredMarker
{
    public string? ActivityId { get; set; }
    public long UtcTicks { get; set; }
}

public static class ResumeRecovery
{
    // How long after the pending intent was recorded we consider the SDK resume
    // to have been dropped (env-configurable; default 5s).
    public static TimeSpan DropThreshold
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("RESUME_DROP_THRESHOLD_SECONDS");
            return int.TryParse(raw, out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : TimeSpan.FromSeconds(5);
        }
    }

    // Recovered markers older than this are ignored/garbage (MemoryStorage has
    // no TTL of its own).
    private static readonly TimeSpan RecoveredMarkerTtl = TimeSpan.FromSeconds(120);

    public static string PendingKey(IActivity a) =>
        $"oauthpoc/pendingIntent/{a.ChannelId?.Channel}/{a.Conversation?.Id}/{a.From?.Id}";

    public static string RecoveredKey(IActivity a, string activityId) =>
        $"oauthpoc/recovered/{a.ChannelId?.Channel}/{a.Conversation?.Id}/{a.From?.Id}/{activityId}";

    // Records (or refreshes) the pending intent = the activity the SDK will
    // replay. Best-effort: failures are swallowed (recovery simply won't fire).
    public static async Task WritePendingAsync(IStorage storage, ITurnContext ctx, ILogger? log, CancellationToken ct)
    {
        try
        {
            if (ctx.Activity is not Activity concrete)
            {
                return;
            }

            var pending = new PendingIntent
            {
                ActivityId = ctx.Activity.Id,
                Text = ctx.Activity.Text,
                ConversationId = ctx.Activity.Conversation?.Id,
                UtcTicks = DateTime.UtcNow.Ticks,
                Activity = concrete,
            };

            var key = PendingKey(ctx.Activity);
            await storage.WriteAsync(
                new Dictionary<string, object> { { key, pending } }, ct).ConfigureAwait(false);
            log?.LogInformation(
                "[Resume] pending intent recorded: key={Key} activityId={Id} text='{Text}'",
                key, pending.ActivityId, pending.Text);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "[Resume] failed to record pending intent");
        }
    }

    private static async Task<PendingIntent?> ReadPendingAsync(IStorage storage, IActivity a, CancellationToken ct)
    {
        var key = PendingKey(a);
        var items = await storage.ReadAsync(new[] { key }, ct).ConfigureAwait(false);
        return items.TryGetValue(key, out var v) ? v as PendingIntent : null;
    }

    private static Task DeletePendingAsync(IStorage storage, IActivity a, CancellationToken ct) =>
        storage.DeleteAsync(new[] { PendingKey(a) }, ct);

    // OnBeforeTurn handler. Returns false to STOP the turn (used only to dedupe a
    // late genuine resume), true to continue normally.
    public static async Task<bool> OnBeforeTurnAsync(
        IStorage storage, IAgent agent, ILogger? log, ITurnContext ctx, CancellationToken ct)
    {
        if (!ctx.Activity.IsType(ActivityTypes.Message))
        {
            return true;
        }

        var currentId = ctx.Activity.Id;

        // (0) Dedupe: is this the late genuine SDK resume of something we already
        // recovered ourselves? If so, suppress it to avoid double execution.
        if (!string.IsNullOrEmpty(currentId))
        {
            var recKey = RecoveredKey(ctx.Activity, currentId);
            var recItems = await storage.ReadAsync(new[] { recKey }, ct).ConfigureAwait(false);
            if (recItems.TryGetValue(recKey, out var recObj) && recObj is RecoveredMarker marker)
            {
                await storage.DeleteAsync(new[] { recKey }, ct).ConfigureAwait(false);
                var age = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - marker.UtcTicks);
                if (age <= RecoveredMarkerTtl)
                {
                    log?.LogInformation(
                        "[Resume] LATE genuine resume for already-recovered activityId={Id} (age={Age}s) — deduped, turn stopped.",
                        currentId, (int)age.TotalSeconds);
                    return false;
                }
            }
        }

        var pending = await ReadPendingAsync(storage, ctx.Activity, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return true;
        }

        // (1) Same-conversation guard (defensive — the key already scopes by
        // conversation).
        if (!string.IsNullOrEmpty(pending.ConversationId) &&
            !string.Equals(pending.ConversationId, ctx.Activity.Conversation?.Id, StringComparison.Ordinal))
        {
            return true;
        }

        // (2) Genuine SDK resume running now (same Activity.Id as banked).
        if (!string.IsNullOrEmpty(currentId) &&
            string.Equals(currentId, pending.ActivityId, StringComparison.Ordinal))
        {
            await DeletePendingAsync(storage, ctx.Activity, ct).ConfigureAwait(false);
            log?.LogInformation(
                "[Resume] RESUME OK — SDK replayed activityId={Id} text='{Text}'. Pending cleared.",
                currentId, pending.Text);
            return true;
        }

        var pendingAge = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - pending.UtcTicks);

        // (3) Resume dropped — self-heal by replaying the pending activity.
        if (pendingAge > DropThreshold && pending.Activity is not null)
        {
            // Delete pending FIRST (recursion guard: the proactive replay re-enters
            // OnBeforeTurn and must find no pending intent).
            await DeletePendingAsync(storage, ctx.Activity, ct).ConfigureAwait(false);

            // Write a dedupe marker so a LATE genuine resume (carrying the original
            // banked Activity.Id) is recognised and dropped.
            if (!string.IsNullOrEmpty(pending.ActivityId))
            {
                var recKey = RecoveredKey(ctx.Activity, pending.ActivityId);
                await storage.WriteAsync(
                    new Dictionary<string, object>
                    {
                        { recKey, new RecoveredMarker { ActivityId = pending.ActivityId, UtcTicks = DateTime.UtcNow.Ticks } }
                    }, ct).ConfigureAwait(false);
            }

            log?.LogWarning(
                "[Resume] RESUME DROPPED — pending activityId={Id} text='{Text}' age={Age}s exceeded threshold {Thresh}s. Self-healing via proactive replay.",
                pending.ActivityId, pending.Text, (int)pendingAge.TotalSeconds, (int)DropThreshold.TotalSeconds);

            await ctx.SendActivityAsync(
                "Looks like your earlier request didn't resume after sign-in — recovering it now, no need to retype." +
                SupportRef.Footer(ctx.Activity.Conversation?.Id),
                cancellationToken: ct).ConfigureAwait(false);

            // Replay our stored activity onto the CURRENT conversation. Give it a
            // FRESH Activity.Id so it is never mistaken for the late genuine resume
            // (which carries the original id and gets deduped above).
            var replay = pending.Activity;
            replay.Id = Guid.NewGuid().ToString("N");
            replay.ApplyConversationReference(ctx.Activity.GetConversationReference(), isIncoming: true);

            await ctx.Adapter.ProcessProactiveAsync(ctx.Identity, replay, agent, ct).ConfigureAwait(false);

            // Continue so the user's current message is also processed normally.
            return true;
        }

        // (4) Pending is young and different — the real resume may still arrive.
        // Leave it untouched.
        return true;
    }
}
