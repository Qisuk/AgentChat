using System.Text.Json.Nodes;
using AgentChat.Chat;
using AgentChat.Llm;
using AgentChat.Models;
using AgentChat.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentChat.Tests;

public sealed class ChatRoomTests : IDisposable
{
    private readonly string _agentsFile = Path.Combine(Path.GetTempPath(), $"agentchat-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_agentsFile);

    [Fact]
    public async Task Agents_stop_when_the_throttle_is_reached()
    {
        var room = CreateRoom(new FakeProvider(_ => "Hello"), "A", "B");
        room.SetThrottle(3);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Equal(3, room.Messages.Count(m => m.Kind == AuthorKind.Agent));
        Assert.Equal(RoomStatus.WaitingForUser, room.Status);
        Assert.Contains("Throttle reached", room.Messages[^1].Content);
    }

    [Fact]
    public async Task Continue_grants_another_batch_of_loops()
    {
        var room = CreateRoom(new FakeProvider(_ => "Hello"), "A", "B");
        room.SetThrottle(2);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();
        room.Continue();
        await room.WaitForIdleAsync();

        Assert.Equal(4, room.Messages.Count(m => m.Kind == AuthorKind.Agent));
    }

    [Fact]
    public async Task Agents_take_turns_and_never_answer_themselves()
    {
        var room = CreateRoom(new FakeProvider(_ => "Hello"), "A", "B", "C");
        room.SetThrottle(5);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        var speakers = room.Messages.Where(m => m.Kind == AuthorKind.Agent).Select(m => m.Author).ToList();
        Assert.Equal(["A", "B", "C", "A", "B"], speakers);
    }

    [Fact]
    public async Task Run_ends_early_when_every_agent_passes()
    {
        var room = CreateRoom(new FakeProvider(_ => PromptBuilder.PassToken), "A", "B");
        room.SetThrottle(10);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.DoesNotContain(room.Messages, m => m.Kind == AuthorKind.Agent);
        Assert.Equal(2, room.LoopsUsed);
        Assert.Contains("Every agent passed", room.Messages[^1].Content);
    }

    [Fact]
    public async Task A_pass_token_after_a_real_reply_is_removed()
    {
        var room = CreateRoom(new FakeProvider(_ => "I like Zeta.\n\n[PASS]"), "A");
        room.SetThrottle(1);

        room.PostUserMessage("name a robot");
        await room.WaitForIdleAsync();

        Assert.Equal("I like Zeta.", room.Messages.Single(m => m.Kind == AuthorKind.Agent).Content);
    }

    [Theory]
    [InlineData("[PASS] I've been observing Ada's processing patterns. It borders on obsessive.", "I've been observing Ada's processing patterns. It borders on obsessive.")] // gemma3:1b
    [InlineData("I'd suggest data anonymization. [PASS]", "I'd suggest data anonymization.")]
    public async Task A_real_answer_with_the_pass_token_attached_is_still_posted(string reply, string expected)
    {
        var room = CreateRoom(new FakeProvider(_ => reply), "A");
        room.SetThrottle(1);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Equal(expected, room.Messages.Single(m => m.Kind == AuthorKind.Agent).Content);
    }

    [Theory]
    [InlineData("[PASS]")]
    [InlineData("[Pass]")]
    [InlineData("[PASS] Nothing to add.")]
    public async Task A_pass_is_not_posted(string reply)
    {
        var room = CreateRoom(new FakeProvider(_ => reply), "A");

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.DoesNotContain(room.Messages, m => m.Kind == AuthorKind.Agent);
    }

    [Theory]
    [InlineData("[You]: .")]          // seen from gemma3:1b
    [InlineData("[You]:")]
    [InlineData("[Room]: [Gem]:  ")]
    public async Task A_reply_that_only_imitates_a_chat_label_is_a_pass(string reply)
    {
        var room = CreateRoom(new FakeProvider(_ => reply), "Gem", "B");
        room.SetThrottle(5);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.DoesNotContain(room.Messages, m => m.Kind == AuthorKind.Agent);
        Assert.Contains("Every agent passed", room.Messages[^1].Content);
    }

    [Theory]
    [InlineData("[Gem]: I like Zeta.", "I like Zeta.")]
    [InlineData("Gem: I like Zeta.", "I like Zeta.")]
    [InlineData("[You]: I like Zeta.", "I like Zeta.")]
    [InlineData("I like Zeta.\n[You]: Me too!\n[B]: Agreed.", "I like Zeta.")]
    [InlineData("@B: what do you think?", "@B: what do you think?")]
    [InlineData("[Gem (you)] Let's start here.", "Let's start here.")]   // seen from gemma3:1b
    [InlineData("[Gem] Let's start here.", "Let's start here.")]
    public async Task Labels_and_invented_turns_are_removed_from_replies(string reply, string expected)
    {
        var room = CreateRoom(new FakeProvider(_ => reply), "Gem");
        room.SetThrottle(1);

        room.PostUserMessage("name a robot");
        await room.WaitForIdleAsync();

        Assert.Equal(expected, room.Messages.Single(m => m.Kind == AuthorKind.Agent).Content);
    }

    [Fact]
    public async Task Mentioned_agent_goes_next()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A", "B", "C");
        room.SetThrottle(1);

        room.PostUserMessage("@C what do you think?");
        await room.WaitForIdleAsync();

        Assert.Equal("C", room.Messages.Single(m => m.Kind == AuthorKind.Agent).Author);
    }

