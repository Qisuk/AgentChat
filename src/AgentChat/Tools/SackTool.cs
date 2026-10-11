using System.Text.Json.Nodes;
using AgentChat.Chat;

namespace AgentChat.Tools;

/// <summary>
/// Each agent's sack of items (<see cref="SackStore"/>). An agent can look in its own sack or give one of
/// its items to another agent or the user.
/// <para>
/// The agent states its name in the call, but that name is only checked, never trusted: the sack used is
/// always the real caller's (<see cref="ToolContext"/>, set by the room, not the model). A call naming
/// anyone else is refused, so no agent can touch another agent's sack whatever the model writes.
/// </para>
/// <para>
/// Small models misread vague parameter names (an "agent" field got the recipient's name), so the fields
/// say whose name goes where (<c>my_name</c>, <c>give_to</c>), and every refusal shows a corrected call.
/// </para>
/// </summary>
public sealed class SackTool(SackStore sacks, AgentStore agents) : ITool
{
    public const string MyName = "my_name";
    public const string GiveTo = "give_to";

    public string Name => "sack";
    public string Description =>
        $"Your own sack of items. Everyone has one, starting with {SackStore.StartingItems} random items from: {string.Join(", ", SackStore.Items)}. "
        + "You can only use your own sack, so always put your own name in my_name. "
        + "To see your items: {\"action\": \"look\", \"my_name\": \"<your name>\"}. "
        + "To give one of your items to someone: {\"action\": \"give\", \"my_name\": \"<your name>\", \"item\": \"<item>\", \"give_to\": \"<their name>\"}.";
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
                ["description"] = "\"look\" to list the items in your sack, or \"give\" to hand one of your items to someone else.",
            },
            [MyName] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Your own name, the caller's, e.g. \"Ada\" if you are Ada. Never the name of the person you are giving to: that goes in give_to.",
            },
            ["item"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(SackStore.Items.Select(i => (JsonNode?)i).ToArray()),
                ["description"] = "Needed for \"give\": the item from your sack to hand over.",
            },
            [GiveTo] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = $"Needed for \"give\": the name of who receives the item, another agent or \"{PromptBuilder.HumanName}\".",
            },
        },
        ["required"] = new JsonArray("action", MyName),
    };

    public Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        var action = Text(arguments, "action")?.ToLowerInvariant();
        var claimed = Text(arguments, MyName);
        var giveTo = Text(arguments, GiveTo) ?? Text(arguments, "to");

        if (claimed is null)
        {
            return Refuse($"my_name is missing. Put your own name, {context.CallerName}, in my_name. Try: {Example(context, action, arguments, giveTo)}");
        }
        // The identity check: the sack is the caller's, never the one named in the call.
        if (!SameName(claimed, context.CallerName))
        {
            // Most often the model put the recipient here; say where that name belongs.
            if (action == "give" && giveTo is null && FindRecipient(claimed) is { } meant && meant.SackId != context.CallerAgentId)
            {
                return Refuse($"my_name must be your own name ({context.CallerName}), not {meant.Name}. "
                    + $"To give an item to {meant.Name}, put {meant.Name} in give_to. Try: {Example(context, action, arguments, meant.Name)}");
            }
            return Refuse($"You are {context.CallerName}, not {claimed}. You can only use your own sack, so my_name must be \"{context.CallerName}\".");
        }

        return action switch
        {
            "look" => Done($"{context.CallerName}'s sack: {Describe(sacks.Open(context.CallerAgentId))}."),
            "give" => Give(arguments, context, giveTo),
            _ => Refuse($"action must be \"look\" or \"give\". Try: {Example(context, "look", arguments, null)}"),
        };
    }

    private Task<ToolResult> Give(JsonObject arguments, ToolContext context, string? giveTo)
    {
        var item = Text(arguments, "item")?.ToLowerInvariant();
        if (item is null || !SackStore.Items.Contains(item))
        {
            return Refuse($"Say which item to give in item. Your items: {Describe(sacks.Open(context.CallerAgentId))}. Try: {Example(context, "give", arguments, giveTo)}");
        }

        if (giveTo is null)
        {
            return Refuse($"Say who gets the {item} in give_to. You can give to: {Others(context)}. Try: {Example(context, "give", arguments, null)}");
        }
        if (FindRecipient(giveTo) is not { } recipient)
        {
            return Refuse($"There is no one called '{giveTo}' to give to. You can give to: {Others(context)}.");
        }
        if (recipient.SackId == context.CallerAgentId)
        {
            return Refuse($"You can't give an item to yourself. give_to is the other person: one of {Others(context)}.");
        }

        // Transfer fills the recipient's sack first if it hasn't been used yet, then adds the item.
        if (!sacks.Transfer(context.CallerAgentId, recipient.SackId, item))
        {
            var mine = sacks.Open(context.CallerAgentId);
            return mine.Count == 0
                ? Refuse($"There is no {item} in your sack: it is empty, so you have nothing to give.")
                : Refuse($"There is no {item} in your sack. Your sack: {Describe(mine)}. Try: {Example(context, "give", arguments, giveTo)}");
        }
        return Done($"{context.CallerName} gave the {item} to {recipient.Name}. {context.CallerName}'s sack: {Describe(sacks.Open(context.CallerAgentId))}.");
    }

    /// <summary>
    /// A corrected call for the caller to copy, keeping whatever it got right and filling the rest with
    /// real values: its own name, an item it has, and someone it can give to.
    /// </summary>
    private string Example(ToolContext context, string? action, JsonObject arguments, string? giveTo)
    {
        var example = new JsonObject { ["action"] = action == "give" ? "give" : "look", [MyName] = context.CallerName };
        if (action == "give")
        {
            var mine = sacks.Open(context.CallerAgentId);
            var item = Text(arguments, "item")?.ToLowerInvariant();
            example["item"] = item is not null && mine.Contains(item) ? item : mine.FirstOrDefault() ?? SackStore.Items[0];
            example[GiveTo] = FindRecipient(giveTo ?? "") is { } r && r.SackId != context.CallerAgentId
                ? r.Name
                : OtherRecipients(context).FirstOrDefault().Name ?? PromptBuilder.HumanName;
        }
        return example.ToJsonString();
    }

    // The user and every configured agent, including muted or disabled ones: they're still in the room.
    private IEnumerable<(string SackId, string Name)> Recipients() =>
        agents.Agents.Select(a => (a.Id, a.Name)).Prepend((SackStore.UserSackId, PromptBuilder.HumanName));

    private IEnumerable<(string SackId, string Name)> OtherRecipients(ToolContext context) =>
        Recipients().Where(r => r.SackId != context.CallerAgentId);

    private string Others(ToolContext context) => string.Join(", ", OtherRecipients(context).Select(r => r.Name));

    private (string SackId, string Name)? FindRecipient(string name) =>
        Recipients().Where(r => SameName(r.Name, name)).Select(r => ((string, string)?)r).FirstOrDefault();

    private static string Describe(IReadOnlyList<string> items) => items.Count == 0 ? "empty" : string.Join(", ", items);

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim().TrimStart('@'), b.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static Task<ToolResult> Done(string text) => Task.FromResult(new ToolResult(text));
    private static Task<ToolResult> Refuse(string text) => Task.FromResult(new ToolResult(text, IsError: true));
}
