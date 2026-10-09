using System.Text.Json.Nodes;

namespace AgentChat.Tools;

/// <summary>Tools implemented in this app. A starting point; add more <see cref="ITool"/>s here.</summary>
public sealed class BuiltInToolSource : IToolSource
{
    public string Name => "built-in";

    public Task<IReadOnlyList<ITool>> LoadToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ITool>>([new CurrentTimeTool()]);

    private sealed class CurrentTimeTool : ITool
    {
        public string Name => "current_time";
        public string Description => "Returns the current local date and time.";
        public string Source => "built-in";
        public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        public Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")));
    }
}
