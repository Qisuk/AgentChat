using System.Text.RegularExpressions;
using AgentChat.Models;

namespace AgentChat.Chat;

public enum RoomStatus
{
    /// <summary>Nothing has happened yet, or the chat was cleared.</summary>
    Idle,
    /// <summary>Agents are taking turns.</summary>
    Running,
    /// <summary>The throttle was reached, every agent passed, or the user pressed Stop: agents wait for feedback.</summary>
    WaitingForUser,
}

/// <summary>
/// The shared chat everyone sees (one room for the whole app). Agents take turns, one loop each, until the
/// throttle — the number of loops allowed between pieces of user feedback — is used up, every agent passes,
/// or the user presses Stop. A new user message (or Continue) gives the agents a fresh allowance.
/// </summary>
public sealed partial class ChatRoom
{
    public const int MaxThrottle = 50;

    private readonly AgentStore agents;
    private readonly AgentLoop loop;
    private readonly ChatSession session;
    private readonly ILogger<ChatRoom> logger;
    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = [];
    private CancellationTokenSource? _runCts;
    private Task _run = Task.CompletedTask;
    private string? _lastTurnAgentId;
    private Guid? _mentionHandledFor;
    private int _passStreak;
    private int _failStreak;
    private int _generation;
    private int _clearedUpTo;

    public ChatRoom(AgentStore agents, AgentLoop loop, ChatSession session, ILogger<ChatRoom> logger)
    {
        this.agents = agents;
        this.loop = loop;
        this.session = session;
        this.logger = logger;
        // Mutes come from the user (UI) or from agents (the mute_agent tool); either way the UI must refresh.
        session.Changed += Notify;
    }

    /// <summary>Raised whenever anything visible changes. Handlers run on the orchestrator's thread.</summary>
    public event Action? Changed;

    public AgentStore Agents => agents;
    public ChatSession Session => session;

    /// <summary>The user mutes an agent for the rest of this chat session.</summary>
    public void Mute(string agentId)
    {
        session.Mute(agentId, "You");
        if (agents.Find(agentId) is { } agent)
        {
            Append(SystemMessage($"You muted {agent.Name}."));
        }
    }

    public void Unmute(string agentId)
    {
        session.Unmute(agentId);
        if (agents.Find(agentId) is { } agent)
        {
            Append(SystemMessage($"You unmuted {agent.Name}."));
        }
    }
    public IReadOnlyList<ChatMessage> Messages { get { lock (_gate) { return _messages.ToList(); } } }
    public RoomStatus Status { get; private set; } = RoomStatus.Idle;
    /// <summary>Loops the agents may run between pieces of user feedback.</summary>
    public int Throttle { get; private set; } = 3;
    /// <summary>Loops run since the user's last message or Continue.</summary>
    public int LoopsUsed { get; private set; }
    public string? ActiveAgentId { get; private set; }
    public LoopPhase? ActivePhase { get; private set; }

    public void SetThrottle(int loops)
    {
        Throttle = Math.Clamp(loops, 1, MaxThrottle);
        Notify();
    }

