// Long-running operation lab (experiments "B" and "C").
//
// PURPOSE
// Establish empirically what the M365 Copilot / Teams timeout budget actually is,
// and which mitigation strategies survive it.
//
// THE SPEC (Microsoft Learn, verbatim)
//   "Each user query should receive an initial response within 15 seconds.
//    For long-running tasks, agents can send follow-up messages.
//    A 45-second timeout applies between streaming updates."
//
// So there are TWO budgets, not one:
//   - 15s to the FIRST response of any kind
//   - 45s between CONSECUTIVE streaming updates
// A single reply that takes 40s is already out of spec even though it beats 45s.
//
// WHAT WE OBSERVED FOR THIS CUSTOMER
// Their timeout capture showed "AsyncChannelTimeout" and "timeout error after
// 45000ms" with no auth involved, and ZERO streaming markers in any of the four
// HAR files. A typing indicator at +2.5s did NOT prevent the timeout - which is
// worth proving, because "just send a typing indicator" is common advice and it
// is wrong.
//
// VARIANTS (send "/slow <variant> <seconds>"):
//   naive       - block for N seconds, then reply once.        Expect: timeout past ~45s.
//   typing      - typing indicator, then block N seconds.      Expect: STILL times out.
//   stream      - informative update now, chunks every ~10s.   Expect: survives.
//   informative - heartbeat updates while working, answer at   Expect: survives.
//                 the end only.
//   proactive   - ack now, finish in background, deliver later. Expect: survives, no budget.
//
// "informative" is the variant that matters for an agent which genuinely has
// nothing to show until the work completes - one long backend call, then a
// formatted answer. "Just stream your tokens" is not actionable for that shape;
// holding the stream open with heartbeat updates is.
//
// The proactive variant is the only one with no upper bound. Use it for work that
// can genuinely exceed a minute.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace CopilotLabs;

public static class LongRunningLab
{
    public static string Help =>
        "**Long-running lab** - which strategies survive the channel timeout?\n\n" +
        "- `/slow naive 60` - block, then reply once (expect `AsyncChannelTimeout`)\n" +
        "- `/slow typing 60` - typing indicator then block (expect it *still* times out)\n" +
        "- `/slow stream 60` - streamed updates (expect success)\n" +
        "- `/slow informative 60` - heartbeat updates, answer only at the end (expect success)\n" +
        "- `/slow proactive 90` - immediate ack + background delivery (expect success)\n\n" +
        "Budget: **15s** to first response, **45s** between streaming updates.";

    public static async Task HandleAsync(
        ITurnContext ctx,
        ILogger log,
        string variant,
        int seconds,
        CancellationToken ct)
    {
        variant = (variant ?? "").Trim().ToLowerInvariant();
        seconds = Math.Clamp(seconds <= 0 ? 60 : seconds, 1, 300);

        var started = DateTimeOffset.UtcNow;
        log.LogInformation("[slow] variant={Variant} seconds={Seconds} start={Start:O}", variant, seconds, started);

        switch (variant)
        {
            case "naive":
                await NaiveAsync(ctx, log, seconds, started, ct);
                break;
            case "typing":
                await TypingAsync(ctx, log, seconds, started, ct);
                break;
            case "stream":
                await StreamAsync(ctx, log, seconds, started, ct);
                break;
            case "informative":
                await InformativeAsync(ctx, log, seconds, started, ct);
                break;
            case "proactive":
                await ProactiveAsync(ctx, log, seconds, started, ct);
                break;
            default:
                await ctx.SendActivityAsync(MessageFactory.Text(Help), ct);
                break;
        }
    }

    // ---------------------------------------------------------------------
    // ANTI-PATTERN 1 - block the turn, then answer.
    // This is what the customer is doing today. Past roughly 45s the channel has
    // already given up; the reply may never render even though the bot sent it.
    // ---------------------------------------------------------------------
    private static async Task NaiveAsync(ITurnContext ctx, ILogger log, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        await SendResultAsync(ctx, log, "naive", seconds, started, ct);
    }

