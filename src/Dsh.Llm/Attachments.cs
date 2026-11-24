namespace Dsh.Llm;

/** 图片/附件字节的持久化抽象: 内容寻址 id 解析为字节。 */
public interface IAttachmentStore
{
    ImageAttachmentRef Put(byte[] bytes, string mediaType, int width, int height, string? name = null);

    /** 任意二进制按内容寻址落盘(不做图片校验), 返回 id 与本地文件路径。 */
    StoredAttachment PutBytes(byte[] bytes);

    bool TryRead(string attachmentId, out byte[] bytes);
}

public readonly record struct StoredAttachment(string AttachmentId, string Path);

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

/**
 * 附件字节已经不在了(被删/搬迁/副本比原件旧): 请求期把那张图换成一段文本占位, 会话本身一个字都不改。
 *
 * 以前是直接抛 ATTACHMENT_MISSING, 结果是整条会话再也发不出消息 —— 图看不了, 但上下文与后续对话不该跟着废掉。
 * 与 TextOnlyImages 的区别只在理由: 那个是模型不吃图, 这个是图已经不存在。
 */
public static class MissingImages
    {
    /** The notice the model sees instead of one image whose bytes are gone. */
    public static string OmittedText(ImageAttachmentRef attachment)
        => $"[image omitted because its bytes are no longer stored; attachment sha256:{attachment.AttachmentId[..Math.Min(TextOnlyImages.DigestPrefixLength, attachment.AttachmentId.Length)]}]";

    /** 返回把"引用了但拿不到字节"的图片换成占位的请求; 没有这种图片时原样返回。 */
    public static GenerateOptions Project(GenerateOptions options, Action<string>? warn)
    {
        if (!options.Messages.Any(message => ToolResultImages.Any(message.Content)))
            return options;
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var projected = options.Messages.Select(message => ToolResultImages.Any(message.Content)
            ? message with { Content = Replace(message.Content, options.Attachments, missing) }
            : message).ToList();
        if (missing.Count == 0)
            return options;
        warn?.Invoke($"request carries {missing.Count} image attachment(s) whose bytes are no longer stored; they are sent as text placeholders: {string.Join(", ", missing)}");
        return options.WithMessages(projected);
    }

    private static IReadOnlyList<ContentBlock> Replace(
        IReadOnlyList<ContentBlock> blocks,
        IReadOnlyDictionary<string, byte[]>? attachments,
        HashSet<string> missing)
        => [.. blocks.Select(block => block switch
        {
            ImageBlock image when attachments is null || !attachments.ContainsKey(image.Attachment.AttachmentId)
                => Missing(image, missing),
            ToolResultBlock result when ToolResultImages.Any(result.Content)
                => result with { Content = Replace(result.Content, attachments, missing) },
            _ => block,
        })];

    private static TextBlock Missing(ImageBlock image, HashSet<string> missing)
    {
        missing.Add(image.Attachment.AttachmentId);
        return new TextBlock(OmittedText(image.Attachment));
    }
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

/**
 * Chat Completions 系协议(OpenAI 兼容、DeepSeek)的 tool 消息只能承载文本。
 * 工具结果里的图片因此改由紧跟在一串连续工具结果之后的一条 user 消息携带, 并标明来自哪次工具调用。
 * 并行调用的结果各自是一条消息, 图片必须等整串结束再放, 否则 user 消息会插进 tool 消息之间被端点拒绝。
 */
public static class ToolResultImages
{
    /** 内容(含嵌套的工具结果)里是否带图片。 */
    public static bool Any(IReadOnlyList<ContentBlock> blocks)
        => blocks.Any(block => block is ImageBlock || (block is ToolResultBlock result && Any(result.Content)));

    /** 返回在每串连续工具结果消息之后插入了图片转发消息的列表; 没有工具结果图片时原样返回。 */
    public static IReadOnlyList<Message> Relay(IReadOnlyList<Message> messages)
    {
        if (!messages.Any(message => message.Content.Any(block => block is ToolResultBlock result && Any(result.Content))))
            return messages;
        var relayed = new List<Message>(messages.Count + 1);
        List<ContentBlock>? pending = null;
        foreach (var message in messages)
        {
            var results = message.Content.OfType<ToolResultBlock>().ToList();
            if (results.Count == 0 && pending is not null)
            {
                relayed.Add(MessageFactory.CreateUserMessage(pending));
                pending = null;
            }
            relayed.Add(message);
            foreach (var result in results.Where(result => result.Content.Any(block => block is ImageBlock)))
            {
                pending ??= [];
                pending.Add(new TextBlock($"Image output of tool call {result.ToolCallId.Value}:\n"));
                pending.AddRange(result.Content.OfType<ImageBlock>());
            }
        }
        if (pending is not null)
            relayed.Add(MessageFactory.CreateUserMessage(pending));
        return relayed;
    }
}

/**
 * A model known to accept only text cannot receive image blocks, even ones left in the history by an earlier image-capable route.
 * Requests to such a model carry a short notice in place of every image, including images nested in tool results; the session keeps the images.
 */
public static class TextOnlyImages
{
    private const string ImageModality = "image";
    internal const int DigestPrefixLength = 8;

    /** The notice the model sees instead of one image. */
    public static string OmittedText(ImageAttachmentRef attachment)
        => $"[image omitted because this model accepts text only; attachment sha256:{attachment.AttachmentId[..Math.Min(DigestPrefixLength, attachment.AttachmentId.Length)]}]";

    /** Returns the request unchanged unless the model is known to lack image input and the messages carry images. */
    public static GenerateOptions Project(GenerateOptions options, IReadOnlyList<string>? inputModalities)
    {
        if (inputModalities is null
            || inputModalities.Contains(ImageModality)
            || !options.Messages.Any(message => ToolResultImages.Any(message.Content)))
            return options;
        return options.WithMessages([.. options.Messages.Select(message => ToolResultImages.Any(message.Content)
            ? message with { Content = Replace(message.Content) }
            : message)]);
    }

    private static IReadOnlyList<ContentBlock> Replace(IReadOnlyList<ContentBlock> blocks)
        => [.. blocks.Select(block => block switch
        {
            ImageBlock image => new TextBlock(OmittedText(image.Attachment)),
            ToolResultBlock result when ToolResultImages.Any(result.Content) => result with { Content = Replace(result.Content) },
            _ => block,
        })];
}
