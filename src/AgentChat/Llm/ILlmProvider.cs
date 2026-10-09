namespace AgentChat.Llm;

/// <summary>
/// A source of chat completions. Each agent picks a provider by id, so local models (Ollama in Docker)
/// and online models (OpenAI, OpenRouter, Anthropic, ...) can be mixed and swapped per agent.
/// Add a new kind of provider by implementing this interface and handling its "Kind" in <see cref="LlmProviderRegistry"/>.
/// </summary>
public interface ILlmProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
