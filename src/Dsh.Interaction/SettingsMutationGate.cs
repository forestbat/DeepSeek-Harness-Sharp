using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Runtime.Logging;

namespace Dsh.Interaction;

/** The single settings approval, publication and redacted audit service. */
public sealed class SettingsMutationGate : IDisposable
{
    private readonly Context context;
    private readonly HarnessHome home;
    private readonly EffectHandle registration;

    public SettingsMutationGate(Context context, HarnessHome home)
    {
        this.context = context;
        this.home = home;
        registration = context.Provide(SettingsMutation.ServiceName,
            (Func<SettingsMutationRequest, Func<string, CancellationToken, Task>, CancellationToken, Task>)CommitAsync);
    }

    public void Dispose() => registration.Dispose();

    public string SettingsPath => CanonicalFilePath.Canonicalize(home.SettingsFile);
    public bool Owns(string path) => CanonicalFilePath.SameFile(path, home.SettingsFile);

    public async Task CommitAsync(SettingsMutationRequest request, Func<string, CancellationToken, Task> commit, CancellationToken cancellationToken = default)
    {
        var run = request.Context;
        var id = Guid.NewGuid().ToString("N");
        var target = SettingsPath;
        var diff = CreateDiff(request.Before, request.After);
        var beforeHash = Hash(request.Before);
        var afterHash = Hash(request.After);
        var outcome = "failed";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Signal);
        try
        {
            var approval = CreateApprovalRequest(request, target, diff, beforeHash, afterHash);
            var safety = SafetyBoundary.Resolve(context)
                ?? throw new InvalidOperationException("Settings mutations require the active safety boundary.");
            await safety.AuthorizeFileAsync(SafetyBoundary.SafetyNamespace, run, target, "write", linked.Token,
                settings: new SettingsApproval(approval)).ConfigureAwait(false);
            await HarnessSettings.CommitApprovedMutationAsync(target, request.Before, request.After, async (temporary, signal) =>
            {
                if (target != SettingsPath || !Owns(request.Path))
                    throw new InvalidOperationException("The active settings path changed while approval was pending.");
                safety.RevalidateFile(SafetyBoundary.SafetyNamespace, run, target, "write", signal);
                await commit(temporary, signal).ConfigureAwait(false);
            }, linked.Token).ConfigureAwait(false);
            run.RecordFileChange(target);
            outcome = "committed";
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            outcome = "rejected";
            throw;
        }
        finally
        {
            run.RecordSettingsMutation(new(id, run.CallId, run.RootCallIdValue, run.Name, target, diff, beforeHash, afterHash, outcome), context);
        }
    }

    private ApprovalRequest CreateApprovalRequest(SettingsMutationRequest request, string target, string diff, string beforeHash, string afterHash)
    {
        if (!Owns(request.Path))
            throw new InvalidOperationException("The mutation does not target the active settings document.");
        var run = request.Context;
        var agent = run.Agent ?? throw new InvalidOperationException("Settings mutations require an active model session.");
        _ = context.Get<IApprovalService>("approval", false)
            ?? throw new InvalidOperationException("Settings mutations require the active approval service.");
        var arguments = new JsonObject
        {
            ["settingsMutation"] = true, ["path"] = target, ["diff"] = diff,
            ["beforeHash"] = beforeHash, ["afterHash"] = afterHash,
        };
        return new(agent, run.Name, run.CallId,
            "Change active settings for all sessions. Review the redacted changed lines and document hashes.", arguments.ToJsonString())
            { RuleArguments = run.Arguments };
    }

    private static string CreateDiff(string before, string after)
    {
        var lines = DiffCardExtractor.DiffLines(Redaction.Apply(before), Redaction.Apply(after));
        var patch = new StringBuilder("--- settings.yaml\n+++ settings.yaml\n");
        var oldLine = 1;
        var newLine = 1;
        for (var index = 0; index < lines.Count;)
        {
            if (lines[index].Kind == DiffLineKind.Context)
            {
                oldLine++;
                newLine++;
                index++;
                continue;
            }
            var start = index;
            var oldStart = oldLine;
            var newStart = newLine;
            while (index < lines.Count && lines[index].Kind != DiffLineKind.Context)
            {
                if (lines[index++].Kind == DiffLineKind.Delete) oldLine++; else newLine++;
            }
            var removed = oldLine - oldStart;
            var added = newLine - newStart;
            patch.AppendLine($"@@ -{(removed == 0 ? oldStart - 1 : oldStart)},{removed} +{(added == 0 ? newStart - 1 : newStart)},{added} @@");
            for (var row = start; row < index; row++)
                patch.Append(lines[row].Kind == DiffLineKind.Delete ? '-' : '+').AppendLine(lines[row].Text);
        }
        return patch.ToString();
    }

    private static string Hash(string text)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(text));
        try
        {
            var length = Encoding.UTF8.GetBytes(text.AsSpan(), bytes);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(bytes.AsSpan(0, length), digest);
            return Convert.ToHexString(digest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }
}