    public void PostUserMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        Append(new ChatMessage { Kind = AuthorKind.User, Author = "You", Content = text.Trim() });
        GrantLoops();
    }

    /// <summary>Lets the agents carry on for another <see cref="Throttle"/> loops without the user saying anything.</summary>
    public void Continue() => GrantLoops();

    public void Stop()
    {
        lock (_gate) { _runCts?.Cancel(); }
    }

    public void Clear()
    {
        Stop();
        lock (_gate)
        {
            _messages.Clear();
            // Runs started before the clear must not post into the new, empty chat.
            _clearedUpTo = _generation;
            _lastTurnAgentId = null;
            LoopsUsed = 0;
            Status = RoomStatus.Idle;
        }
        // Mutes last for one chat session; a cleared chat is a new session.
        session.Reset();
    }

    /// <summary>Completes when the agents stop taking turns. Mainly for tests.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate) { return _run; }
    }

    private void GrantLoops()
    {
        lock (_gate)
        {
            LoopsUsed = 0;
            _passStreak = 0;
            _failStreak = 0;
            // A running loop picks up the fresh allowance by itself; a stopped (or stopping) one is replaced.
            if (Status != RoomStatus.Running || _runCts?.IsCancellationRequested == true)
            {
                Status = RoomStatus.Running;
                _runCts = new CancellationTokenSource();
                var token = _runCts.Token;
                var generation = ++_generation;
                var previous = _run;
                _run = Task.Run(async () =>
                {
                    await previous;
                    await RunAsync(generation, token);
                });
            }
        }
        Notify();
    }

    private async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        string reason;
        try
        {
            while (true)
            {
                // Muted agents sit out: they don't take turns and aren't listed to the others as participants.
                var roster = agents.Agents.Where(a => !session.IsMuted(a.Id)).ToList();
                AgentConfig agent;
                // Deciding to stop and changing the status happen under one lock, so a user message
                // arriving at the same moment either extends this run or starts a new one.
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var enabled = roster.Where(a => a.Enabled).ToList();
                    var stop = enabled.Count == 0 ? "No agents can speak (all are disabled or muted)."
                        : _failStreak >= enabled.Count ? "Every agent failed. Check the errors above (is the model server running?)."
                        : _passStreak >= enabled.Count ? "Every agent passed. Your turn."
                        : LoopsUsed >= Throttle ? $"Throttle reached ({Throttle} loop{(Throttle == 1 ? "" : "s")}). Reply, or press Continue to let the agents carry on."
                        : null;
                    if (stop is not null)
                    {
                        reason = stop;
                        Finish(generation);
                        break;
                    }

                    agent = PickNext(enabled);
                    LoopsUsed++;
                    ActiveAgentId = agent.Id;
                    _lastTurnAgentId = agent.Id;
                }
                Notify();

                bool spoke;
                try
                {
                    spoke = await loop.RunAsync(agent, roster, Messages, LoopsUsed, SetPhase, Append, cancellationToken);
                    _failStreak = 0;
                }
                // An HTTP timeout is also an OperationCanceledException; only the user's Stop ends the run.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Agent {Agent} failed", agent.Name);
                    var message = ex is OperationCanceledException ? "the model did not answer in time" : ex.Message;
                    Append(SystemMessage($"{agent.Name} ({agent.ProviderId}/{agent.Model}) failed: {message}"));
                    spoke = false;
                    _failStreak++;
                }

                _passStreak = spoke ? 0 : _passStreak + 1;
            }
        }
        catch (OperationCanceledException)
        {
            reason = "Stopped.";
            lock (_gate) { Finish(generation); }
        }

        if (generation > _clearedUpTo)
        {
            Append(SystemMessage(reason));
        }
    }

    // Only the newest run owns the status; a stopped run finishing late must not override its replacement.
    private void Finish(int generation)
    {
        if (generation == _generation)
        {
            Status = RoomStatus.WaitingForUser;
            ActiveAgentId = null;
            ActivePhase = null;
        }
    }

    /// <summary>
    /// An agent @mentioned in the latest message goes next; otherwise agents take turns in order,
    /// never answering themselves straight away.
    /// </summary>
    private AgentConfig PickNext(List<AgentConfig> enabled)
    {
        var last = Messages.LastOrDefault(m => m.Kind is AuthorKind.User or AuthorKind.Agent);
        if (last is not null)
        {
            var mentioned = Mention().Matches(last.Content)
                .Select(m => enabled.FirstOrDefault(a => NameKey(a.Name) == NameKey(m.Groups[1].Value)))
                .FirstOrDefault(a => a is not null && a.Id != last.AgentId);
            // A mention only counts once: once the agent has had its turn for this message (even if it
            // passed), fall back to round robin.
            if (mentioned is not null && _mentionHandledFor != last.Id)
            {
                _mentionHandledFor = last.Id;
                return mentioned;
            }
        }

        var start = enabled.FindIndex(a => a.Id == _lastTurnAgentId);
        for (var i = 1; i <= enabled.Count; i++)
        {
            var candidate = enabled[(start + i) % enabled.Count];
            if (enabled.Count == 1 || candidate.Id != last?.AgentId)
            {
                return candidate;
            }
        }
        return enabled[0];
    }

    private static string NameKey(string name) => name.Replace(" ", "").ToLowerInvariant();

    [GeneratedRegex(@"@([\w-]+)")]
    private static partial Regex Mention();

    private void SetPhase(LoopPhase phase)
    {
        ActivePhase = phase;
        Notify();
    }

    private void Append(ChatMessage message)
    {
        lock (_gate) { _messages.Add(message); }
        Notify();
    }

    private static ChatMessage SystemMessage(string text) => new() { Kind = AuthorKind.System, Author = "System", Content = text };

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A chat listener failed");
        }
    }
}
