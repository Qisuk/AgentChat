using AgentChat.Chat;
using AgentChat.Components;
using AgentChat.Llm;
using AgentChat.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Model providers: one per entry in "Providers" (local Ollama containers, online APIs, ...).
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => LlmProviderRegistry.FromOptions(
    builder.Configuration.GetSection(ProviderOptions.SectionName).Get<List<ProviderOptions>>() ?? [],
    sp.GetRequiredService<IHttpClientFactory>()));

// State for the current chat session only (muted agents); reset when the chat is cleared.
builder.Services.AddSingleton<ChatSession>();

// Tools: built-in ones, chat tools (mute_agent), and every tool of every enabled MCP server in "McpServers".
builder.Services.AddSingleton<IToolSource, BuiltInToolSource>();
builder.Services.AddSingleton<IToolSource, ChatToolSource>();
foreach (var server in builder.Configuration.GetSection(McpServerOptions.SectionName).Get<List<McpServerOptions>>() ?? [])
{
    if (server.Enabled)
    {
        builder.Services.AddSingleton<IToolSource>(sp => new McpToolSource(server, sp.GetRequiredService<ILoggerFactory>()));
    }
}
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddHostedService<ToolLoader>();

// The single shared chat room and the agents' saved settings.
builder.Services.AddSingleton(sp =>
{
    var path = builder.Configuration["AgentsFile"] ?? "App_Data/agents.json";
    return new AgentStore(Path.IsPathRooted(path) ? path : Path.Combine(builder.Environment.ContentRootPath, path));
});
builder.Services.AddSingleton<AgentLoop>();
builder.Services.AddSingleton<ChatRoom>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
