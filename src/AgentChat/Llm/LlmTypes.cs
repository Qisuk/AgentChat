using System.Text.Json.Nodes;

namespace AgentChat.Llm;

public enum LlmRole { System, User, Assistant, Tool }

/// <summary>A provider-neutral chat message sent to a model.</summary>
public sealed record LlmMessage(LlmRole Role, string Content)
{
    /// <summary>Tool calls the assistant made in this message (assistant messages only).</summary>
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    /// <summary>The call this message answers (tool messages only).</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>A tool the model may call, described with a JSON schema for its arguments.</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonObject ParametersSchema);

public sealed record LlmToolCall(string Id, string Name, JsonObject Arguments);

public sealed record LlmRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 400;
}

public sealed record LlmResponse(string Content, IReadOnlyList<LlmToolCall> ToolCalls);

public sealed class LlmException(string message) : Exception(message);