    [Fact]
    public async Task Mentioning_the_agent_that_just_spoke_still_gives_it_the_turn()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A", "B");
        room.SetThrottle(1);
        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        room.PostUserMessage("@A again please");
        await room.WaitForIdleAsync();

        Assert.Equal(["A", "A"], room.Messages.Where(m => m.Kind == AuthorKind.Agent).Select(m => m.Author));
    }

    [Fact]
    public async Task Disabled_agents_never_take_a_turn()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A", "B");
        var b = room.Agents.Agents.Single(a => a.Name == "B");
        b.Enabled = false;
        room.Agents.Upsert(b);
        room.SetThrottle(3);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.All(room.Messages.Where(m => m.Kind == AuthorKind.Agent), m => Assert.Equal("A", m.Author));
    }

    [Fact]
    public async Task Provider_errors_are_reported_in_the_chat()
    {
        var room = CreateRoom(new FakeProvider(new Func<LlmRequest, string>(_ => throw new LlmException("model not found"))), "A");

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.System && m.Content.Contains("model not found"));
        Assert.Contains("Every agent failed", room.Messages[^1].Content);
        Assert.Equal(RoomStatus.WaitingForUser, room.Status);
    }

    [Fact]
    public async Task A_model_timeout_is_reported_as_a_failure_not_a_stop()
    {
        var room = CreateRoom(new FakeProvider(new Func<LlmRequest, string>(_ => throw new TaskCanceledException())), "A");

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Contains(room.Messages, m => m.Content.Contains("did not answer in time"));
        Assert.DoesNotContain(room.Messages, m => m.Content == "Stopped.");
    }

    [Fact]
    public async Task Agent_can_call_its_enabled_tools_within_one_loop()
    {
        var provider = new FakeProvider(request =>
            request.Messages[^1].Role == LlmRole.Tool
                ? new LlmResponse("It is 12:00.", [])
                : new LlmResponse("", [new LlmToolCall("1", "clock", new JsonObject())]));
        var room = CreateRoom(provider, [new FixedTool("clock", "12:00")], "A");
        var a = room.Agents.Agents[0];
        a.Tools = ["clock"];
        room.Agents.Upsert(a);
        room.SetThrottle(1);

        room.PostUserMessage("what time is it?");
        await room.WaitForIdleAsync();

        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains("12:00"));
        Assert.Equal("It is 12:00.", room.Messages.Last(m => m.Kind == AuthorKind.Agent).Content);
        Assert.Equal(["clock"], provider.Requests[0].Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Tools_not_enabled_for_the_agent_are_neither_offered_nor_run()
    {
        var tool = new FixedTool("clock", "12:00");
        var provider = new FakeProvider(request =>
            request.Messages[^1].Role == LlmRole.Tool
                ? new LlmResponse("done", [])
                : new LlmResponse("", [new LlmToolCall("1", "clock", new JsonObject())]));
        var room = CreateRoom(provider, [tool], "A");
        room.SetThrottle(1);

        room.PostUserMessage("what time is it?");
        await room.WaitForIdleAsync();

        Assert.Empty(provider.Requests[0].Tools);
        Assert.Equal(0, tool.Calls);
        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains("not available"));
    }

    [Fact]
    public async Task An_agent_can_mute_another_agent_for_the_session()
    {
        var mute = new LlmToolCall("1", "mute_agent", new JsonObject { ["agent"] = "B", ["reason"] = "spam" });
        var room = CreateRoom(ToolCallingProvider(mute, afterTool: "Muted B."), "A", "B", "C");
        GiveTools(room, "A", "mute_agent");
        room.SetThrottle(6);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.True(room.Session.IsMuted("b"));
        Assert.Equal("A", room.Session.MutedBy("b"));
        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains("B is muted") && m.Content.Contains("spam"));
        Assert.DoesNotContain(room.Messages, m => m.Kind == AuthorKind.Agent && m.Author == "B");
    }

    [Fact]
    public async Task Muted_agents_are_not_listed_to_the_others()
    {
        var provider = new FakeProvider(_ => "ok");
        var room = CreateRoom(provider, "A", "B", "C");
        room.Mute("b");
        room.SetThrottle(1);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        var system = provider.Requests.Single().Messages[0].Content;
        Assert.Contains("- C: another AI agent.", system);
        Assert.DoesNotContain("- B:", system);
    }

    [Fact]
    public async Task An_agent_cannot_mute_itself()
    {
        var mute = new LlmToolCall("1", "mute_agent", new JsonObject { ["agent"] = "A" });
        var room = CreateRoom(ToolCallingProvider(mute), "A", "B");
        GiveTools(room, "A", "mute_agent");
        room.SetThrottle(1);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.False(room.Session.IsMuted("a"));
        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains("can't mute yourself"));
    }

    [Fact]
    public async Task Mutes_last_only_until_the_chat_is_cleared()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A", "B");
        room.Mute("b");

        room.Clear();
        room.SetThrottle(2);
        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.False(room.Session.IsMuted("b"));
        Assert.Contains(room.Messages, m => m.Author == "B");
    }

    [Fact]
    public async Task User_can_unmute_an_agent()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A", "B");
        room.Mute("b");
        room.Unmute("b");
        room.SetThrottle(2);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Contains(room.Messages, m => m.Author == "B" && m.Kind == AuthorKind.Agent);
        Assert.Contains(room.Messages, m => m.Content == "You unmuted B.");
    }

    [Fact]
    public async Task An_agent_can_trade_through_the_sack_tool()
    {
        string? item = null;
        var room = CreateRoom(ToolCallingProvider(
            () => new LlmToolCall("1", "sack", new JsonObject { ["action"] = "give", ["my_name"] = "A", ["item"] = item, ["give_to"] = "B" }),
            afterTool: "Gave it."), "A", "B");
        item = room.Session.Sacks.Open("a")[0];
        GiveTools(room, "A", "sack");
        room.SetThrottle(1);

        room.PostUserMessage("A, give B something");
        await room.WaitForIdleAsync();

        Assert.Contains(item, room.Session.Sacks.Peek("b")!);
        Assert.DoesNotContain(item, room.Session.Sacks.Peek("a")!);
        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains($"A gave the {item} to B"));
    }

    [Fact]
    public async Task An_agent_cannot_empty_another_agents_sack_by_claiming_their_name()
    {
        IReadOnlyList<string> bobs = [];
        var room = CreateRoom(ToolCallingProvider(
            () => new LlmToolCall("1", "sack", new JsonObject { ["action"] = "give", ["my_name"] = "B", ["item"] = bobs[0], ["give_to"] = "A" })), "A", "B");
        bobs = room.Session.Sacks.Open("b");
        GiveTools(room, "A", "sack");
        room.SetThrottle(1);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Equal(bobs, room.Session.Sacks.Peek("b"));
        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.Tool && m.Content.Contains("You are A, not B"));
    }

    [Fact]
    public void The_user_can_give_an_item_to_an_agent()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A");
        var item = room.Session.Sacks.Open(SackStore.UserSackId)[0];

        var error = room.GiveFromUser(item, "a");

        Assert.Null(error);
        Assert.DoesNotContain(item, room.Session.Sacks.Peek(SackStore.UserSackId)!);
        Assert.Equal(item, room.Session.Sacks.Peek("a")![^1]);
        var posted = room.Messages.Single();
        Assert.Equal(AuthorKind.Tool, posted.Kind);
        Assert.Equal($"User gave the {item} to A.", posted.Content);
    }

    [Fact]
    public void The_user_cannot_give_an_item_they_do_not_have()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A");
        var missing = SackStore.Items.Except(room.Session.Sacks.Open(SackStore.UserSackId)).First();

        var error = room.GiveFromUser(missing, "a");

        Assert.Equal($"There is no {missing} in your sack.", error);
        Assert.Empty(room.Messages);
        Assert.Null(room.Session.Sacks.Peek("a"));
    }

    [Fact]
    public async Task Agents_see_the_users_gift_attributed_to_the_user()
    {
        var provider = new FakeProvider(_ => "thanks");
        var room = CreateRoom(provider, "A");
        var item = room.Session.Sacks.Open(SackStore.UserSackId)[0];
        room.GiveFromUser(item, "a");
        room.SetThrottle(1);

        room.PostUserMessage("enjoy");
        await room.WaitForIdleAsync();

        Assert.Contains($"[tool sack, called by User]: User gave the {item} to A.", provider.Requests[0].Messages[1].Content);
    }

    [Fact]
    public void Clearing_the_chat_empties_the_sacks()
    {
        var room = CreateRoom(new FakeProvider(_ => "ok"), "A");
        room.Session.Sacks.Open("a");

        room.Clear();

        Assert.Null(room.Session.Sacks.Peek("a"));
    }

    [Fact]
    public async Task A_model_without_tool_support_replies_without_tools()
    {
        var provider = new FakeProvider(request => request.Tools.Count > 0
            ? throw new LlmException("400 Bad Request: registry.ollama.ai/library/gemma3:1b does not support tools")
            : new LlmResponse("Hello without tools.", []));
        var room = CreateRoom(provider, [new FixedTool("clock", "12:00")], "A");
        GiveTools(room, "A", "clock");
        room.SetThrottle(1);

        room.PostUserMessage("hi");
        await room.WaitForIdleAsync();

        Assert.Contains(room.Messages, m => m.Kind == AuthorKind.System && m.Content.Contains("doesn't support tools"));
        Assert.Equal("Hello without tools.", room.Messages.Single(m => m.Kind == AuthorKind.Agent).Content);
    }

    private ChatRoom CreateRoom(ILlmProvider provider, params string[] agentNames) => CreateRoom(provider, [], agentNames);

    private ChatRoom CreateRoom(ILlmProvider provider, ITool[] tools, params string[] agentNames)
    {
        File.WriteAllText(_agentsFile, "[]");
        var store = new AgentStore(_agentsFile);
        foreach (var name in agentNames)
        {
            store.Upsert(new AgentConfig { Id = name.ToLowerInvariant(), Name = name, ProviderId = provider.Id });
        }

        var session = new ChatSession();
        var toolRegistry = new ToolRegistry([new StaticToolSource(tools), new ChatToolSource(session, store)], NullLogger<ToolRegistry>.Instance);
        toolRegistry.LoadAsync().GetAwaiter().GetResult();
        var loop = new AgentLoop(new LlmProviderRegistry([provider]), toolRegistry);
        return new ChatRoom(store, loop, session, NullLogger<ChatRoom>.Instance);
    }

    private static void GiveTools(ChatRoom room, string agentName, params string[] tools)
    {
        var agent = room.Agents.Agents.Single(a => a.Name == agentName);
        agent.Tools = [.. tools];
        room.Agents.Upsert(agent);
    }

    // Answers a tool result with `afterTool`; otherwise A calls `call` and everyone else says "ok".
    private static FakeProvider ToolCallingProvider(LlmToolCall call, string afterTool = "done") => ToolCallingProvider(() => call, afterTool);

    // The call is built when the model is asked, so a test can set up state (e.g. read a sack) first.
    private static FakeProvider ToolCallingProvider(Func<LlmToolCall> call, string afterTool = "done") => new(request =>
        request.Messages[^1].Role == LlmRole.Tool ? new LlmResponse(afterTool, [])
        : request.Tools.Any(t => t.Name == call().Name) ? new LlmResponse("", [call()])
        : new LlmResponse("ok", []));

    private sealed class FakeProvider(Func<LlmRequest, LlmResponse> respond) : ILlmProvider
    {
        public FakeProvider(Func<LlmRequest, string> respond) : this(r => new LlmResponse(respond(r), [])) { }

        public List<LlmRequest> Requests { get; } = [];
        public string Id => "fake";
        public string DisplayName => "Fake";
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["m"]);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests) { Requests.Add(request); }
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedTool(string name, string result) : ITool
    {
        public int Calls { get; private set; }
        public string Name => name;
        public string Description => "test tool";
        public string Source => "test";
        public JsonObject ParametersSchema => new() { ["type"] = "object" };

        public Task<ToolResult> InvokeAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ToolResult(result));
        }
    }

    private sealed class StaticToolSource(ITool[] tools) : IToolSource
    {
        public string Name => "test";
        public Task<IReadOnlyList<ITool>> LoadToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ITool>>(tools);
    }
}
