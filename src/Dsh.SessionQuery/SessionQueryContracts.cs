using Dsh.Core;

namespace Dsh.SessionQuery;

public sealed record SessionQueryHit(string SessionId, long Seq, string Type, string Snippet);

/** 会话全文检索的消费侧契约: 实现由 session-query 插件注册, 界面等模块只面向本接口。 */
public interface ISessionQueryService
{
    public const string ServiceName = "sessionQuery";

    IReadOnlyList<SessionQueryHit> Search(string query, int limit = 20);
}
