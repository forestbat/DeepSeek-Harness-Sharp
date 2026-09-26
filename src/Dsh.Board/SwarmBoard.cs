using System.Text;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Board;

public enum BoardMessageKind
{
    Info,
    Ask,
    Result,
    Hold,
    Veto,
}

public static class BoardMessageKindWire
{
    public static string Of(BoardMessageKind kind) => kind switch
    {
        BoardMessageKind.Info => "INFO",
        BoardMessageKind.Ask => "ASK",
        BoardMessageKind.Result => "RESULT",
        BoardMessageKind.Hold => "HOLD",
        BoardMessageKind.Veto => "VETO",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static BoardMessageKind Parse(string value) => value switch
    {
        "INFO" => BoardMessageKind.Info,
        "ASK" => BoardMessageKind.Ask,
        "RESULT" => BoardMessageKind.Result,
        "HOLD" => BoardMessageKind.Hold,
        "VETO" => BoardMessageKind.Veto,
        _ => throw new BoardException($"board message type \"{value}\" is invalid", BoardErrorCodes.InvalidMessage),
    };
}

public static class BoardErrorCodes
{
    public const string InvalidMessage = "BOARD_INVALID_MESSAGE";
    public const string CapacityExceeded = "BOARD_CAPACITY_EXCEEDED";
    public const string UnknownRecipient = "BOARD_UNKNOWN_RECIPIENT";
    public const string UnknownReference = "BOARD_UNKNOWN_REFERENCE";
    public const string Conflict = "BOARD_CONFLICT";
}

public sealed class BoardException(string message, string code) : HarnessException(message, code);

/** 看板消息：To 为 "ALL"、根会话 id 字符串（即 "main"）或参与者会话 id 字符串。 */
public sealed record BoardMessage(
    string Id,
    long Seq,
    long Timestamp,
    SessionId From,
    string To,
    BoardMessageKind Kind,
    string Body,
    string? ReplyTo = null);

public sealed record BoardPage(IReadOnlyList<BoardMessage> Messages, long Cursor, bool HasMore);

/**
 * 谱系树共享看板：每棵以主会话为根的树一块板，sub agent 之间以小的增量消息共享发现，
 * 替代大上下文的 fork 继承。进程内实现；集群部署时替换为共享存储实现（同一接口）。
 */
public sealed class SwarmBoard(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "board";

    public const int MaxMessageBytes = 4 * 1024;
    public const int MaxMessagesPerBoard = 1000;
    public const int MaxBytesPerBoard = 2 * 1024 * 1024;
    public const int MaxReadBytes = 32 * 1024;
    public const int DefaultReadLimit = 20;
    public const int MaxReadLimit = 50;

    private sealed class BoardState
    {
        public long NextSeq = 1;
        public List<BoardMessage> Messages = [];
        public int Bytes;
        public readonly Dictionary<(SessionId Sender, string Key), BoardMessage> Dedup = [];
    }

    private readonly Dictionary<SessionId, BoardState> _boards = [];
    private readonly Lock _sync = new();

    public static SwarmBoard Register(Context ctx) => new(ctx);

    public SessionId RootOf(IAgent agent) => agent.Session.Header.RootSession ?? agent.Id;

    public bool IsMain(IAgent agent) => RootOf(agent) == agent.Id;

    public BoardMessage Post(IAgent sender, string to, BoardMessageKind kind, string body, string? replyTo, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new BoardException("board message body is required", BoardErrorCodes.InvalidMessage);
        if (string.IsNullOrEmpty(to))
            throw new BoardException("board recipient is required", BoardErrorCodes.InvalidMessage);
        if (string.IsNullOrEmpty(idempotencyKey))
            throw new BoardException("board message idempotency key is required", BoardErrorCodes.InvalidMessage);
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        if (bodyBytes > MaxMessageBytes)
            throw new BoardException(
                $"board message body exceeds {MaxMessageBytes} bytes", BoardErrorCodes.CapacityExceeded);
        var root = RootOf(sender);
        lock (_sync)
        {
            var board = BoardFor(root);
            if (board.Dedup.TryGetValue((sender.Id, idempotencyKey), out var existing))
            {
                if (existing.To != NormalizeRecipient(to, root)
                    || existing.Kind != kind
                    || existing.Body != body
                    || existing.ReplyTo != replyTo)
                {
                    throw new BoardException(
                        "board post retried with different arguments under the same idempotency key",
                        BoardErrorCodes.Conflict);
                }
                return existing;
            }
            var recipient = NormalizeRecipient(to, root);
            if (recipient == sender.Id.Value)
                throw new BoardException("board messages cannot be sent to yourself", BoardErrorCodes.InvalidMessage);
            AssertParticipant(root, recipient);
            if (replyTo is not null && !board.Messages.Exists(message => message.Id == replyTo))
                throw new BoardException($"reply target \"{replyTo}\" is not on this board", BoardErrorCodes.UnknownReference);
            if (board.Messages.Count >= MaxMessagesPerBoard)
                throw new BoardException("board message limit reached", BoardErrorCodes.CapacityExceeded);
            if (board.Bytes + bodyBytes > MaxBytesPerBoard)
                throw new BoardException("board storage limit reached", BoardErrorCodes.CapacityExceeded);
            var message = new BoardMessage(
                $"board-{Guid.NewGuid():N}",
                board.NextSeq++,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sender.Id,
                recipient,
                kind,
                body,
                replyTo);
            board.Messages.Add(message);
            board.Bytes += bodyBytes;
            board.Dedup[(sender.Id, idempotencyKey)] = message;
            return message;
        }
    }

    /** 增量读：返回 seq 大于 since 的消息（升序），受 limit 与单次读字节上限约束。 */
    public BoardPage Read(IAgent reader, long since = 0, int limit = DefaultReadLimit)
    {
        if (limit < 1 || limit > MaxReadLimit)
            throw new BoardException($"board read limit must be between 1 and {MaxReadLimit}", BoardErrorCodes.InvalidMessage);
        lock (_sync)
        {
            var board = BoardFor(RootOf(reader));
            var page = new List<BoardMessage>();
            var bytes = 0;
            foreach (var message in board.Messages)
            {
                if (message.Seq <= since)
                    continue;
                var size = Encoding.UTF8.GetByteCount(message.Body) + 256;
                if (page.Count >= limit || bytes + size > MaxReadBytes)
                    break;
                page.Add(message);
                bytes += size;
            }
            var hasMore = page.Count > 0 && board.Messages[^1].Seq > page[^1].Seq;
            return new BoardPage(page, page.Count > 0 ? page[^1].Seq : since, hasMore);
        }
    }

    /** 收件活动水位：发给该读者（或 ALL）且非本人发出的最新消息 seq；无则返回 after 原值。 */
    public long Activity(IAgent viewer, long after)
    {
        lock (_sync)
        {
            var board = BoardFor(RootOf(viewer));
            var recipient = viewer.Id.Value;
            var latest = after;
            foreach (var message in board.Messages)
            {
                if (message.Seq <= after || message.From == viewer.Id)
                    continue;
                if (message.To == "ALL" || message.To == recipient)
                    latest = message.Seq;
            }
            return latest;
        }
    }

    /** 参与者名册：树根 + 店内 RootSession 指向该根的会话（无头子代不入店，不在名册）。 */
    public IReadOnlyList<SessionId> Roster(IAgent viewer)
    {
        var root = RootOf(viewer);
        var roster = new List<SessionId> { root };
        if (Ctx.Get<SessionStore>(SessionStore.ServiceName, false) is not { } sessions)
            return roster;
        foreach (var session in sessions.List())
        {
            if (session.Header.RootSession == root && session.Id != root)
                roster.Add(session.Id);
        }
        return roster;
    }

    private BoardState BoardFor(SessionId root)
        => _boards.TryGetValue(root, out var board) ? board : _boards[root] = new BoardState();

    private static string NormalizeRecipient(string to, SessionId root)
        => to == "main" ? root.Value : to;

    private void AssertParticipant(SessionId root, string recipient)
    {
        if (recipient == "ALL" || recipient == root.Value)
            return;
        if (Ctx.Get<SessionStore>(SessionStore.ServiceName, false) is not { } sessions)
            return;
        var target = sessions.Get(SessionId.Create(recipient));
        if (target is null || (target.Header.RootSession ?? target.Id) != root)
            throw new BoardException(
                $"board recipient \"{recipient}\" is not a participant in this board", BoardErrorCodes.UnknownRecipient);
    }
}
