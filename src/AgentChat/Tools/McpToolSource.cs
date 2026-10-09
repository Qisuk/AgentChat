using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentChat.Tools;

/// <summary>One entry of the "McpServers" config section.</summary>
public sealed class McpServerOptions
{
    public const string SectionName = "McpServers";

    /// <summary>Short name; tools are exposed to agents as "{Name}__{tool}".</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>"stdio" (launch a local process) or "http" (connect to a running server).</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Arguments { get; set; } = new();
    public Dictionary<string, string?> Environment { get; set; } = new();
    public string? Url { get; set; }
}

/// <summary>Connects to an MCP server and exposes its tools as <see cref="ITool"/>s.</summary>
public sealed class McpToolSource(McpServerOptions options, ILoggerFactory loggerFactory) : IToolSource, IAsyncDisposable
{
    private McpClient? _client;

    public string Name => options.Name;

    public async Task<IReadOnlyList<ITool>> LoadToolsAsync(CancellationToken cancellationToken = default)
    {
        IClientTransport transport = options.Transport.ToLowerInvariant() switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = options.Name,
                Command = options.Command ?? throw new InvalidOperationException($"MCP server '{options.Name}' needs a Command."),
                Arguments = options.Arguments,
                EnvironmentVariables = options.Environment,
            }, loggerFactory),
            "http" => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = options.Name,
                Endpoint = new Uri(options.Url ?? throw new InvalidOperationException($"MCP server '{options.Name}' needs a Url.")),
            }, loggerFactory),
            _ => throw new InvalidOperationException($"MCP server '{options.Name}' has unknown Transport '{options.Transport}'."),
        };

        _client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => (ITool)new McpTool(t, options.Name)).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    private sealed class McpTool(McpClientTool tool, string server) : ITool
    {
        public string Name { get; } = $"{server}__{tool.Name}";
        public string Description => tool.Description;
        public string Source => $"mcp:{server}";
        public JsonObject ParametersSchema { get; } =
            JsonNode.Parse(tool.JsonSchema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };

        public async Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToDictionary(p => p.Key, p => (object?)(p.Value is null ? null : JsonSerializer.SerializeToElement(p.Value)));
            var result = await tool.CallAsync(args, cancellationToken: cancellationToken);
            var text = string.Join("\n", result.Content.Select(c => c is TextContentBlock t ? t.Text : $"[{c.Type} content]"));
            return new ToolResult(text, result.IsError == true);
        }
    }
}
