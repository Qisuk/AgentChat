using System.Text;
using AgentChat.Llm;
using AgentChat.Models;

namespace AgentChat.Chat;

/// <summary>
/// The "read" step: turns the shared chat into the prompt one agent sees, applying that agent's
/// system prompt, long-term memory and short-term memory limits (message count and token budget).
/// <para>
/// The chat is given as one labelled transcript in a single user message, rather than spread over
/// user/assistant turns. Small chat models assume every user turn comes from one human, which makes them
/// mix up speakers; with a transcript every line, the agent's own included, says plainly who wrote it.
/// </para>
/// </summary>
public static class PromptBuilder
{
    /// <summary>What an agent replies when it decides it has nothing to add this loop.</summary>
    public const string PassToken = "[PASS]";

    /// <summary>
    /// What the human is called in prompts. The UI shows "You", but in a prompt that word would also
    /// mean the agent reading it.
    /// </summary>
    public const string HumanName = "User";

    public static List<LlmMessage> Build(AgentConfig agent, IReadOnlyList<AgentConfig> roster, IReadOnlyList<ChatMessage> chat, bool hasTools)
    {
        var lines = chat
            .Where(m => m.Kind != AuthorKind.System)
            .TakeLast(Math.Max(0, agent.ContextMessages))
            .Select(m => TranscriptLine(agent, m))
            .ToList();

        var latest = chat.LastOrDefault(m => m.Kind is AuthorKind.User or AuthorKind.Agent);
        var addressed = latest is not null && latest.AgentId != agent.Id
            && latest.Content.Contains($"@{agent.Name}", StringComparison.OrdinalIgnoreCase);
        var instruction = Instruction(agent, lines.Count > 0, addressed ? (latest!.Kind == AuthorKind.User ? HumanName : latest.Author) : null);
        var system = BuildSystemPrompt(agent, roster, chat, hasTools);

        // Leave room for the reply; drop the oldest lines until the estimate fits.
        var budget = agent.ContextTokens - agent.MaxResponseTokens - EstimateTokens(system) - EstimateTokens(instruction) - 20;
        var used = lines.Sum(EstimateTokens);
        while (lines.Count > 0 && used > budget)
        {
            used -= EstimateTokens(lines[0]);
            lines.RemoveAt(0);
        }

        var transcript = lines.Count > 0
            ? $"Chat transcript so far (oldest first):\n\n{string.Join("\n", lines)}\n\n{instruction}"
            : instruction;

        return [new LlmMessage(LlmRole.System, system), new LlmMessage(LlmRole.User, transcript)];
    }

    /// <summary>Rough token estimate (~4 characters per token) that works across models without a tokenizer.</summary>
    public static int EstimateTokens(string text) => text.Length / 4 + 4;

    /// <summary>The label for an agent's own lines: its name plus "(you)", so it can't miss them.</summary>
    public static string SelfLabel(AgentConfig agent) => $"{agent.Name} (you)";

    // The pass option is deliberately not repeated here: small models lean on the last thing they read,
    // and ending on "[PASS]" made them pass even when asked a direct question.
    private static string Instruction(AgentConfig agent, bool hasHistory, string? addressedBy) =>
        (addressedBy is not null ? $"{addressedBy} has just addressed {agent.Name} directly, so answer them. " : "")
        + (hasHistory ? $"Write {agent.Name}'s next message in this chat." : $"The chat is empty. Write {agent.Name}'s opening message.")
        + $" Speak only as {agent.Name}: don't write lines for anyone else and don't start with a name label.";

    private static string BuildSystemPrompt(AgentConfig agent, IReadOnlyList<AgentConfig> roster, IReadOnlyList<ChatMessage> chat, bool hasTools)
    {
        var active = roster.Where(a => a.Enabled && a.Id != agent.Id).Select(a => a.Name).ToList();
        // Agents that spoke earlier but can't now (muted, disabled or removed) still appear in the transcript.
        var inactive = chat
            .Where(m => m.Kind == AuthorKind.Agent && m.AgentId != agent.Id)
            .Select(m => m.Author)
            .Distinct()
            .Where(name => !active.Contains(name) && name != agent.Name)
            .ToList();

        var sb = new StringBuilder(agent.SystemPrompt.Trim());
        sb.AppendLine().AppendLine();
        sb.AppendLine("## Group chat");
        sb.AppendLine($"You are {agent.Name}, taking part in a group chat. Everyone sees every message.");
        sb.AppendLine("Participants:");
        sb.AppendLine($"- {agent.Name}: you. Your own earlier messages are labelled \"[{SelfLabel(agent)}]\". When others mention {agent.Name}, they mean you.");
        sb.AppendLine($"- {HumanName}: the human running the chat.");
        foreach (var name in active)
        {
            sb.AppendLine($"- {name}: another AI agent.");
        }
        foreach (var name in inactive)
        {
            sb.AppendLine($"- {name}: an AI agent that can no longer speak (muted or removed).");
        }
        sb.AppendLine("Nobody else is in the chat. Each transcript line starts with the speaker's name in brackets.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine($"- Write only {agent.Name}'s next message, without a name label.");
        sb.AppendLine("- Address someone with @Name to hand them the next turn. Respond to the other agents, not just the user: agree, challenge or build on what they said.");
        sb.AppendLine("- Keep replies short (a few sentences).");
        sb.AppendLine($"- If you were asked something, answer it. Only if you have nothing new to add, reply with exactly {PassToken} to skip your turn.");
        if (hasTools)
        {
            sb.AppendLine("- You can call tools when they help. Tool results are visible to everyone in the chat.");
        }
        if (!string.IsNullOrWhiteSpace(agent.Memory))
        {
            sb.AppendLine().AppendLine("## Your memory").AppendLine(agent.Memory.Trim());
        }
        return sb.ToString();
    }

    private static string TranscriptLine(AgentConfig agent, ChatMessage message)
    {
        var label = message switch
        {
            { Kind: AuthorKind.User } => HumanName,
            { Kind: AuthorKind.Agent } when message.AgentId == agent.Id => SelfLabel(agent),
            { Kind: AuthorKind.Tool } => $"tool {message.Author}, called by {(message.AgentId == agent.Id ? SelfLabel(agent) : CallerName(message))}",
            _ => message.Author,
        };
        // Indent continuation lines so a line inside a message can never look like a new speaker.
        return $"[{label}]: {message.Content.Trim().Replace("\n", "\n    ")}";
    }

    // Tool messages read "<caller> called <tool>(...)"; the caller's name is the first word.
    private static string CallerName(ChatMessage message) =>
        message.Content.Split(' ', 2)[0] is { Length: > 0 } name ? name : "an agent";
}
