// Outbound activity tracer (experiment "A").
//
// PURPOSE
// Prove, on the wire, what an auth-RESUMED turn actually sends compared with a
// normal turn - specifically the value of ReplyToId.
//
// WHY THIS MATTERS (source-verified against Agents-for-net)
// The SDK threads every outbound activity to the inbound activity that caused it:
//
//   1. TurnContext.SendActivitiesAsync
//        -> Activity.GetConversationReference()      // reference.ActivityId = inbound Activity.Id
//        -> outgoing.ApplyConversationReference(ref) // ReplyToId = reference.ActivityId
//
//   2. When a sign-in flow resumes, the SDK replays the BANKED ContinuationActivity:
//        replay.ApplyConversationReference(ref, isIncoming: true)
//      and the incoming branch is `Id ??= reference.ActivityId` - so a banked
//      activity that already carries an Id KEEPS IT.
//
//   3. ConversationReference.GetContinuationActivity() likewise sets
//        Id = ActivityId ?? Guid.NewGuid()
//
// Consequence: on a resumed turn, ReplyToId is the id of the activity that STARTED
// the flow, not the user's actual question. If the flow was started on a
// conversationUpdate (the root cause we identified for this customer in June),
// the post-auth answer is threaded to an activity the user never saw. Auth returns
// HTTP 200, the bot really does send the answer, and nothing appears in the UI.
//
// That is the missing link between the June finding and the August symptom, and
// this tracer is how we confirm it rather than assert it.
//
// USAGE: call OutboundTrace.Attach(turnContext, logger) early in the turn.
// Then grep the container logs for [outbound].

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace CopilotLabs;

public static class OutboundTrace
{
    public static void Attach(ITurnContext ctx, ILogger log)
    {
        var inbound = ctx.Activity;

        log.LogInformation(
            "[outbound] INBOUND type={Type} name={Name} id={Id} replyToId={ReplyToId} conv={Conv} text={Text}",
            inbound?.Type,
            inbound?.Name ?? "-",
            inbound?.Id ?? "-",
            inbound?.ReplyToId ?? "-",
            inbound?.Conversation?.Id ?? "-",
            Truncate(inbound?.Text));

        ctx.OnSendActivities(async (context, activities, next) =>
        {
            foreach (var a in activities)
            {
                // ReplyToId is ALREADY stamped here: SendActivitiesAsync calls
                // ApplyConversationReference on every activity before it runs this
                // callback pipeline. So this line shows the SDK's chosen parent, and
                // the callback is also the last place we can change it.
                log.LogInformation(
                    "[outbound] PRE  type={Type} replyToId={ReplyToId} inboundId={InboundId} conv={Conv} attachments={Att} entities={Ent} text={Text}",
                    a.Type,
                    a.ReplyToId ?? "(null)",
                    context.Activity?.Id ?? "-",
                    a.Conversation?.Id ?? context.Activity?.Conversation?.Id ?? "-",
                    a.Attachments?.Count ?? 0,
                    a.Entities?.Count ?? 0,
                    Truncate(a.Text));
            }

            var responses = await next();

            for (var i = 0; i < activities.Count; i++)
            {
                var a = activities[i];
                var assignedId = i < (responses?.Length ?? 0) ? responses![i]?.Id : null;

                // This is the line that matters. If replyToId here is NOT the id of
                // the user's most recent message, the reply is threaded to a stale
                // parent - which is exactly the failure mode under investigation.
                log.LogInformation(
                    "[outbound] POST type={Type} replyToId={ReplyToId} assignedId={AssignedId} conv={Conv}",
                    a.Type,
                    a.ReplyToId ?? "(null)",
                    assignedId ?? "(none)",
                    a.Conversation?.Id ?? "-");
            }

            return responses;
        });
    }

    /// <summary>
    /// The mitigation, if the tracer confirms the stale-parent theory.
    /// Clearing ReplyToId makes the channel post the activity as a new message in
    /// the conversation instead of threading it to a possibly-invisible parent.
    /// Call this on the outbound activity of a resumed turn.
    /// </summary>
    public static void DetachFromStaleParent(ITurnContext ctx, ILogger log)
    {
        ctx.OnSendActivities(async (context, activities, next) =>
        {
            foreach (var a in activities)
            {
                if (a.ReplyToId != null)
                {
                    log.LogInformation("[outbound] clearing stale replyToId={ReplyToId}", a.ReplyToId);
                }
                a.ReplyToId = null;
            }

            return await next();
        });
    }

    private static string Truncate(string? s, int max = 60)
        => string.IsNullOrEmpty(s) ? "-" : (s.Length <= max ? s : s[..max] + "...");
}
