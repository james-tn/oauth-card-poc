// Minimal agent that exposes the two labs. No authentication, no storage, no
// business logic - the labs are deliberately isolated so the behaviour they
// demonstrate cannot be blamed on anything else in the app.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace CopilotLabs;

public class LabsAgent : AgentApplication
{
    private readonly ILogger<LabsAgent> _log;

    public LabsAgent(AgentApplicationOptions options, ILogger<LabsAgent> log) : base(options)
    {
        _log = log;

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);

        // Trace every outbound activity's ReplyToId. See OutboundTrace.cs.
        OnBeforeTurn((ctx, state, ct) =>
        {
            OutboundTrace.Attach(ctx, _log);
            return Task.FromResult(true);
        });

        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    private async Task WelcomeAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        foreach (var member in ctx.Activity.MembersAdded ?? [])
        {
            if (member.Id != ctx.Activity.Recipient?.Id)
            {
                await ctx.SendActivityAsync(MessageFactory.Text(Help), ct);
            }
        }
    }

    private Task OnMessageAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var text = (ctx.Activity.Text ?? "").Trim();

        _log.LogInformation(
            "[labs] inbound text={Text} channel={Channel} subChannel={SubChannel} conv={Conv}",
            text,
            ctx.Activity.ChannelId?.Channel ?? "-",
            ctx.Activity.ChannelId?.SubChannel ?? "(none)",
            ctx.Activity.Conversation?.Id ?? "-");

        if (text.StartsWith("/cite", StringComparison.OrdinalIgnoreCase))
        {
            return CitationLab.HandleAsync(ctx, _log, Arg(text, 1), ct);
        }

        if (text.StartsWith("/slow", StringComparison.OrdinalIgnoreCase))
        {
            _ = int.TryParse(Arg(text, 2), out var seconds);
            return LongRunningLab.HandleAsync(ctx, _log, Arg(text, 1), seconds, ct);
        }

        if (text.StartsWith("/surface", StringComparison.OrdinalIgnoreCase))
        {
            // The single most useful diagnostic in this sample: which surface am I on?
            // Microsoft 365 Copilot reports SubChannel = "COPILOT"; Teams reports none.
            return ctx.SendActivityAsync(MessageFactory.Text(
                $"channel = `{ctx.Activity.ChannelId?.Channel ?? "(null)"}`\n\n" +
                $"subChannel = `{ctx.Activity.ChannelId?.SubChannel ?? "(none)"}`\n\n" +
                $"streaming supported = `{ctx.StreamingResponse.IsStreamingChannel}`"), ct);
        }

        return ctx.SendActivityAsync(MessageFactory.Text(Help), ct);
    }

    private static string Arg(string text, int index)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : "";
    }

    private static string Help =>
        "### Copilot citation & timeout labs\n\n" +
        $"{CitationLab.Help}\n\n" +
        $"{LongRunningLab.Help}\n\n" +
        "- `/surface` - report the current channel, sub-channel, and streaming support";
}
