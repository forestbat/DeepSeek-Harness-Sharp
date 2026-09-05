using System.Runtime.CompilerServices;
using Cordis;

namespace Dsh.Core;

public sealed record SessionProjectionDefinition<TState>(
    string Key,
    int StateVersion,
    Func<SessionHeader, long, TState> Init,
    Func<TState, SessionEvent, TState> Apply);

// `ctx.sessionProjections`:按键注册的会话投影单元表,注册按 key 引用计数共享。
// 单元格惰性建立(对内存日志做全量 fold),随后由 `session/event` 逐事件推进。
public sealed class SessionProjectionRegistry : Service
{
    public const string ServiceName = "sessionProjections";

    private abstract class Registration
    {
        public abstract string Key { get; }
        public abstract int StateVersion { get; }
        public int Refs { get; set; }
        public abstract object? StateOf(Session session);
        public abstract void Drive(Session session, SessionEvent sessionEvent);
    }

    private sealed class Registration<TState>(SessionProjectionDefinition<TState> definition) : Registration
    {
        private sealed class Cell
        {
            public required TState State;
            public required long ObservedSeq;
        }

        private readonly ConditionalWeakTable<Session, Cell> _cells = new();

        public override string Key => definition.Key;
        public override int StateVersion => definition.StateVersion;

        public override object? StateOf(Session session) => CellFor(session).State;

        private Cell CellFor(Session session)
        {
            if (_cells.TryGetValue(session, out var cell))
            {
                Advance(cell, session, session.Seq - 1);
                return cell;
            }
            var built = Build(session, session.SnapshotEvents());
            _cells.Add(session, built);
            return built;
        }

        private Cell Build(Session session, IReadOnlyList<SessionEvent> events)
        {
            var state = definition.Init(session.Header, session.InheritedEventCount);
            foreach (var sessionEvent in events)
                state = definition.Apply(state, sessionEvent);
            return new Cell { State = state, ObservedSeq = events.Count == 0 ? -1 : events[^1].Seq };
        }

        private void Advance(Cell cell, Session session, long throughSeq)
        {
            if (cell.ObservedSeq >= throughSeq)
                return;
            for (var seq = cell.ObservedSeq + 1; seq <= throughSeq; seq++)
            {
                var sessionEvent = session.EventAt(seq);
                if (sessionEvent is null || sessionEvent.Seq != seq)
                {
                    throw new InvalidOperationException(
                        $"session projection {System.Text.Json.JsonSerializer.Serialize(definition.Key)} cannot advance across missing seq {seq}");
                }
                cell.State = definition.Apply(cell.State, sessionEvent);
                cell.ObservedSeq = seq;
            }
        }

        public override void Drive(Session session, SessionEvent sessionEvent)
        {
            if (_cells.TryGetValue(session, out var cell))
            {
                if (cell.ObservedSeq >= sessionEvent.Seq)
                    return;
                Advance(cell, session, sessionEvent.Seq - 1);
            }
            else
            {
                cell = Build(session, session.SnapshotEvents(0, sessionEvent.Seq));
                _cells.Add(session, cell);
            }
            cell.State = definition.Apply(cell.State, sessionEvent);
            cell.ObservedSeq = sessionEvent.Seq;
        }
    }

    private readonly Dictionary<string, Registration> _registrations = [];

    public SessionProjectionRegistry(Context ctx) : base(ctx, ServiceName)
    {
        ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            Drive((Session)args[0]!, (SessionEvent)args[1]!);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
    }

    public IDisposable Register<TState>(SessionProjectionDefinition<TState> definition)
    {
        if (definition.StateVersion < 0)
        {
            throw new ArgumentException(
                $"session projection {System.Text.Json.JsonSerializer.Serialize(definition.Key)} stateVersion must be a non-negative integer, got {definition.StateVersion}");
        }
        if (_registrations.TryGetValue(definition.Key, out var existing))
        {
            if (existing.StateVersion != definition.StateVersion)
            {
                throw new InvalidOperationException(
                    $"session projection {System.Text.Json.JsonSerializer.Serialize(definition.Key)} is already registered at stateVersion {existing.StateVersion}; refusing to share it with stateVersion {definition.StateVersion}");
            }
            existing.Refs += 1;
            return new RegistrationHandle(() => Release(definition.Key));
        }
        _registrations[definition.Key] = new Registration<TState>(definition) { Refs = 1 };
        return new RegistrationHandle(() => Release(definition.Key));
    }

    public TState? StateOf<TState>(Session session, string key) where TState : class
        => _registrations.TryGetValue(key, out var registration) ? (TState?)registration.StateOf(session) : null;

    private void Drive(Session session, SessionEvent sessionEvent)
    {
        foreach (var registration in _registrations.Values)
            registration.Drive(session, sessionEvent);
    }

    private void Release(string key)
    {
        if (!_registrations.TryGetValue(key, out var registration))
            return;
        registration.Refs -= 1;
        if (registration.Refs == 0)
            _registrations.Remove(key);
    }

    private sealed class RegistrationHandle(Action release) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            release();
        }
    }
}
