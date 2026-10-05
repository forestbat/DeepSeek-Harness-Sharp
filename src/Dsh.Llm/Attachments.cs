namespace Dsh.Llm;

/** 图片/附件字节的持久化抽象: 内容寻址 id 解析为字节。 */
public interface IAttachmentStore
{
    ImageAttachmentRef Put(byte[] bytes, string mediaType, int width, int height, string? name = null);

    bool TryRead(string attachmentId, out byte[] bytes);
}

/** 支持的图片类型识别(魔数嗅探)、扩展名/媒体类型互转。 */
public static class ImageAttachments
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static IReadOnlyList<string> SupportedExtensions { get; } = ["png", "jpg", "jpeg", "gif", "webp", "avif"];

    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        if (bytes.Length >= 12 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            var brand = System.Text.Encoding.ASCII.GetString(bytes[8..12].ToArray());
            if (brand is "avif" or "avis")
                return "image/avif";
        }
        return null;
    }

    public static string? MediaTypeForExtension(string? extension)
    {
        var normalized = extension?.TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "avif" => "image/avif",
            _ => null,
        };
    }

    public static string ExtensionFor(string mediaType)
        => mediaType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/avif" => "avif",
            _ => "bin",
        };
}

/** 请求期把 ImageBlock 的引用解析为真实字节; 缺字节时给出可读错误。 */
public static class AttachmentResolution
{
    public static ReadOnlyMemory<byte> Resolve(GenerateOptions options, ImageAttachmentRef attachment)
        => options.Attachments is { } map && map.TryGetValue(attachment.AttachmentId, out var bytes)
            ? bytes
            : throw new LlmException(new LlmFailure(
                $"image attachment \"{attachment.AttachmentId}\" is missing its bytes",
                "ATTACHMENT_MISSING"));

    public static string DataUrl(GenerateOptions options, ImageAttachmentRef attachment)
        => $"data:{attachment.MediaType};base64,{Convert.ToBase64String(Resolve(options, attachment).Span)}";
}

/** 从消息里收集 ImageBlock 引用的字节, 供请求期注入 GenerateOptions.Attachments。 */
public static class AttachmentHydration
{
    public static IReadOnlyDictionary<string, byte[]>? Collect(IReadOnlyList<Message> messages, IAttachmentStore? store)
    {
        if (store is null)
            return null;
        Dictionary<string, byte[]>? map = null;
        foreach (var message in messages)
            CollectBlocks(message.Content, store, ref map);
        return map;
    }

    private static void CollectBlocks(IReadOnlyList<ContentBlock> blocks, IAttachmentStore store, ref Dictionary<string, byte[]>? map)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ImageBlock image when (map is null || !map.ContainsKey(image.Attachment.AttachmentId))
                    && store.TryRead(image.Attachment.AttachmentId, out var bytes):
                    (map ??= [])[image.Attachment.AttachmentId] = bytes;
                    break;
                case ToolResultBlock result:
                    CollectBlocks(result.Content, store, ref map);
                    break;
            }
        }
    }
}
