using System.Text.RegularExpressions;
using AgentChat.Llm;
using AgentChat.Models;
using AgentChat.Tools;

namespace AgentChat.Chat;

/// <summary>The steps of one agent loop, shown live in the UI.</summary>
public enum LoopPhase { Read, Decide, Comment, Act, Report }

/// <summary>
/// Runs one loop for one agent: <b>read</b> the chat, <b>decide</b> (ask the model whether and what to say),
/// <b>comment</b> (post its reply), <b>act</b> (run any tool calls it made) and <b>report</b> (post what it
/// concluded from the tool results). One loop uses one unit of the room's throttle.
/// </summary>
public sealed partial class AgentLoop(LlmProviderRegistry providers, ToolRegistry tools, ILogger<AgentLoop>? logger = null)
{
    /// <returns>True if the agent posted something; false if it passed.</returns>
    public async Task<bool> RunAsync(
        AgentConfig agent,
        IReadOnlyList<AgentConfig> roster,
        IReadOnlyList<ChatMessage> chat,
        int loopNumber,
        Action<LoopPhase> setPhase,
        Action<ChatMessage> post,
        CancellationToken cancellationToken)
    {
        setPhase(LoopPhase.Read);
        var allowedTools = agent.Tools.Select(tools.Find).OfType<ITool>().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var toolDefinitions = allowedTools.Values.Select(t => new LlmToolDefinition(t.Name, t.Description, t.ParametersSchema)).ToList();
        var messages = PromptBuilder.Build(agent, roster, chat, toolDefinitions.Count > 0);
        var provider = providers.Get(agent.ProviderId);

        setPhase(LoopPhase.Decide);
        LlmResponse response;
        try
        {
            response = await CompleteAsync(provider, agent, messages, toolDefinitions, cancellationToken);
        }
        catch (LlmException ex) when (toolDefinitions.Count > 0 && ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. gemma3 on Ollama: say so once per loop, then answer without tools rather than failing.
            post(new ChatMessage
            {
                Kind = AuthorKind.System,
                Author = "System",
                Content = $"{agent.Name}'s model ({agent.Model}) doesn't support tools, so it is replying without them.",
            });
            toolDefinitions = [];
            response = await CompleteAsync(provider, agent, messages, toolDefinitions, cancellationToken);
        }
        logger?.LogInformation("{Agent} ({Model}) raw reply: {Reply} [tool calls: {ToolCalls}]",
            agent.Name, agent.Model, response.Content, string.Join(", ", response.ToolCalls.Select(c => c.Name)));
        var reply = CleanReply(agent, response.Content);
        if (response.ToolCalls.Count == 0 && IsPass(response.Content, reply))
        {
            return false;
        }

        if (reply.Any(char.IsLetterOrDigit))
        {
            setPhase(LoopPhase.Comment);
            post(AgentMessage(agent, reply, loopNumber));
        }

        var toolCallsLeft = agent.MaxToolCallsPerLoop;
        var rounds = 0;
        // The round cap guards against a model that keeps emitting tool calls even when none are offered.
        while (response.ToolCalls.Count > 0 && rounds++ <= agent.MaxToolCallsPerLoop)
        {
            setPhase(LoopPhase.Act);
            messages.Add(new LlmMessage(LlmRole.Assistant, response.Content) { ToolCalls = response.ToolCalls });
            foreach (var call in response.ToolCalls)
            {
                var result = toolCallsLeft-- > 0
                    ? await InvokeAsync(allowedTools, call, new ToolContext(agent.Id, agent.Name), cancellationToken)
                    : new ToolResult("Tool call limit for this loop reached; report what you have.", IsError: true);
                messages.Add(new LlmMessage(LlmRole.Tool, result.Content) { ToolCallId = call.Id });
                post(new ChatMessage
                {
                    Kind = AuthorKind.Tool,
                    Author = call.Name,
                    AgentId = agent.Id,
                    Content = $"{agent.Name} called {call.Name}({call.Arguments.ToJsonString()})\n{(result.IsError ? "Error: " : "")}{result.Content}",
                    Loop = loopNumber,
                });
            }

            setPhase(LoopPhase.Report);
            // Once the limit is used up, stop offering tools so the model has to answer in words.
            response = await CompleteAsync(provider, agent, messages, toolCallsLeft > 0 ? toolDefinitions : [], cancellationToken);
            var report = CleanReply(agent, response.Content);
            if (!IsPass(response.Content, report))
            {
                post(AgentMessage(agent, report, loopNumber));
            }
        }

        return true;
    }

    private static Task<LlmResponse> CompleteAsync(ILlmProvider provider, AgentConfig agent, List<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> toolDefinitions, CancellationToken cancellationToken) =>
        provider.CompleteAsync(new LlmRequest
        {
            Model = agent.Model,
            Messages = messages,
            Tools = toolDefinitions,
            Temperature = agent.Temperature,
            MaxTokens = agent.MaxResponseTokens,
        }, cancellationToken);

    private static async Task<ToolResult> InvokeAsync(Dictionary<string, ITool> allowedTools, LlmToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        // Only tools enabled for this agent can run, whatever the model asks for.
        if (!allowedTools.TryGetValue(call.Name, out var tool))
        {
            return new ToolResult($"Tool '{call.Name}' is not available to you.", IsError: true);
        }
        try
        {
            return await tool.InvokeAsync(call.Arguments, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
    }

    // Small models stick the token on either end of a real answer ("[PASS] I've been observing Ada…"),
    // so what counts is what's left without it: nothing, or a few words after a leading token
    // ("[PASS] Nothing to add.").
    private static bool IsPass(string raw, string cleaned) =>
        !cleaned.Any(char.IsLetterOrDigit)
        || (raw.TrimStart().StartsWith(PromptBuilder.PassToken, StringComparison.OrdinalIgnoreCase)
            && cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 6);

    /// <summary>
    /// Small models imitate the "[Name]: text" format the chat history is shown in: they label their reply
    /// ("[You]: ."), or carry on writing other participants' turns. Keep only the agent's own words.
    /// </summary>
    internal static string CleanReply(AgentConfig agent, string raw)
    {
        var text = raw.Replace(PromptBuilder.PassToken, "", StringComparison.OrdinalIgnoreCase);

        // Anything from the first invented turn on is the model talking for someone else.
        var invented = InventedTurn().Match(text);
        if (invented.Success)
        {
            text = text[..invented.Index];
        }

        // Drop leading labels, bracketed ("[You]:", "[Room]:") or the agent's own name ("Ada:").
        string previous;
        do
        {
            previous = text;
            text = LeadingLabel().Replace(text.TrimStart(), "");
            // Its own label also turns up without the colon: "[Cuthbert (you)] Let's start here."
            foreach (var label in new[] { $"[{PromptBuilder.SelfLabel(agent)}]", $"[{agent.Name}]", $"{PromptBuilder.SelfLabel(agent)}:", $"{agent.Name}:" })
            {
                if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[label.Length..];
                }
            }
        }
        while (text != previous);

        return text.Trim();
    }

    [GeneratedRegex(@"^\[[^\]\n]{1,40}\]:\s*")]
    private static partial Regex LeadingLabel();

    [GeneratedRegex(@"\n\s*\[[^\]\n]{1,40}\]:")]
    private static partial Regex InventedTurn();

    private static ChatMessage AgentMessage(AgentConfig agent, string content, int loop) => new()
    {
        Kind = AuthorKind.Agent,
        Author = agent.Name,
        AgentId = agent.Id,
        Content = content,
        Loop = loop,
    };
}
