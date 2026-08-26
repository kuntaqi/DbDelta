using System.Collections.Concurrent;

namespace DbDelta.Api.Services;

// Plan and comparison state live here rather than only in browser memory, so a refresh does not throw
// away the picking work. Single-user local tool, so an in-memory store with a cap is enough.
public sealed class CompareSessionStore
{
    private const int MaxSessions = 10;

    private readonly ConcurrentDictionary<string, CompareSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public void Add(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sessions[session.Id] = session;
        _order.Enqueue(session.Id);

        while (_order.Count > MaxSessions && _order.TryDequeue(out var oldest))
        {
            _sessions.TryRemove(oldest, out _);
        }
    }

    public CompareSession? Find(string id) => _sessions.GetValueOrDefault(id);
}
