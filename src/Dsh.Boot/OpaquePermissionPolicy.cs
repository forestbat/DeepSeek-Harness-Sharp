using Dsh.Core;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Boot;

/** Compile only decisions that existing OS profiles really enforce; never pretend to intercept per-open events. */
internal static class OpaquePermissionPolicy
{
    internal static IReadOnlyList<string> DeniedWrites(PermissionRules rules, ToolRunContext? run, string cwd)
    {
        var input = new PermissionInput(run?.Name, run?.Arguments ?? default, cwd);
        var denied = new List<string>();
        if (!rules.AutoApprove)
            foreach (var (id, matcher) in rules.Whitelist)
                if ((matcher.Path is not null || matcher.Intent != "any")
                    && (run is null || PermissionEngine.MatchFields(matcher, input,
                        static path => PinnedFileAccess.Canonicalize(path), omitFileDimensions: true) is not null))
                    throw Unsupported(id, "a path/intent whitelist needs real per-open notification decisions, not a static profile");
        foreach (var (id, matcher) in rules.Blacklist)
        {
            if (matcher.Path is null && matcher.Intent == "any") continue;
            if (run is null && (matcher.Tool is not null || matcher.Dialect is not null || matcher.Args.Count != 0))
                throw Unsupported(id, "a long-lived process without invocation context cannot compile invocation-dependent file restrictions");
            if (PermissionEngine.MatchFields(matcher, input, static path => PinnedFileAccess.Canonicalize(path), omitFileDimensions: true) is null)
                continue;
            if (matcher.Intent != "write" || matcher.Path is null)
                throw Unsupported(id, "this backend has no per-open read/intent callback");
            var pattern = PermissionEngine.NormalizePattern(matcher.Path, cwd, static path => PinnedFileAccess.Canonicalize(path));
            if (pattern.IndexOfAny(['*', '?']) >= 0)
                throw Unsupported(id, "glob restrictions cannot be represented by a concrete static OS profile");
            if (!File.Exists(pattern))
                throw Unsupported(id, "the existing backend can protect only existing concrete regular-file write denials");
            denied.Add(pattern);
        }
        return denied;
    }

    internal static void DemandUnconfined(PermissionRules rules, string surface, bool explicitHostGrant = false)
    {
        if (!rules.AutoApprove && !explicitHostGrant || SettingsPolicyActivation.NeedsPerFileAuthority(rules))
            throw new NotSupportedException($"{surface} has no host-controlled file mediation or OS profile. The configured policy requires that boundary; use mediated tools or an owned isolated worker. No execution occurred.");
    }

    private static NotSupportedException Unsupported(string id, string reason)
        => new($"Permission rule '{id}' cannot be enforced for opaque execution: {reason}. Use mediated host tools or plugin broker operations. No process was started.");
}
