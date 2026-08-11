// Citation rendering lab (experiment "D").
//
// PURPOSE
// Determine whether an M365 Copilot custom engine agent can produce a CLICKABLE
// citation when the answer is delivered as an Adaptive Card, or whether citations
// structurally only bind to message Text.
//
// BACKGROUND (Paycor/Paychex WISE escalation, Aug 2026)
// Plain-text answers render citations correctly and remain clickable. The same
// content rendered as an Adaptive Card table shows a literal "[1]" that is not
// clickable. The captured channel payload for the 48 KB card had entities: []
// and contained no citation entity, no citation URL, and no Action.OpenUrl.
//
// THE OPEN QUESTION THIS LAB ANSWERS
// Was the citation missing because the application never sent it, or because
// citations do not bind to Adaptive Card content at all? The HAR only shows the
// post-channel representation, so it cannot distinguish these. This lab sends a
// KNOWN-CORRECT citation in four different shapes and lets us observe which ones
// the client actually renders as clickable.
//
// Docs state two relevant constraints:
//   - "Citations with Adaptive Cards are available in public developer preview."
//   - "Adaptive Cards aren't rendered in the citation pop-up window."
// If variant "card" fails while "both" succeeds, the fix for the customer is to
// keep the card but carry the [n] markers in the message Text.
//
// USAGE:  /cite text | card | both | stream

using System.Text.Json.Nodes;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace CopilotLabs;

public static class CitationLab
{
    // A real, resolvable URL so a rendered citation is visibly clickable.
    private const string SourceUrl = "https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/bot-messages-ai-generated-content";
    private const string SourceName = "Employee directory - HR system";
    private const string SourceAbstract = "Active employee roster, refreshed nightly from the HR system of record.";

    public static string Help =>
        "**Citation lab** - which shapes produce a *clickable* citation?\n\n" +
        "- `/cite text` - message Text with `[1]` + citation entity (control)\n" +
        "- `/cite card` - Adaptive Card only, `[1]` inside a TextBlock (the broken shape)\n" +
        "- `/cite both` - message Text with `[1]` + citation entity **and** the card attached (candidate fix)\n" +
        "- `/cite stream` - streamed response, card attached via `FinalMessage` (the 1.5.x-era path)\n" +
        "- `/cite stream17` - streamed response, card attached via `AddAttachment` (requires SDK >= 1.6)";

    public static async Task HandleAsync(ITurnContext ctx, ILogger log, string variant, CancellationToken ct)
    {
        variant = (variant ?? "").Trim().ToLowerInvariant();

        switch (variant)
        {
            case "text":
                await SendTextOnlyAsync(ctx, log, ct);
                break;
            case "card":
                await SendCardOnlyAsync(ctx, log, ct);
                break;
            case "both":
                await SendTextPlusCardAsync(ctx, log, ct);
                break;
            case "stream":
                await SendStreamedAsync(ctx, log, ct);
                break;
            case "stream17":
                await SendStreamedWithAddAttachmentAsync(ctx, log, ct);
                break;
            default:
                await ctx.SendActivityAsync(MessageFactory.Text(Help), ct);
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Variant 1 - CONTROL. Text with an in-text [1] plus the citation entity.
    // This mirrors the customer's WORKING plain-text case. If this does not
    // render clickable, the problem is environmental, not shape-related.
    // ---------------------------------------------------------------------
    private static Task SendTextOnlyAsync(ITurnContext ctx, ILogger log, CancellationToken ct)
    {
        var activity = MessageFactory.Text(
            "**Active employees (text only)**\n\n" +
            "- Ada Lovelace - Engineering\n" +
            "- Grace Hopper - Engineering\n" +
            "- Katherine Johnson - Finance\n\n" +
            "Source: employee directory [1]");

        AttachCitation(activity);
        LogShape(log, "text", activity);
        return ctx.SendActivityAsync(activity, ct);
    }

    // ---------------------------------------------------------------------
    // Variant 2 - THE CUSTOMER'S CURRENT SHAPE. All content lives inside an
    // Adaptive Card; the "[1]" is a TextBlock inside the card. The citation
    // entity is still attached correctly at the activity level.
    //
    // Expectation: NOT clickable, because the citation renderer scans the
    // message Text for [n] markers and there is no message Text here.
    // If that holds, the customer's issue is structural, not a missing field.
    // ---------------------------------------------------------------------
    private static Task SendCardOnlyAsync(ITurnContext ctx, ILogger log, CancellationToken ct)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Attachments = [BuildEmployeeCard(includeCitationMarker: true)]
        };

        AttachCitation(activity);
        LogShape(log, "card", activity);
        return ctx.SendActivityAsync(activity, ct);
    }

