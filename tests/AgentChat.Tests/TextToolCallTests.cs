using System.Text.Json.Nodes;
using AgentChat.Llm;

namespace AgentChat.Tests;

public class TextToolCallTests
{
    private static readonly LlmToolDefinition[] Tools =
        [new("current_time", "time", new JsonObject { ["type"] = "object" })];

    [Theory]
    [InlineData("""{"name": "current_time", "parameters": {}}""")]
    [InlineData("""{"name":"current_time","arguments":"{}"}""")]
    [InlineData("```json\n{\"name\": \"current_time\", \"parameters\": {}}\n```")]
    public void Json_tool_calls_written_as_text_are_recognised(string text)
    {
        var call = OpenAiCompatibleProvider.ParseTextToolCall(text, Tools);

        Assert.NotNull(call);
        Assert.Equal("current_time", call.Name);
    }

    [Theory]
    [InlineData("""{"name":"current_time","parameters{}".format("")}" """)] // seen from llama3.2:3b: not valid JSON
    [InlineData("""{"name": "delete_everything", "parameters": {}}""")]   // not an offered tool
    [InlineData("It is about noon.")]
    public void Anything_else_is_left_as_text(string text)
    {
        Assert.Null(OpenAiCompatibleProvider.ParseTextToolCall(text, Tools));
    }
}
