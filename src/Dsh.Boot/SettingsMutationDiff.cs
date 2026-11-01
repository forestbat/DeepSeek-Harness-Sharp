using Dsh.Core;
using Dsh.Runtime;
using Dsh.Runtime.Logging;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Dsh.Boot;

public static class SettingsMutationDiff
{
    private readonly record struct SecretSpan(int Start, int End, string Path, string Value);
    /** Collect secrets from both documents so replacing or removing a credential cannot expose its old value. */
    public static (string Before, string After) Redact(string before, string after)
    {
        if (!SettingsMutationLimits.CanReview(before) || !SettingsMutationLimits.CanReview(after))
            return ("[Settings document exceeds the 4 MiB review limit]", "[Settings document exceeds the 4 MiB review limit]");
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var first = Collect(before, secrets);
        var second = Collect(after, secrets);
        var values = secrets.ToArray();
        return (Mask(before, first, values, null), Mask(after, second, values,
            first.Spans.GroupBy(span => span.Path).ToDictionary(group => group.Key, group => group.First().Value)));
    }

    private static (bool Valid, List<SecretSpan> Spans) Collect(string text, HashSet<string> secrets)
    {
        var spans = new List<SecretSpan>();
        try
        {
            var document = new YamlStream();
            document.Load(new StringReader(text));
            var visited = new Dictionary<YamlNode, bool>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < document.Documents.Count; index++)
                Visit(document.Documents[index].RootNode, false, $"document:{index}", secrets, spans, visited);
            return (true, spans);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return (false, spans);
        }
    }

    private static string Mask(string text, (bool Valid, List<SecretSpan> Spans) document, string[] secrets, Dictionary<string, string>? previous)
    {
        if (!document.Valid)
            return "[Invalid YAML content withheld]";
        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (var span in document.Spans.OrderBy(span => span.Start))
        {
            if (span.Start < position)
                continue;
            result.Append(Redaction.Apply(OutboundProxy.Redact(text[position..span.Start], secrets)));
            var changed = previous is not null && (!previous.TryGetValue(span.Path, out var value) || value != span.Value);
            result.Append(changed ? "\"*** (changed)\"" : "\"***\"");
            if (span.End > span.Start && text[span.End - 1] == '\n')
                result.Append('\n');
            position = span.End;
        }
        result.Append(Redaction.Apply(OutboundProxy.Redact(text[position..], secrets)));
        return result.ToString();
    }

    internal static bool IsSensitiveKey(string key)
    {
        var name = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        return name.Contains("password") || name.Contains("secret") || name.Contains("token")
            || name.Contains("apikey") || name.Contains("accesskey") || name.Contains("connectionstring")
            || name is "key" or "authorization" or "credential" or "credentials" or "privatekey" or "cookie" or "setcookie";
    }

    private static void Visit(YamlNode node, bool sensitive, string path, HashSet<string> secrets, List<SecretSpan> spans, Dictionary<YamlNode, bool> visited)
    {
        if (visited.TryGetValue(node, out var alreadySensitive) && (alreadySensitive || !sensitive))
            return;
        visited[node] = sensitive;
        switch (node)
        {
            case YamlScalarNode { Value: { Length: > 0 } value } when sensitive:
                secrets.Add(value);
                spans.Add(new(checked((int)node.Start.Index), checked((int)node.End.Index), path, value));
                break;
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    var keyText = (key as YamlScalarNode)?.Value ?? "";
                    Visit(value, sensitive || IsSensitiveKey(keyText), $"{path}/{keyText.Length}:{keyText}", secrets, spans, visited);
                }
                break;
            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                    Visit(sequence.Children[index], sensitive, $"{path}/[{index}]", secrets, spans, visited);
                break;
        }
    }
}