    // ---------------------------------------------------------------------
    // Variant 3 - CANDIDATE FIX. Keep the Adaptive Card for the table, but put
    // the in-text [1] marker in the message Text where the citation renderer
    // can bind to it. Card and citation coexist on one activity.
    // ---------------------------------------------------------------------
    private static Task SendTextPlusCardAsync(ITurnContext ctx, ILogger log, CancellationToken ct)
    {
        var activity = MessageFactory.Text("Here are the active employees, from the employee directory [1]");
        activity.Attachments = [BuildEmployeeCard(includeCitationMarker: false)];

        AttachCitation(activity);
        LogShape(log, "both", activity);
        return ctx.SendActivityAsync(activity, ct);
    }

    // ---------------------------------------------------------------------
    // Variant 4 - MODERN SUPPORTED PATH. Stream the answer, attach the card to
    // the final streamed message, and register the citation through the SDK's
    // own streaming API rather than hand-building the entity.
    //
    // This also demonstrates the fix for the SEPARATE 45s timeout issue: the
    // informative update lands immediately, so the channel sees a response well
    // inside the 15s initial-response budget.
    // ---------------------------------------------------------------------
    private static async Task SendStreamedAsync(ITurnContext ctx, ILogger log, CancellationToken ct)
    {
        var stream = ctx.StreamingResponse;
        var full = new System.Text.StringBuilder();

        void Chunk(string s)
        {
            full.Append(s);
            stream.QueueTextChunk(s);
        }

        try
        {
            log.LogInformation("[CiteLab] variant=stream isStreamingChannel={IsStreaming}", stream.IsStreamingChannel);

            await stream.QueueInformativeUpdateAsync("Looking up active employees...", ct);

            Chunk("**Active employees**\n\n");
            await Task.Delay(400, ct);
            Chunk("- Ada Lovelace - Engineering\n");
            await Task.Delay(400, ct);
            Chunk("- Grace Hopper - Engineering\n");
            await Task.Delay(400, ct);
            Chunk("- Katherine Johnson - Finance\n\n");
            Chunk("Source: employee directory [1]");

            stream.AddCitation(BuildClientCitation());

            // NOTE for 1.5.184: IStreamingResponse has no AddAttachment. The only way
            // to put an attachment on the final streamed message is to supply
            // FinalMessage. Two gotchas, both confirmed in StreamingResponse.cs:
            //  1. CreateFinalMessage only auto-fills Text when FinalMessage is null,
            //     so setting FinalMessage means YOU must carry the streamed text over
            //     or it is lost.
            //  2. Citations are still resolved against the ACCUMULATED streamed text,
            //     so the [1] marker must appear in the chunks above, not only here.
            stream.FinalMessage = new Activity
            {
                Type = ActivityTypes.Message,
                Text = full.ToString(),
                Attachments = [BuildEmployeeCard(includeCitationMarker: false)]
            };
        }
        finally
        {
            var result = await stream.EndStreamAsync(ct);
            log.LogInformation("[CiteLab] variant=stream endStreamResult={Result} updatesSent={Updates}",
                result, stream.UpdatesSent());
        }
    }

