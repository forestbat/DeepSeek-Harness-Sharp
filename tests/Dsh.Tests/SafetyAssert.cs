using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

internal static class SafetyAssert
{
    public static SafetyDecisionAuditPayload Denied(Session session, string? callId, string ruleId,
        string? path, string? intent, string tool, int since = 0)
    {
        var audit = Assert.Single(session.SnapshotEvents().Skip(since).Select(item => item.Data)
            .OfType<SafetyDecisionAuditPayload>(), audit => audit.Decision == "deny"
                && (callId is null || audit.CallId == callId));
        Assert.Equal(ruleId, audit.RuleId);
        Assert.Equal("blacklist", audit.RuleKind);
        Assert.Equal(path is null ? null : CanonicalFilePath.Canonicalize(path), audit.Path);
        Assert.Equal(intent, audit.Intent);
        Assert.Equal(tool, audit.Tool);
        Assert.Equal(session.Id.Value, audit.SessionId);
        Assert.False(string.IsNullOrEmpty(audit.CallId));
        return audit;
    }
}
