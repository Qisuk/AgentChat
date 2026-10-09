using System.Text.Json;
using AgentChat.Models;

namespace AgentChat.Chat;

/// <summary>Loads and saves the agents' settings as JSON so tweaks made in the UI survive a restart.</summary>
public sealed class AgentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<AgentConfig> _agents;

    public AgentStore(string path)
    {
        _path = path;
        _agents = File.Exists(path)
            ? JsonSerializer.Deserialize<List<AgentConfig>>(File.ReadAllText(path), JsonOptions) ?? []
            : CreateDefaults();
        if (!File.Exists(path))
        {
            Save();
        }
    }

    /// <summary>A snapshot of the agents, in turn order. Edit a copy and pass it to <see cref="Upsert"/>.</summary>
    public IReadOnlyList<AgentConfig> Agents
    {
        get { lock (_gate) { return _agents.Select(a => a.Clone()).ToList(); } }
    }

    public AgentConfig? Find(string id)
    {
        lock (_gate) { return _agents.FirstOrDefault(a => a.Id == id)?.Clone(); }
    }

    public void Upsert(AgentConfig agent)
    {
        lock (_gate)
        {
            var index = _agents.FindIndex(a => a.Id == agent.Id);
            if (index >= 0)
            {
                _agents[index] = agent.Clone();
            }
            else
            {
                _agents.Add(agent.Clone());
            }
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _agents.RemoveAll(a => a.Id == id);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_agents, JsonOptions));
    }

    /// <summary>Three agents on three different local models, so they disagree in interesting ways.</summary>
    public static List<AgentConfig> CreateDefaults() =>
    [
        new()
        {
            Id = "ada", Name = "Ada", Color = "#3b82f6", Model = "llama3.2:3b", Temperature = 0.6,
            SystemPrompt = "You are Ada, a pragmatic engineer. You break problems into concrete next steps and propose a plan.",
        },
        new()
        {
            Id = "quinn", Name = "Quinn", Color = "#f97316", Model = "qwen2.5:3b", Temperature = 0.5,
            SystemPrompt = "You are Quinn, a careful skeptic. You look for flaws, risks and missing information in what others say, and ask sharp questions.",
        },
        new()
        {
            Id = "gem", Name = "Gem", Color = "#10b981", Model = "gemma3:1b", Temperature = 0.9,
            SystemPrompt = "You are Gem, a creative thinker. You suggest unusual alternatives and build on other people's ideas.",
        },
        // No LLM: instant, deterministic word salad from the chat, with random tool calls.
        new()
        {
            Id = "rando", Name = "Rando", Color = "#a855f7", ProviderId = "random", Model = "short",
            SystemPrompt = "(Not used: this agent has no LLM.)",
            Tools = ["current_time", "mute_agent"],
        },
    ];
}