    // ---------------------------------------------------------------------
    // ANTI-PATTERN 2 - typing indicator, then block.
    // A typing indicator is NOT a response. It does not reset the budget.
    // Worth proving explicitly, because this is the most common wrong fix.
    // ---------------------------------------------------------------------
    private static async Task TypingAsync(ITurnContext ctx, ILogger log, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        await ctx.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, ct);
        log.LogInformation("[slow] typing sent at +{Elapsed:F1}s", Elapsed(started));

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        await SendResultAsync(ctx, log, "typing", seconds, started, ct);
    }

    // ---------------------------------------------------------------------
    // FIX 1 - STREAMING. Satisfies both budgets:
    //   - QueueInformativeUpdateAsync lands immediately  -> beats the 15s budget
    //   - a chunk at least every 10s                     -> beats the 45s budget
    //
    // Note the interval is deliberately well under 45s. Aiming for 44s leaves no
    // margin for network jitter or a slow upstream call.
    // ---------------------------------------------------------------------
    private static async Task StreamAsync(ITurnContext ctx, ILogger log, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        var stream = ctx.StreamingResponse;
        const int HeartbeatSeconds = 10;

        try
        {
            await stream.QueueInformativeUpdateAsync("Working on it...", ct);
            log.LogInformation("[slow] informative update at +{Elapsed:F1}s isStreamingChannel={S}",
                Elapsed(started), stream.IsStreamingChannel);

            stream.QueueTextChunk($"Running a {seconds}s operation.\n\n");

            var remaining = seconds;
            var step = 1;
            while (remaining > 0)
            {
                var slice = Math.Min(HeartbeatSeconds, remaining);
                await Task.Delay(TimeSpan.FromSeconds(slice), ct);
                remaining -= slice;

                // Every chunk resets the 45s inter-update clock.
                stream.QueueTextChunk($"Step {step++} complete ({seconds - remaining}s of {seconds}s)...\n");
                log.LogInformation("[slow] chunk at +{Elapsed:F1}s", Elapsed(started));
            }

            stream.QueueTextChunk($"\n**Done** after {Elapsed(started):F1}s.");
        }
        finally
        {
            var result = await stream.EndStreamAsync(ct);
            log.LogInformation("[slow] variant=stream finished at +{Elapsed:F1}s result={Result}",
                Elapsed(started), result);
        }
    }

    // ---------------------------------------------------------------------
    // FIX 1b - HEARTBEAT INFORMATIVE UPDATES. The realistic pattern for an agent
    // that has NOTHING to show until the work finishes.
    //
    // Repeated QueueInformativeUpdateAsync calls are explicitly allowed - the SDK
    // only rejects them after EndStreamAsync. Each one emits a typing activity
    // carrying StreamInfo{ StreamType = Informative } with an incrementing
    // sequence, which resets the 45s inter-update clock without polluting the
    // final message. The real answer is queued as text only at the very end.
    //
    // TWO CAVEATS, both from StreamingResponse.cs:
    //  1. QueueInformativeUpdateAsync returns immediately and does nothing when
    //     IsStreamingChannel is false, so on a non-streaming channel there is no
    //     heartbeat at all and you are back to the naive case.
    //  2. You must queue at least one real text chunk before EndStreamAsync.
    //     With no chunks, Message is empty and the final message becomes
    //     "No text was streamed" with StreamResults.Error.
    // ---------------------------------------------------------------------
    private static async Task InformativeAsync(ITurnContext ctx, ILogger log, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        var stream = ctx.StreamingResponse;
        const int HeartbeatSeconds = 10;

        try
        {
            log.LogInformation("[slow] variant=informative isStreamingChannel={S}", stream.IsStreamingChannel);
            await stream.QueueInformativeUpdateAsync("Working on it...", ct);

            var remaining = seconds;
            var beat = 1;
            while (remaining > 0)
            {
                var slice = Math.Min(HeartbeatSeconds, remaining);
                await Task.Delay(TimeSpan.FromSeconds(slice), ct);
                remaining -= slice;

                if (remaining <= 0)
                {
                    break;
                }

                // Holds the stream open. No fabricated progress text in the answer.
                await stream.QueueInformativeUpdateAsync($"Still working... ({seconds - remaining}s elapsed)", ct);
                log.LogInformation("[slow] informative heartbeat {Beat} at +{Elapsed:F1}s", beat++, Elapsed(started));
            }

            // Only now is there anything to say. This is the entire visible answer.
            stream.QueueTextChunk(
                $"**Here is your answer.**\n\n" +
                $"The backend call took {Elapsed(started):F1}s. Nothing was shown until it " +
                $"completed, but the stream was held open with informative updates so the " +
                $"channel never timed out.");
        }
        finally
        {
            var result = await stream.EndStreamAsync(ct);
            log.LogInformation("[slow] variant=informative finished at +{Elapsed:F1}s result={Result}",
                Elapsed(started), result);
        }
    }

    // ---------------------------------------------------------------------
    // FIX 2 - ASYNCHRONOUS / PROACTIVE. The only pattern with no upper bound.
    //
    //   1. Acknowledge inside the turn (well under 15s) and RETURN.
    //   2. Do the real work on a background task.
    //   3. Deliver the result later with ProcessProactiveAsync.
    //
    // IMPORTANT (and this is the part most implementations get wrong):
    // ConversationReference.GetContinuationActivity() sets
    //     Id = ActivityId ?? Guid.NewGuid()
    // so the seeded turn inherits the ORIGINAL activity id, and every reply sent
    // from it gets ReplyToId = that original id. If the original activity is old
    // or was never a user-visible message, the delivered answer can be threaded to
    // an invisible parent. We clear ReplyToId below so the result posts as a new
    // message. See OutboundTrace for the source-level detail.
    // ---------------------------------------------------------------------
    private static async Task ProactiveAsync(
        ITurnContext ctx,
        ILogger log,
        int seconds,
        DateTimeOffset started,
        CancellationToken ct)
    {
        // Everything the background task needs must be captured NOW - the
        // ITurnContext is disposed when this turn ends.
        var identity = ctx.Identity;
        var adapter = ctx.Adapter;
        var reference = ctx.Activity.GetConversationReference();
        var question = ctx.Activity.Text;

        await ctx.SendActivityAsync(
            MessageFactory.Text($"Got it - this will take about {seconds}s. I'll follow up here when it's done."),
            ct);
        log.LogInformation("[slow] ack sent at +{Elapsed:F1}s, handing off to background", Elapsed(started));

        // Deliberately NOT awaited and deliberately NOT using the turn's
        // CancellationToken - that token is cancelled when the turn completes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), CancellationToken.None);

                var continuation = reference.GetContinuationActivity();

                await adapter.ProcessProactiveAsync(
                    identity,
                    continuation,
                    audience: null,
                    callback: async (proactiveCtx, pct) =>
                    {
                        OutboundTrace.Attach(proactiveCtx, log);

                        // Post as a new message rather than threading to the
                        // inherited (possibly stale) parent activity id.
                        OutboundTrace.DetachFromStaleParent(proactiveCtx, log);

                        await proactiveCtx.SendActivityAsync(
                            MessageFactory.Text(
                                $"**Done** after {Elapsed(started):F1}s.\n\n" +
                                $"Delivered proactively, outside the original turn. " +
                                $"Original question: _{question}_"),
                            pct);
                    },
                    CancellationToken.None);

                log.LogInformation("[slow] proactive delivery succeeded at +{Elapsed:F1}s", Elapsed(started));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[slow] proactive delivery FAILED at +{Elapsed:F1}s", Elapsed(started));
            }
        }, CancellationToken.None);
    }

    private static Task SendResultAsync(ITurnContext ctx, ILogger log, string variant, int seconds, DateTimeOffset started, CancellationToken ct)
    {
        var elapsed = Elapsed(started);
        log.LogInformation("[slow] variant={Variant} sending result at +{Elapsed:F1}s", variant, elapsed);

        return ctx.SendActivityAsync(
            MessageFactory.Text(
                $"Finished `{variant}` after **{elapsed:F1}s** (requested {seconds}s).\n\n" +
                "If you can read this in the client, the channel did not time out."),
            ct);
    }

    private static double Elapsed(DateTimeOffset started) => (DateTimeOffset.UtcNow - started).TotalSeconds;
}
