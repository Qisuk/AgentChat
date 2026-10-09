namespace AgentChat.Models;

/// <summary>Who wrote a message in the shared chat.</summary>
public enum AuthorKind
{
    User,
    Agent,
    /// <summary>Notices from the room itself (throttle reached, errors, ...).</summary>
    System,
    /// <summary>The result of a tool an agent called, posted as part of that agent's "act" step.</summary>
    Tool,
}

/// <summary>One message in the shared chat that the user and every agent can read.</summary>
public sealed record ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AuthorKind Kind { get; init; }
    /// <summary>Display name: "You", the agent's name, "System", or the tool name.</summary>
    public required string Author { get; init; }
    /// <summary>The agent that wrote the message (or called the tool); null for the user and the room.</summary>
    public string? AgentId { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    /// <summary>The loop (1-based, counted since the user's last feedback) the message was written in; 0 for user/system.</summary>
    public int Loop { get; init; }
}
