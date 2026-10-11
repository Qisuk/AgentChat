namespace AgentChat.Chat;

/// <summary>
/// Every agent's sack of items for the current chat session. A sack is filled with
/// <see cref="StartingItems"/> different random items the first time it is used (looked in or given to).
/// Only this class changes sacks, and a transfer moves the item out of one sack and into the other in a
/// single step, so items are never lost or duplicated. Who may move what is decided by the sack tool.
/// </summary>
public sealed class SackStore(Random? random = null)
{
    public static readonly IReadOnlyList<string> Items = ["sword", "axe", "shield", "wand", "hat", "shoes"];
    public const int StartingItems = 3;

    /// <summary>The human user's sack. Not a valid agent id, so it can't clash with an agent's sack.</summary>
    public const string UserSackId = "@user";

    private readonly Random _random = random ?? Random.Shared;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _sacks = new();

    /// <summary>Raised when any sack changes.</summary>
    public event Action? Changed;

    /// <summary>The agent's items, or null if its sack hasn't been used yet. Doesn't fill it (for the UI).</summary>
    public IReadOnlyList<string>? Peek(string agentId)
    {
        lock (_gate) { return _sacks.TryGetValue(agentId, out var sack) ? sack.ToList() : null; }
    }

    /// <summary>The agent's items, filling its sack first if this is the first time it is used.</summary>
    public IReadOnlyList<string> Open(string agentId)
    {
        bool filled;
        List<string> items;
        lock (_gate)
        {
            filled = !_sacks.ContainsKey(agentId);
            items = SackOf(agentId).ToList();
        }
        if (filled)
        {
            Changed?.Invoke();
        }
        return items;
    }

    /// <summary>Moves one <paramref name="item"/> from one agent's sack to another's.</summary>
    /// <returns>False (and nothing changes) if the giver doesn't have the item.</returns>
    public bool Transfer(string fromAgentId, string toAgentId, string item)
    {
        lock (_gate)
        {
            var from = SackOf(fromAgentId);
            var index = from.FindIndex(i => string.Equals(i, item, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }
            var to = SackOf(toAgentId);
            to.Add(from[index]);
            from.RemoveAt(index);
        }
        Changed?.Invoke();
        return true;
    }

    public void Reset()
    {
        lock (_gate) { _sacks.Clear(); }
        Changed?.Invoke();
    }

    // Caller holds _gate.
    private List<string> SackOf(string agentId)
    {
        if (!_sacks.TryGetValue(agentId, out var sack))
        {
            sack = Items.OrderBy(_ => _random.Next()).Take(StartingItems).ToList();
            _sacks[agentId] = sack;
        }
        return sack;
    }
}
