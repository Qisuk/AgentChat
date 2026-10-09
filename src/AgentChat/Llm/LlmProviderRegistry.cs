namespace AgentChat.Llm;

/// <summary>Builds one <see cref="ILlmProvider"/> per entry in the "Providers" config section.</summary>
public sealed class LlmProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    /// <summary>Creates a provider for each configured entry.</summary>
    public static LlmProviderRegistry FromOptions(IEnumerable<ProviderOptions> options, IHttpClientFactory httpClientFactory) =>
        new(options.Select(o => Create(o, httpClientFactory)));

    public LlmProviderRegistry(IEnumerable<ILlmProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ILlmProvider> All => _providers.Values;

    public ILlmProvider Get(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new LlmException($"No provider with id '{id}'. Configured: {string.Join(", ", _providers.Keys)}.");

    private static ILlmProvider Create(ProviderOptions options, IHttpClientFactory httpClientFactory) =>
        options.Kind.ToLowerInvariant() switch
        {
            "openaicompatible" or "openai" or "ollama" => new OpenAiCompatibleProvider(options, httpClientFactory.CreateClient($"llm:{options.Id}")),
            "random" => new RandomWordsProvider(options),
            _ => throw new InvalidOperationException($"Provider '{options.Id}' has unknown Kind '{options.Kind}'."),
        };
}
