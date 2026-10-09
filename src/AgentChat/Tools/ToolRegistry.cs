namespace AgentChat.Tools;

/// <summary>
/// Every tool available to agents, gathered from the built-in source and each configured MCP server.
/// Agents only see the tools named in their own <see cref="Models.AgentConfig.Tools"/> list.
/// </summary>
public sealed class ToolRegistry(IEnumerable<IToolSource> sources, ILogger<ToolRegistry> logger) : IAsyncDisposable
{
    private readonly List<IToolSource> _sources = sources.ToList();
    private Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceErrors = new();

    /// <summary>Raised after the tools are (re)loaded so the UI can refresh its tool lists.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<ITool> All => _tools.Values;
    /// <summary>Tool sources that failed to load (e.g. an MCP server that would not start), with the reason.</summary>
    public IReadOnlyDictionary<string, string> SourceErrors => _sourceErrors;

    public ITool? Find(string name) => _tools.GetValueOrDefault(name);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var tools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        _sourceErrors.Clear();
        foreach (var source in _sources)
        {
            try
            {
                foreach (var tool in await source.LoadToolsAsync(cancellationToken))
                {
                    tools[tool.Name] = tool;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tool source {Source} failed to load", source.Name);
                _sourceErrors[source.Name] = ex.Message;
            }
        }
        _tools = tools;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.OfType<IAsyncDisposable>())
        {
            await source.DisposeAsync();
        }
    }
}

/// <summary>Loads tools at startup in the background so a slow MCP server doesn't delay the UI.</summary>
public sealed class ToolLoader(ToolRegistry registry) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => registry.LoadAsync(stoppingToken);
}
