using System.Text.Json.Nodes;
using AgentChat.Chat;

namespace AgentChat.Tools;

/// <summary>
/// Each agent's sack of items (<see cref="SackStore"/>). An agent can look in its own sack or give one of
/// its items to another agent.
/// <para>
/// The agent states its name in the call, but that name is only checked, never trusted: the sack used is
/// always the real caller's (<see cref="ToolContext"/>, set by the room, not the model). A call naming
/// anyone else is refused, so no agent can touch another agent's sack whatever the model writes.
/// </para>
/// </summary>
public sealed class SackTool(SackStore sacks, AgentStore agents) : ITool
{
    public string Name => "sack";
    public string Description =>
        $"Your sack of items. Every agent has one, starting with {SackStore.StartingItems} random items from: {string.Join(", ", SackStore.Items)}. "
        + "action \"look\" shows what is in your sack; action \"give\" moves one of your items into another agent's sack. "
        + "Always give your own name in \"agent\": you can only use your own sack.";
    public string Source => "chat";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("look", "give"),
                ["description"] = "look: list your items. give: hand one item to another agent.",
            },
            ["agent"] = new JsonObject { ["type"] = "string", ["description"] = "Your own name." },
            ["item"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(SackStore.Items.Select(i => (JsonNode?)i).ToArray()),
                ["description"] = "For give: the item to hand over.",
            },
            ["to"] = new JsonObject { ["type"] = "string", ["description"] = "For give: the name of the agent receiving the item." },
        },
        ["required"] = new JsonArray("action", "agent"),
    };

    public Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        var claimed = Text(arguments, "agent");
        if (claimed is null)
        {
            return Refuse($"Give your own name in \"agent\" (you are {context.CallerName}).");
        }
        // The identity check: the sack is the caller's, never the one named in the call.
        if (!SameName(claimed, context.CallerName))
        {
            return Refuse($"You are {context.CallerName}, not {claimed}. You can only use your own sack.");
        }

        return Text(arguments, "action")?.ToLowerInvariant() switch
        {
            "look" => Done($"{context.CallerName}'s sack: {Describe(sacks.Open(context.CallerAgentId))}."),
            "give" => Give(arguments, context),
            var other => Refuse($"Unknown action '{other}'. Use \"look\" or \"give\"."),
        };
    }

    private Task<ToolResult> Give(JsonObject arguments, ToolContext context)
    {
        var item = Text(arguments, "item")?.ToLowerInvariant();
        if (item is null || !SackStore.Items.Contains(item))
        {
            return Refuse($"Say which item to give: one of {string.Join(", ", SackStore.Items)}.");
        }

        var toName = Text(arguments, "to");
        // Any configured agent can receive, including muted or disabled ones: they're still in the room.
        var recipient = toName is null ? null : agents.Agents.FirstOrDefault(a => SameName(a.Name, toName));
        if (recipient is null)
        {
            return Refuse($"There is no agent called '{toName}'. Agents: {string.Join(", ", agents.Agents.Select(a => a.Name))}.");
        }
        if (recipient.Id == context.CallerAgentId)
        {
            return Refuse("You can't give an item to yourself.");
        }

        if (!sacks.Transfer(context.CallerAgentId, recipient.Id, item))
        {
            return Refuse($"There is no {item} in your sack. Your sack: {Describe(sacks.Open(context.CallerAgentId))}.");
        }
        return Done($"{context.CallerName} gave the {item} to {recipient.Name}. {context.CallerName}'s sack: {Describe(sacks.Open(context.CallerAgentId))}.");
    }

    private static string Describe(IReadOnlyList<string> items) => items.Count == 0 ? "empty" : string.Join(", ", items);

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim().TrimStart('@'), b.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static Task<ToolResult> Done(string text) => Task.FromResult(new ToolResult(text));
    private static Task<ToolResult> Refuse(string text) => Task.FromResult(new ToolResult(text, IsError: true));
}
