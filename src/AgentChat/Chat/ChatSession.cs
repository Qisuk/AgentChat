namespace AgentChat.Chat;

/// <summary>
/// State that lasts for one chat session only: it is never saved, and is reset when the chat is cleared or
/// the app restarts. Holds the muted agents and everyone's sacks.
/// </summary>
public sealed class ChatSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _mutedBy = new();

    public ChatSession() : this(new SackStore())
    {
    }

    /// <summary>For tests: use this sack store (e.g. one with a seeded random generator).</summary>
    public ChatSession(SackStore sacks)
    {
        Sacks = sacks;
        Sacks.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised when an agent is muted or unmuted, or a sack changes.</summary>
    public event Action? Changed;

    public SackStore Sacks { get; }

    public bool IsMuted(string agentId)
    {
        lock (_gate) { return _mutedBy.ContainsKey(agentId); }
    }

    /// <summary>Who muted the agent ("You" or an agent's name), or null if it isn't muted.</summary>
    public string? MutedBy(string agentId)
    {
        lock (_gate) { return _mutedBy.GetValueOrDefault(agentId); }
    }

    public void Mute(string agentId, string by)
    {
        lock (_gate) { _mutedBy[agentId] = by; }
        Changed?.Invoke();
    }

    public void Unmute(string agentId)
    {
        lock (_gate) { _mutedBy.Remove(agentId); }
        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_gate) { _mutedBy.Clear(); }
        Sacks.Reset();
    }
}
