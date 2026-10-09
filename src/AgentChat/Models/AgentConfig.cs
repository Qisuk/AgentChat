namespace AgentChat.Models;

/// <summary>Everything that can be tweaked for one agent from the UI. Saved to the agents file.</summary>
public sealed class AgentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Agent";
    /// <summary>CSS colour used for the agent's name and chat bubble accent.</summary>
    public string Color { get; set; } = "#4f7cff";
    /// <summary>Disabled agents stay configured but never take a turn.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Id of an entry in the "Providers" config section (a local Ollama container, OpenAI, ...).</summary>
    public string ProviderId { get; set; } = "ollama";
    public string Model { get; set; } = "llama3.2:3b";
    public double Temperature { get; set; } = 0.7;
    /// <summary>Upper bound on the length of each reply.</summary>
    public int MaxResponseTokens { get; set; } = 400;

    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    /// <summary>
    /// Long-term memory: free-form notes always included in the agent's context, independent of how much
    /// chat history fits. Editable in the UI.
    /// </summary>
    public string Memory { get; set; } = "";

    /// <summary>Short-term memory: at most this many of the most recent chat messages are shown to the agent.</summary>
    public int ContextMessages { get; set; } = 30;
    /// <summary>Approximate token budget for the whole prompt (system prompt + memory + history); oldest history is dropped first.</summary>
    public int ContextTokens { get; set; } = 4096;

    /// <summary>Names of the tools (built-in or MCP, see <see cref="AgentChat.Tools.ToolRegistry"/>) this agent may call. Empty = no tools.</summary>
    public List<string> Tools { get; set; } = new();
    /// <summary>Most tool calls the agent can make within a single loop before it must report.</summary>
    public int MaxToolCallsPerLoop { get; set; } = 3;

    public AgentConfig Clone()
    {
        var copy = (AgentConfig)MemberwiseClone();
        copy.Tools = new List<string>(Tools);
        return copy;
    }
}
