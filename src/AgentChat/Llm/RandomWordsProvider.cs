using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentChat.Llm;

/// <summary>
/// A "fast deterministic agent": no LLM at all. It replies with random runs of words lifted from the chat so
/// far, and sometimes calls one of its tools with random arguments. Useful for exercising the room, the
/// throttle and tools without waiting for a model.
/// <para>
/// Deterministic: the random generator is seeded from the conversation and the model name, so the same
/// chat always produces the same reply. Its "models" set the reply length.
/// </para>
/// </summary>
public sealed partial class RandomWordsProvider(ProviderOptions options) : ILlmProvider
{
    /// <summary>Chance of calling a tool on a turn where tools are offered.</summary>
    public const double ToolCallChance = 0.35;
    /// <summary>Chance of passing instead of speaking, so a room of these agents eventually goes quiet.</summary>
    public const double PassChance = 0.1;

    private static readonly Dictionary<string, (int Min, int Max)> Lengths = new()
    {
        ["short"] = (4, 10),
        ["medium"] = (10, 25),
        ["long"] = (25, 60),
    };

    public string Id { get; } = options.Id;
    public string DisplayName { get; } = options.DisplayName ?? options.Id;

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Lengths.Keys.ToList());

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var random = new Random(Seed(request));
        var history = request.Messages.Where(m => m.Role != LlmRole.System).ToList();
        var lastIsToolResult = history.Count > 0 && history[^1].Role == LlmRole.Tool;

        // Never call a tool straight after a tool result, so every loop ends with a report.
        if (!lastIsToolResult && request.Tools.Count > 0 && random.NextDouble() < ToolCallChance)
        {
            var tool = request.Tools[random.Next(request.Tools.Count)];
            var words = PlainWords(Words(history));
            var self = OwnName(request.Messages);
            return Task.FromResult(new LlmResponse("", [new LlmToolCall($"rnd_{random.Next():x}", tool.Name, RandomArguments(tool, words, Names(history), self, random))]));
        }

        if (!lastIsToolResult && random.NextDouble() < PassChance)
        {
            return Task.FromResult(new LlmResponse("[PASS]", []));
        }

        // After a tool call, quote the result; otherwise quote the whole chat.
        var source = lastIsToolResult ? [history[^1]] : history;
        return Task.FromResult(new LlmResponse(Babble(source, request.Model, random), []));
    }

    private static string Babble(IReadOnlyList<LlmMessage> source, string model, Random random)
    {
        var (min, max) = Lengths.GetValueOrDefault(model, Lengths["medium"]);
        var target = random.Next(min, max + 1);
        var messages = Utterances(source).Select(u => WordList(u.Text)).Where(w => w.Count > 0).ToList();
        if (messages.Count == 0)
        {
            return "Hello?";
        }

        // Stitch together extracts: short runs of consecutive words from random messages.
        var result = new List<string>();
        while (result.Count < target)
        {
            var words = messages[random.Next(messages.Count)];
            var start = random.Next(words.Count);
            var run = Math.Min(random.Next(2, 7), words.Count - start);
            result.AddRange(words.Skip(start).Take(run));
        }

        var text = string.Join(' ', result.Take(target)).TrimEnd(',', ';', ':');
        return char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') ? "" : ".");
    }

    /// <summary>Chance of using its own name in a field that asks for "your own name" (else someone else's).</summary>
    public const double OwnNameChance = 0.7;

    // "agent"/"name" anywhere in the field name; "to" only as the whole name or a "_to" suffix
    // ("give_to" is one, "topic" isn't).
    private static bool IsParticipantField(string field) =>
        field.Contains("agent", StringComparison.OrdinalIgnoreCase)
        || field.Contains("name", StringComparison.OrdinalIgnoreCase)
        || field is "to" or "recipient" or "target"
        || field.EndsWith("_to", StringComparison.OrdinalIgnoreCase);

    private static JsonObject RandomArguments(LlmToolDefinition tool, List<string> words, List<string> names, string? self, Random random)
    {
        var args = new JsonObject();
        if (tool.ParametersSchema["properties"] is not JsonObject properties)
        {
            return args;
        }

        foreach (var (name, schema) in properties)
        {
            var type = schema?["type"]?.GetValue<string>();
            var description = schema?["description"]?.GetValue<string>() ?? "";
            args[name] = type switch
            {
                // Fixed choices (e.g. the sack's actions and items): pick one.
                _ when schema?["enum"] is JsonArray { Count: > 0 } choices => choices[random.Next(choices.Count)]!.DeepClone(),
                "integer" => random.Next(0, 100),
                "number" => Math.Round(random.NextDouble() * 100, 2),
                "boolean" => random.Next(2) == 1,
                // "Your own name" fields usually get its real name, sometimes someone else's, which the tool should refuse.
                _ when self is not null && description.Contains("your own name", StringComparison.OrdinalIgnoreCase) && random.NextDouble() < OwnNameChance
                    => self,
                // Fields that look like they want a participant get one of the names seen in the chat.
                _ when IsParticipantField(name)
                    => names.Count > 0 ? names[random.Next(names.Count)] : Pick(words, random),
                _ => string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ => Pick(words, random))),
            };
        }
        return args;
    }

    // PromptBuilder's system prompt says "You are <name>, taking part in a group chat"; failing that,
    // its own transcript lines are labelled "<name> (you)".
    private static string? OwnName(IEnumerable<LlmMessage> messages)
    {
        var all = messages.ToList();
        var stated = all.Where(m => m.Role == LlmRole.System).Select(m => OwnNameLine().Match(m.Content)).FirstOrDefault(m => m.Success);
        if (stated is not null)
        {
            return stated.Groups[1].Value;
        }
        return Utterances(all).Select(u => u.Speaker).FirstOrDefault(s => s?.EndsWith(" (you)") == true)?[..^" (you)".Length];
    }

    [GeneratedRegex(@"You are (.{1,40}?), taking part in a group chat")]
    private static partial Regex OwnNameLine();

    private static string Pick(List<string> words, Random random) => words.Count > 0 ? words[random.Next(words.Count)] : "something";

    private static List<string> Words(IEnumerable<LlmMessage> history) => Utterances(history).SelectMany(u => WordList(u.Text)).ToList();

    // The speaker labels are the participants, apart from this agent itself ("Name (you)") and tools.
    private static List<string> Names(IEnumerable<LlmMessage> history) =>
        Utterances(history)
            .Select(u => u.Speaker)
            .OfType<string>()
            .Where(s => !s.EndsWith("(you)") && !s.StartsWith("tool ", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

    /// <summary>
    /// What was said, and by whom. The chat arrives as a transcript of "[Speaker]: text" lines (continuation
    /// lines indented). Unlabelled lines are the prompt's own instructions and aren't quoted. Tool results
    /// in the tool-calling exchange have no speaker.
    /// </summary>
    private static IEnumerable<(string? Speaker, string Text)> Utterances(IEnumerable<LlmMessage> history)
    {
        foreach (var message in history)
        {
            if (message.Role == LlmRole.Tool)
            {
                yield return (null, message.Content);
                continue;
            }

            string? speaker = null;
            var text = new StringBuilder();
            foreach (var line in message.Content.Split('\n'))
            {
                var label = Label().Match(line);
                if (label.Success)
                {
                    if (speaker is not null)
                    {
                        yield return (speaker, text.ToString());
                    }
                    speaker = label.Groups[1].Value;
                    text.Clear().Append(line[label.Length..]);
                }
                else if (speaker is not null && line.StartsWith(' '))
                {
                    text.Append('\n').Append(line.Trim());
                }
                else if (speaker is not null)
                {
                    yield return (speaker, text.ToString());
                    speaker = null;
                }
            }
            if (speaker is not null)
            {
                yield return (speaker, text.ToString());
            }
        }
    }

    private static List<string> WordList(string text)
    {
        // Long tokens are JSON from earlier tool calls; quoting them makes each generation's escaped
        // JSON bigger than the last, so only real words are reused.
        return text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w != PassTokenWord && w.Length <= MaxWordLength)
            .ToList();
    }

    private const int MaxWordLength = 30;

    // Tool arguments get plain words only (no quotes, braces or JSON fragments).
    private static List<string> PlainWords(IEnumerable<string> words) =>
        words.Select(w => w.Trim('.', ',', ';', ':', '!', '?', '\'', '"', '(', ')'))
            .Where(w => w.Length > 0 && w.All(c => char.IsLetterOrDigit(c) || c == '-'))
            .ToList();

    private const string PassTokenWord = "[PASS]";

    [GeneratedRegex(@"^\[([^\]\n]{1,60})\]:\s*")]
    private static partial Regex Label();

    // A stable hash (string.GetHashCode is randomised per process) of everything the reply depends on.
    private static int Seed(LlmRequest request)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(request.Model + "\u0001" + string.Join("\u0002", request.Messages.Select(m => m.Content))))
        {
            hash = (hash ^ b) * 16777619u;
        }
        return (int)hash;
    }
}
