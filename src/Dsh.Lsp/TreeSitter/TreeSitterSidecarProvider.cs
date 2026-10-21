using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.TreeSitter;

public interface ISyntaxTreeProvider
{
    string Name { get; }

    bool SupportsIncremental => false;

    Task<CstNode?> TryParseAsync(LspDocument document, CancellationToken cancellationToken = default);
}

/**
 * Optional Tree-sitter backend. It launches a short-lived Node sidecar for one
 * parse, so a broken grammar cannot poison the long-lived DSH process.
 */
public sealed class TreeSitterSidecarProvider(TreeSitterOptions options) : ISyntaxTreeProvider
{
    private const string DriverResource = "Dsh.Lsp.TreeSitter.sidecar.cjs";
    private readonly ConcurrentDictionary<string, (string Text, CstNode Cst)> _cache = new(StringComparer.Ordinal);

    public string Name => "tree-sitter";

    public string? LastError { get; private set; }

    public async Task<CstNode?> TryParseAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
    {
        LastError = null;
        if (!options.Allows(document.LanguageId))
            return null;
        if (_cache.TryGetValue(document.Uri, out var cached) && cached.Text == document.Text)
            return cached.Cst;
        if (Encoding.UTF8.GetByteCount(document.Text) > options.MaxFileBytes)
        {
            LastError = $"file exceeds Tree-sitter limit of {options.MaxFileBytes} bytes";
            return null;
        }

        var grammar = GrammarFor(document.LanguageId);
        if (grammar is null || !File.Exists(grammar))
        {
            LastError = $"no Tree-sitter grammar configured for '{document.LanguageId}'";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.RuntimePath))
        {
            LastError = "Tree-sitter RuntimePath is not configured";
            return null;
        }

        var driver = EnsureDriver();
        var request = new JsonObject
        {
            ["language"] = document.LanguageId,
            ["grammar"] = Path.GetFullPath(
                Path.IsPathRooted(grammar)
                    ? grammar
                    : Path.Combine(options.RuntimePath!, grammar)),
            ["runtimePath"] = Path.GetFullPath(options.RuntimePath),
            ["source"] = document.Text,
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = options.NodePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(driver);
        using var process = new Process();
        process.StartInfo = startInfo;
        try
        {
            if (!process.Start())
            {
                LastError = "failed to start Tree-sitter sidecar";
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(request.ToJsonString().AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
            var timeout = options.Timeout ?? TimeSpan.FromSeconds(20);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                LastError = error.Length > 0 ? error.Trim() : "Tree-sitter sidecar returned no output";
                return null;
            }

            using var json = JsonDocument.Parse(output);
            if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                LastError = json.RootElement.TryGetProperty("error", out var message)
                    ? message.GetString()
                    : "Tree-sitter sidecar failed";
                return null;
            }

            if (!json.RootElement.TryGetProperty("root", out var root))
                return null;
            var byteMap = Utf8ByteMap.Build(document.Text);
            var parsed = ParseNode(root, byteMap, document, 0);
            if (parsed is not null)
                _cache[document.Uri] = (document.Text, parsed);
            return parsed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception killError)
            {
                _ = killError;
            }

            return null;
        }
    }

    private string? GrammarFor(string languageId)
    {
        if (options.GrammarPaths is null)
            return null;
        if (options.GrammarPaths.TryGetValue(languageId, out var exact))
            return exact;
        foreach (var (language, path) in options.GrammarPaths)
        {
            if (language.Equals(languageId, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    private static CstNode? ParseNode(
        JsonElement item,
        IReadOnlyList<int> byteMap,
        LspDocument document,
        int depth)
    {
        if (depth > 512 || item.ValueKind != JsonValueKind.Object)
            return null;
        var startByte = Math.Clamp(LspJson.Int32(item, "startByte") ?? 0, 0, byteMap.Count - 1);
        var endByte = Math.Clamp(LspJson.Int32(item, "endByte") ?? startByte, startByte, byteMap.Count - 1);
        var children = LspJson.Items(LspJson.Property(item, "children"))
            .Select(child => ParseNode(child, byteMap, document, depth + 1))
            .Where(child => child is not null)
            .Select(child => child!)
            .ToArray();
        return new CstNode(
            LspJson.String(item, "type") ?? "unknown",
            LspJson.String(item, "fieldName"),
            LspJson.Boolean(item, "isNamed"),
            LspJson.Boolean(item, "isMissing"),
            LspJson.Boolean(item, "isExtra"),
            startByte,
            endByte,
            new LspRange(document.PositionAt(byteMap[startByte]), document.PositionAt(byteMap[endByte])),
            children);
    }

    private static string EnsureDriver()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dsh-lsp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tree-sitter-sidecar.cjs");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DriverResource)
            ?? throw new InvalidOperationException($"missing embedded resource {DriverResource}");
        using var output = File.Create(path);
        stream.CopyTo(output);
        return path;
    }
}

internal static class Utf8ByteMap
{
    public static int[] Build(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var map = new int[bytes.Length + 1];
        var byteIndex = 0;
        var charIndex = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            for (var offset = 0; offset < rune.Utf8SequenceLength; offset++)
                map[byteIndex + offset] = charIndex;
            byteIndex += rune.Utf8SequenceLength;
            charIndex += rune.Utf16SequenceLength;
        }

        map[^1] = text.Length;
        return map;
    }
}
