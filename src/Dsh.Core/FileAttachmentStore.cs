using System.Security.Cryptography;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Core;

/** 附件字节以内容寻址(sha256)落盘在 <harness home>/attachments; 幂等去重, 支持会话 resume 后按 id 重新解析。 */
public sealed class FileAttachmentStore : Service, IAttachmentStore
{
    public const string ServiceName = "attachments";

    private readonly string _root;

    public FileAttachmentStore(Context ctx)
        : this(ctx, ResolveHome(ctx))
    {
    }

    public FileAttachmentStore(Context ctx, string home)
        : base(ctx, ServiceName)
        => _root = Path.Combine(home, "attachments");

    public ImageAttachmentRef Put(byte[] bytes, string mediaType, int width, int height, string? name = null)
    {
        if (bytes.Length == 0)
            throw new ArgumentException("attachment is empty");
        if (bytes.Length > ImageAttachments.MaxBytes)
            throw new ArgumentException($"attachment exceeds {ImageAttachments.MaxBytes} bytes");
        var sniffed = ImageAttachments.Sniff(bytes)
            ?? throw new ArgumentException("unsupported image format (expected png/jpeg/gif/webp/avif)");
        if (!string.Equals(sniffed, mediaType, StringComparison.OrdinalIgnoreCase))
            mediaType = sniffed;
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, bytes);
            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temp);
            }
        }
        return new ImageAttachmentRef(id, mediaType, bytes.Length, width, height, name);
    }

    public bool TryRead(string attachmentId, out byte[] bytes)
    {
        bytes = [];
        if (attachmentId.Length != 64 || !attachmentId.All(Uri.IsHexDigit))
            return false;
        var path = PathFor(attachmentId.ToLowerInvariant());
        if (!File.Exists(path))
            return false;
        bytes = File.ReadAllBytes(path);
        return true;
    }

    private string PathFor(string id) => Path.Combine(_root, id[..2], id);

    private static string ResolveHome(Context ctx)
        => ctx.GetProp("dshHomePath") is string { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
}
