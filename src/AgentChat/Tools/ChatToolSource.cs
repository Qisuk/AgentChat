using System.Text.Json.Nodes;
using AgentChat.Chat;

namespace AgentChat.Tools;

/// <summary>Tools that let agents act on the chat room itself.</summary>
public sealed class ChatToolSource(ChatSession session, AgentStore agents) : IToolSource
{
    public string Name => "chat";

    public Task<IReadOnlyList<ITool>> LoadToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ITool>>([new MuteAgentTool(session, agents), new SackTool(session.Sacks, agents)]);

    /// <summary>Silences another agent for the rest of the chat session. Only the user can unmute it.</summary>
    internal sealed class MuteAgentTool(ChatSession session, AgentStore agents) : ITool
    {
        public string Name => "mute_agent";
        public string Description =>
            "Mute another agent in this chat so it cannot speak again until the user unmutes it or the chat is cleared. "
            + "Use only when an agent is disruptive, off-topic or keeps repeating itself.";
        public string Source => "chat";
        public JsonObject ParametersSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["agent"] = new JsonObject { ["type"] = "string", ["description"] = "Name of the agent to mute." },
                ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Short reason, shown to everyone." },
            },
            ["required"] = new JsonArray("agent"),
        };

        public Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
        {
            var name = (arguments["agent"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s.Trim().TrimStart('@') : "";
            var roster = agents.Agents.Where(a => a.Enabled).ToList();
            var target = roster.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                return Result($"There is no agent called '{name}'. Agents: {string.Join(", ", roster.Select(a => a.Name))}.", error: true);
            }
            if (target.Id == context.CallerAgentId)
            {
                return Result("You can't mute yourself. Reply with [PASS] if you have nothing to add.", error: true);
            }
            if (session.IsMuted(target.Id))
            {
                return Result($"{target.Name} is already muted.");
            }

            session.Mute(target.Id, context.CallerName);
            var reason = (arguments["reason"] as JsonValue)?.TryGetValue<string>(out var r) == true && !string.IsNullOrWhiteSpace(r) ? $" Reason: {r.Trim()}" : "";
            return Result($"{target.Name} is muted for the rest of this chat session.{reason}");
        }

        private static Task<ToolResult> Result(string text, bool error = false) => Task.FromResult(new ToolResult(text, error));
    }
}
