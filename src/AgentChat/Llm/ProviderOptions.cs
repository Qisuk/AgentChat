namespace AgentChat.Llm;

/// <summary>One entry of the "Providers" config section.</summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>
    /// "OpenAICompatible" covers Ollama, LM Studio, vLLM, llama.cpp server, OpenAI, OpenRouter, Groq, ...
    /// Other kinds can be added in <see cref="LlmProviderRegistry"/>.
    /// </summary>
    public string Kind { get; set; } = "OpenAICompatible";
    public string BaseUrl { get; set; } = "http://localhost:11435/v1";
    /// <summary>Optional API key. Prefer user secrets or an environment variable (Providers__1__ApiKey) over appsettings.</summary>
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}
