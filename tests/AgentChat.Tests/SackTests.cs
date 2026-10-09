using System.Text.Json.Nodes;
using AgentChat.Chat;
using AgentChat.Models;
using AgentChat.Tools;

namespace AgentChat.Tests;

public sealed class SackTests : IDisposable
{
    private readonly string _agentsFile = Path.Combine(Path.GetTempPath(), $"agentchat-sack-{Guid.NewGuid():N}.json");
    private readonly SackStore _sacks = new(new Random(42));
    private readonly AgentStore _agents;
    private readonly SackTool _tool;

    private static readonly ToolContext Ada = new("ada", "Ada");
    private static readonly ToolContext Bob = new("bob", "Bob");

    public SackTests()
    {
        File.WriteAllText(_agentsFile, "[]");
        _agents = new AgentStore(_agentsFile);
        _agents.Upsert(new AgentConfig { Id = "ada", Name = "Ada" });
        _agents.Upsert(new AgentConfig { Id = "bob", Name = "Bob" });
        _agents.Upsert(new AgentConfig { Id = "cy", Name = "Cy", Enabled = false });
        _tool = new SackTool(_sacks, _agents);
    }

    public void Dispose() => File.Delete(_agentsFile);

    private Task<ToolResult> Call(ToolContext caller, string action, string? agent = null, string? item = null, string? to = null)
    {
        var args = new JsonObject { ["action"] = action, ["agent"] = agent ?? caller.CallerName };
        if (item is not null) args["item"] = item;
        if (to is not null) args["to"] = to;
        return _tool.InvokeAsync(args, caller);
    }

    // ---- SackStore

    [Fact]
    public void A_new_sack_holds_three_different_items_from_the_list()
    {
        var items = _sacks.Open("ada");

        Assert.Equal(SackStore.StartingItems, items.Count);
        Assert.Equal(items.Count, items.Distinct().Count());
        Assert.All(items, i => Assert.Contains(i, SackStore.Items));
    }

    [Fact]
    public void A_sack_is_filled_once_and_only_when_used()
    {
        Assert.Null(_sacks.Peek("ada"));

        var first = _sacks.Open("ada");

        Assert.Equal(first, _sacks.Open("ada"));
        Assert.Equal(first, _sacks.Peek("ada"));
    }

    [Fact]
    public void Transfer_moves_exactly_one_item_and_fills_the_recipients_sack_first()
    {
        var item = _sacks.Open("ada")[0];

        Assert.True(_sacks.Transfer("ada", "bob", item));

        Assert.Equal(2, _sacks.Peek("ada")!.Count);
        Assert.Equal(4, _sacks.Peek("bob")!.Count);
        Assert.Equal(item, _sacks.Peek("bob")![^1]);
    }

    [Fact]
    public void Transfer_of_an_item_the_giver_lacks_changes_nothing()
    {
        var missing = SackStore.Items.Except(_sacks.Open("ada")).First();
        var bobBefore = _sacks.Open("bob");

        Assert.False(_sacks.Transfer("ada", "bob", missing));

        Assert.Equal(3, _sacks.Peek("ada")!.Count);
        Assert.Equal(bobBefore, _sacks.Peek("bob"));
    }

    [Fact]
    public void Reset_empties_every_sack()
    {
        _sacks.Open("ada");
        _sacks.Reset();

        Assert.Null(_sacks.Peek("ada"));
    }

    // ---- SackTool

    [Fact]
    public async Task Look_lists_the_callers_own_items()
    {
        var result = await Call(Ada, "look");

        Assert.False(result.IsError);
        Assert.Equal($"Ada's sack: {string.Join(", ", _sacks.Peek("ada")!)}.", result.Content);
        Assert.Null(_sacks.Peek("bob"));
    }

    [Fact]
    public async Task Give_moves_the_item_to_the_recipient()
    {
        var item = _sacks.Open("ada")[1];

        var result = await Call(Ada, "give", item: item, to: "@bob");

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain(item, _sacks.Peek("ada")!);
        Assert.Contains(item, _sacks.Peek("bob")!);
        Assert.StartsWith($"Ada gave the {item} to Bob.", result.Content);
    }

    [Fact]
    public async Task Claiming_another_agents_name_is_refused_and_moves_nothing()
    {
        var bobsItem = _sacks.Open("bob")[0];

        // Ada's model claims to be Bob, trying to give Bob's item to Ada.
        var result = await Call(Ada, "give", agent: "Bob", item: bobsItem, to: "Ada");

        Assert.True(result.IsError);
        Assert.Contains("You are Ada, not Bob", result.Content);
        Assert.Contains(bobsItem, _sacks.Peek("bob")!);
        Assert.Equal(3, _sacks.Peek("bob")!.Count);
        Assert.Null(_sacks.Peek("ada"));
    }

    [Fact]
    public async Task Looking_in_another_agents_sack_is_refused()
    {
        var result = await Call(Ada, "look", agent: "Bob");

        Assert.True(result.IsError);
        Assert.Null(_sacks.Peek("bob"));
    }

    [Fact]
    public async Task A_call_without_a_name_is_refused()
    {
        var result = await _tool.InvokeAsync(new JsonObject { ["action"] = "look" }, Ada);

        Assert.True(result.IsError);
        Assert.Contains("you are Ada", result.Content);
    }

    [Fact]
    public async Task Giving_an_item_you_do_not_have_is_refused()
    {
        var missing = SackStore.Items.Except(_sacks.Open("ada")).First();

        var result = await Call(Ada, "give", item: missing, to: "Bob");

        Assert.True(result.IsError);
        Assert.Contains($"no {missing} in your sack", result.Content);
        Assert.Equal(3, _sacks.Peek("ada")!.Count);
    }

    [Theory]
    [InlineData("Zed", "no agent called 'Zed'")]
    [InlineData("User", "no agent called 'User'")]
    [InlineData("Ada", "can't give an item to yourself")]
    public async Task Giving_to_someone_who_is_not_another_agent_is_refused(string to, string expected)
    {
        var item = _sacks.Open("ada")[0];

        var result = await Call(Ada, "give", item: item, to: to);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Contains(item, _sacks.Peek("ada")!);
    }

    [Fact]
    public async Task Disabled_or_muted_agents_can_still_receive()
    {
        var item = _sacks.Open("ada")[0];

        var result = await Call(Ada, "give", item: item, to: "Cy");

        Assert.False(result.IsError, result.Content);
        Assert.Contains(item, _sacks.Peek("cy")!);
    }

    [Theory]
    [InlineData("dragon")]
    [InlineData(null)]
    public async Task Give_needs_a_known_item(string? item)
    {
        var result = await Call(Ada, "give", item: item, to: "Bob");

        Assert.True(result.IsError);
        Assert.Contains("Say which item", result.Content);
    }

    [Fact]
    public void The_tool_offers_fixed_choices_for_action_and_item()
    {
        var properties = (JsonObject)_tool.ParametersSchema["properties"]!;

        Assert.Equal(["look", "give"], properties["action"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(SackStore.Items, properties["item"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}