    // ---------------------------------------------------------------------
    // Variant 5: streamed, but the card is attached with AddAttachment instead
    // of FinalMessage. AddAttachment does not exist in 1.5.184; it was added in
    // 1.6.x and is present in 1.7.129. It sidesteps the FinalMessage text-drop
    // trap entirely, because the streamed text is still auto-carried when
    // FinalMessage is left null.
    private static async Task SendStreamedWithAddAttachmentAsync(ITurnContext ctx, ILogger log, CancellationToken ct)
    {
        var stream = ctx.StreamingResponse;

        try
        {
            log.LogInformation("[CiteLab] variant=stream17 isStreamingChannel={IsStreaming}", stream.IsStreamingChannel);

            await stream.QueueInformativeUpdateAsync("Looking up active employees...", ct);

            stream.QueueTextChunk("**Active employees**\n\n");
            await Task.Delay(400, ct);
            stream.QueueTextChunk("- Ada Lovelace - Engineering\n");
            await Task.Delay(400, ct);
            stream.QueueTextChunk("- Grace Hopper - Engineering\n");
            await Task.Delay(400, ct);
            stream.QueueTextChunk("- Katherine Johnson - Finance\n\n");
            stream.QueueTextChunk("Source: employee directory [1]");

            stream.AddCitation(BuildClientCitation());
            stream.AddAttachment(BuildEmployeeCard(includeCitationMarker: false));
        }
        finally
        {
            var result = await stream.EndStreamAsync(ct);
            log.LogInformation("[CiteLab] variant=stream17 endStreamResult={Result} updatesSent={Updates}",
                result, stream.UpdatesSent());
        }
    }

    // ---------------------------------------------------------------------
    // Citation construction - the SUPPORTED developer API.
    //
    // Note for the customer: do NOT hand-set a "botCitations" property. That name
    // appears in captured channel traffic because it is the channel's internal
    // representation. The developer-facing API is the AIEntity below (or the
    // StreamingResponse.AddCitation helper used in variant 4).
    // ---------------------------------------------------------------------
    private static ClientCitation BuildClientCitation() => new()
    {
        Position = 1,
        Appearance = new ClientCitationAppearance
        {
            Name = SourceName,
            Url = SourceUrl,
            Abstract = SourceAbstract,
            Keywords = ["employees", "directory", "roster"]
        }
    };

    private static void AttachCitation(IActivity activity)
    {
        var entity = new AIEntity
        {
            AdditionalType = [AIEntity.AdditionalTypeAIGeneratedContent],
            Citation = [BuildClientCitation()]
        };

        activity.Entities ??= [];
        activity.Entities.Add(entity);
    }

    // Mirrors the customer's table-in-a-card shape.
    private static Attachment BuildEmployeeCard(bool includeCitationMarker)
    {
        var rows = new JsonArray();
        foreach (var (name, dept) in new[]
                 {
                     ("Ada Lovelace", "Engineering"),
                     ("Grace Hopper", "Engineering"),
                     ("Katherine Johnson", "Finance")
                 })
        {
            rows.Add(new JsonObject
            {
                ["type"] = "ColumnSet",
                ["columns"] = new JsonArray
                {
                    TextColumn(name),
                    TextColumn(dept)
                }
            });
        }

        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "Active employees",
                ["weight"] = "Bolder",
                ["size"] = "Medium"
            }
        };

        foreach (var row in rows.ToArray())
        {
            rows.Remove(row);
            body.Add(row);
        }

        if (includeCitationMarker)
        {
            // The customer's current shape: the citation marker is a TextBlock
            // INSIDE the card, where the citation renderer cannot bind to it.
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "Source: employee directory [1]",
                ["wrap"] = true,
                ["isSubtle"] = true
            });
        }

        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body
        };

        return new Attachment
        {
            ContentType = ContentTypes.AdaptiveCard,
            Content = card
        };
    }

    private static JsonObject TextColumn(string text) => new()
    {
        ["type"] = "Column",
        ["width"] = "stretch",
        ["items"] = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true }
        }
    };

    // Logs exactly what we put on the wire, so the outbound shape can be compared
    // against what the client renders.
    private static void LogShape(ILogger log, string variant, IActivity activity)
    {
        log.LogInformation(
            "[CiteLab] variant={Variant} hasText={HasText} textLen={TextLen} attachments={Attachments} entities={Entities}",
            variant,
            !string.IsNullOrEmpty(activity.Text),
            activity.Text?.Length ?? 0,
            activity.Attachments?.Count ?? 0,
            activity.Entities?.Count ?? 0);
    }
}
