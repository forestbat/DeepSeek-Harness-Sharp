using System.Text.RegularExpressions;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.Services;

/**
 * Images a prompt refers to: image paths written in the prompt are attached and removed from the text, relative paths resolve against the session's directory.
 * Files mentioned by the prompt stay as text; their image paths must be read through the read_image tool.
 * For a session on another machine every file is read from that machine.
 */
public static partial class PromptImages
{
    /** `@<relative or absolute path>` or a standalone absolute path ending in an image extension; `(?<!\S)` keeps `/bar.png` inside `@foo/bar.png` from matching on its own. */
    [GeneratedRegex("""(?<!\S)(?:@(?<m>[^\s@"']*?\.(?:png|jpe?g|gif|webp|avif))|(?<p>(?:[A-Za-z]:[\\/]|/)[^\s@"']*?\.(?:png|jpe?g|gif|webp|avif)))""", RegexOptions.IgnoreCase)]
    public static partial Regex PathPattern();

    public static async Task<(string Text, IReadOnlyList<RemoteImageBlock> Images)> ExtractRemoteAsync(
        RemoteFilesClient files, string cwd, RemotePathStyle style, string text, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var images = new List<RemoteImageBlock>();
        var remaining = text;
        foreach (Match match in PathPattern().Matches(text))
        {
            var token = match.Groups["m"].Success ? match.Groups["m"].Value : match.Groups["p"].Value;
            if (token.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (await TryAddAsync(files, RemotePaths.Resolve(cwd, token, style), style, seen, images, cancellationToken))
                remaining = remaining.Replace(match.Value, " ", StringComparison.Ordinal);
        }
        return (remaining.Trim(), images);
    }

    /** Attaches the readable image (media type by extension) and returns whether it was added. */
    private static async Task<bool> TryAddAsync(
        RemoteFilesClient files, string path, RemotePathStyle style, HashSet<string> seen, List<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (ImageAttachments.MediaTypeForExtension(RemotePaths.Extension(path, style)) is not { } mediaType || !seen.Add(path))
            return false;
        try
        {
            var bytes = await files.ReadFileAsync(path, cancellationToken);
            images.Add(new RemoteImageBlock(mediaType, 0, 0, RemotePaths.FileName(path, style), Convert.ToBase64String(bytes)));
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonRpcException)
        {
            seen.Remove(path);
            return false;
        }
    }
}
