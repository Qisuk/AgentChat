using System.Text.Json.Nodes;
using AgentChat.Llm;

namespace AgentChat.Tests;

public class RandomWordsProviderTests
{
    private static readonly RandomWordsProvider Provider = new(new ProviderOptions { Id = "random" });

    private static readonly LlmToolDefinition Mute = new("mute_agent", "mute", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["agent"] = new JsonObject { ["type"] = "string" },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
    });

    // The shape PromptBuilder sends: one labelled transcript followed by an instruction.
    private static LlmRequest Request(string lastLine, IReadOnlyList<LlmToolDefinition>? tools = null, string model = "medium") => new()
    {
        Model = model,
        Tools = tools ?? [],
        Messages =
        [
            new(LlmRole.System, "You are Rando."),
            new(LlmRole.User,
                "Chat transcript so far (oldest first):\n\n"
                + "[User]: the quick brown fox jumps over the lazy dog\n"
                + "[Ada]: we should plan the robot naming\n    ceremony carefully\n"
                + "[Rando (you)]: fox\n"
                + lastLine
                + "\n\nWrite Rando's next message in this chat. Speak only as Rando: don't write lines for anyone else."),
        ],
    };

    // Inputs whose seed leads to speaking rather than a pass or a tool call.
    private static IEnumerable<LlmRequest> SpokenReplies() =>
        Enumerable.Range(0, 200).Select(i => Request($"[Quinn]: point {i}"))
            .Where(r => Provider.CompleteAsync(r).Result.Content != "[PASS]");

    [Fact]
    public async Task Same_chat_gives_the_same_reply()
    {
        var request = Request("[Quinn]: what about risks?");

        var first = await Provider.CompleteAsync(request);
        var second = await Provider.CompleteAsync(request);

        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public async Task Replies_are_made_of_words_from_the_chat()
    {
        var chatWords = "the quick brown fox jumps over lazy dog we should plan robot naming ceremony carefully"
            .Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var request in SpokenReplies().Take(20))
        {
            var reply = (await Provider.CompleteAsync(request)).Content.TrimEnd('.');
            Assert.All(reply.Split(' '), w => Assert.True(
                chatWords.Contains(w) || w.StartsWith("point", StringComparison.OrdinalIgnoreCase) || int.TryParse(w, out _),
                $"'{w}' is not from the chat (reply: {reply})"));
            Assert.DoesNotContain("[", reply);
            Assert.DoesNotContain("Write", reply);      // the instruction isn't quoted
            Assert.DoesNotContain("transcript", reply);
        }
    }

    [Fact]
    public async Task Sometimes_calls_an_offered_tool_naming_a_participant()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Provider.CompleteAsync(Request($"[Quinn]: point {i}", [Mute]))));

        var calls = responses.SelectMany(r => r.ToolCalls).ToList();
        Assert.InRange(calls.Count, 15, 60);
        Assert.All(calls, c =>
        {
            Assert.Equal("mute_agent", c.Name);
            // Participants from the labels: never itself ("Rando (you)") or a tool.
            Assert.Contains(c.Arguments["agent"]!.GetValue<string>(), new[] { "User", "Ada", "Quinn" });
        });
    }

    [Fact]
    public async Task Never_calls_tools_when_none_are_offered_or_right_after_a_tool_result()
    {
        var noTools = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Provider.CompleteAsync(Request($"[Quinn]: point {i}"))));
        Assert.All(noTools, r => Assert.Empty(r.ToolCalls));

        var afterTool = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
        {
            var request = Request($"[Quinn]: point {i}", [Mute]);
            return Provider.CompleteAsync(request with { Messages = [.. request.Messages, new LlmMessage(LlmRole.Tool, $"Ada is muted {i}")] });
        }));
        Assert.All(afterTool, r =>
        {
            Assert.Empty(r.ToolCalls);
            Assert.NotEqual("[PASS]", r.Content);
        });
    }

    [Fact]
    public async Task Json_from_earlier_tool_calls_is_not_quoted_or_reused_as_arguments()
    {
        var json = """Rando called mute_agent({"agent":"You","reason":""reason":"agents""})""";
        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Provider.CompleteAsync(Request($"[tool mute_agent, called by Quinn]: {json} {i}", [Mute]))));

        Assert.All(responses, r => Assert.DoesNotContain("mute_agent({", r.Content));
        Assert.All(responses.SelectMany(r => r.ToolCalls).SelectMany(c => c.Arguments.Select(a => a.Value!.ToString())),
            value => Assert.DoesNotContain("\"", value));
    }

    [Fact]
    public async Task Sack_calls_use_the_fixed_choices_and_usually_its_own_name()
    {
        var sack = new AgentChat.Tools.SackTool(new AgentChat.Chat.SackStore(), new AgentChat.Chat.AgentStore(Path.Combine(Path.GetTempPath(), $"agentchat-{Guid.NewGuid():N}.json")));
        LlmToolDefinition[] tools = [new(sack.Name, sack.Description, sack.ParametersSchema)];

        var calls = (await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Provider.CompleteAsync(Request($"[Quinn]: point {i}", tools)))))
            .SelectMany(r => r.ToolCalls).ToList();

        Assert.NotEmpty(calls);
        Assert.All(calls, c =>
        {
            Assert.Contains(c.Arguments["action"]!.GetValue<string>(), new[] { "look", "give" });
            Assert.Contains(c.Arguments["item"]!.GetValue<string>(), AgentChat.Chat.SackStore.Items);
            Assert.Contains(c.Arguments["to"]!.GetValue<string>(), new[] { "User", "Ada", "Quinn" });
        });
        // Its own name comes from the "Rando (you)" label; sometimes it claims someone else's, which the tool refuses.
        var own = calls.Count(c => c.Arguments["agent"]!.GetValue<string>() == "Rando");
        Assert.InRange(own, calls.Count / 2, calls.Count - 1);
    }

    [Theory]
    [InlineData("short", 4, 10)]
    [InlineData("long", 25, 60)]
    public async Task Model_sets_the_reply_length(string model, int min, int max)
    {
        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Provider.CompleteAsync(Request($"[Quinn]: point {i}", model: model))));

        Assert.All(replies.Where(r => r.Content != "[PASS]"), r => Assert.InRange(r.Content.Split(' ').Length, min, max));
    }
}
