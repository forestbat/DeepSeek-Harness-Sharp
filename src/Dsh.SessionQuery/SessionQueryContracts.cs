using Dsh.Core;

namespace Dsh.SessionQuery;

public sealed record SessionQueryHit(string SessionId, long Seq, string Type, string Snippet);
