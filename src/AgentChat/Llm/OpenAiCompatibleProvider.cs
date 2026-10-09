using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentChat.Llm;

/// <summary>Talks to any endpoint implementing the OpenAI chat completions API (Ollama serves one at /v1).</summary>
public sealed partial class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly HttpClient _http;

    public OpenAiCompatibleProvider(ProviderOptions options, HttpClient http)
    {
        Id = options.Id;
        DisplayName = options.DisplayName ?? options.Id;
        _http = http;
        // A trailing slash makes "models" resolve under /v1 rather than replacing it.
        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public string Id { get; }
    public string DisplayName { get; }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("models", cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        return body["data"]?.AsArray()
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var json = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode?)ToJson(m)).ToArray()),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        if (request.Tools.Count > 0)
        {
            json["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.ParametersSchema.DeepClone(),
                },
            }).ToArray());
        }

        using var content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("chat/completions", content, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);

        var message = body["choices"]?[0]?["message"] ?? throw new LlmException("Response had no choices.");
        var text = StripThinking(message["content"]?.GetValue<string>() ?? "");
        var calls = message["tool_calls"]?.AsArray().Select(ParseToolCall).OfType<LlmToolCall>().ToList() ?? [];
        if (calls.Count == 0 && request.Tools.Count > 0 && ParseTextToolCall(text, request.Tools) is { } textCall)
        {
            return new LlmResponse("", [textCall]);
        }
        return new LlmResponse(text, calls);
    }

    /// <summary>
    /// Small models (e.g. llama3.2:3b) sometimes write the tool call as JSON in the reply text instead of
    /// using tool_calls: {"name": "current_time", "parameters": {}}. Treat that as a call to an offered tool.
    /// </summary>
    public static LlmToolCall? ParseTextToolCall(string text, IReadOnlyList<LlmToolDefinition> tools)
    {
        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            json = json.Trim('`').Trim();
            if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                json = json[4..].TrimStart();
            }
        }
        if (!json.StartsWith('{'))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj
                || obj["name"] is not JsonValue nameNode
                || !nameNode.TryGetValue<string>(out var name)
                || !tools.Any(t => t.Name == name))
            {
                return null;
            }
            var args = (obj["parameters"] ?? obj["arguments"]) switch
            {
                JsonObject o => (JsonObject)o.DeepClone(),
                JsonValue v when v.TryGetValue<string>(out var s) => JsonNode.Parse(s) as JsonObject ?? new JsonObject(),
                _ => new JsonObject(),
            };
            return new LlmToolCall($"text_{Guid.NewGuid():N}", name, args);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static JsonObject ToJson(LlmMessage message)
    {
        var json = new JsonObject
        {
            ["role"] = message.Role.ToString().ToLowerInvariant(),
            ["content"] = message.Content,
        };
        if (message.ToolCalls.Count > 0)
        {
            json["tool_calls"] = new JsonArray(message.ToolCalls.Select(c => (JsonNode?)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToJsonString() },
            }).ToArray());
        }
        if (message.ToolCallId is not null)
        {
            json["tool_call_id"] = message.ToolCallId;
        }
        return json;
    }

    private static LlmToolCall? ParseToolCall(JsonNode? node)
    {
        var name = node?["function"]?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Arguments are normally a JSON string, but some servers send an object.
        var raw = node!["function"]?["arguments"];
        JsonObject args;
        try
        {
            args = raw switch
            {
                JsonObject o => (JsonObject)o.DeepClone(),
                JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => JsonNode.Parse(s) as JsonObject ?? new JsonObject(),
                _ => new JsonObject(),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            args = new JsonObject();
        }

        return new LlmToolCall(node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), name, args);
    }

    // Reasoning models (qwen3, deepseek-r1, ...) may put <think>...</think> in the answer; keep it out of the chat.
    private static string StripThinking(string text) => ThinkBlock().Replace(text, "").Trim();

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length > 300 ? text[..300] + "…" : text;
            throw new LlmException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new LlmException("Response was not a JSON object.");
    }
}
