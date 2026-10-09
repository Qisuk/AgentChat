using System.Text.Json.Nodes;

namespace AgentChat.Tools;

public sealed record ToolResult(string Content, bool IsError = false);

/// <summary>Who is calling a tool. Lets tools act on the chat on the caller's behalf (e.g. muting another agent).</summary>
public sealed record ToolContext(string CallerAgentId, string CallerName);

/// <summary>Something an agent can call during the "act" step of its loop.</summary>
public interface ITool
{
    /// <summary>Unique name shown to models. Must match ^[a-zA-Z0-9_-]+$ (OpenAI function-name rules).</summary>
    string Name { get; }
    string Description { get; }
    /// <summary>Where the tool comes from, e.g. "built-in" or "mcp:filesystem". Shown in the UI.</summary>
    string Source { get; }
    /// <summary>JSON schema of the tool's arguments.</summary>
    JsonObject ParametersSchema { get; }
    Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>A group of tools discovered together: the built-in tools, or every tool one MCP server exposes.</summary>
public interface IToolSource
{
    string Name { get; }
    Task<IReadOnlyList<ITool>> LoadToolsAsync(CancellationToken cancellationToken = default);
}
