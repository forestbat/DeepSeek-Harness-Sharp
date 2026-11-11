using System.Globalization;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;
using Dsh.Agent;
using Dsh.Safety;

namespace Dsh.Tools;

public sealed record ReadImageCaps
{
    /** 源文件字节上限: 超过即拒绝, 不读入内存。 */
    public long MaxSourceBytes { get; init; } = 20 * 1024 * 1024;

    /** 源图像素上限: 解码前按文件头拦截, 防止解压炸弹。 */
    public long MaxSourcePixels { get; init; } = 64_000_000;

    /** 交给模型的长边上限; 2000 同时满足 Anthropic 单请求超过 20 张图时的 2000x2000 限制。 */
    public int MaxLongEdge { get; init; } = 2000;

    /** 落进附件库的字节上限, 默认与附件库自身上限一致。 */
    public int MaxStoredBytes { get; init; } = ImageAttachments.MaxBytes;
}

public readonly record struct PixelSize(int Width, int Height);

/** Original 只在降采样后出现, 记录已按 EXIF 方向摆正的源图尺寸。 */
public sealed record ReadImageValue(string Path, ImageAttachmentRef Image, PixelSize? Original = null);

/** read_image: 读本地图片, 按需降采样后存进内容寻址附件库, 把图片本身作为工具结果交给模型。 */
public static class ReadImageTool
{
    public const string ToolName = "read_image";

    private const string PngMediaType = "image/png";
    private const string JpegMediaType = "image/jpeg";
    private const string ImageModality = "image";

    private const string SectionText = "Use read_image — not read or shell commands — to look at an image file such as a screenshot, diagram, photo or rendered chart; the image itself is returned to you. Large images are downscaled automatically, so do not create thumbnails or install image libraries just to inspect an image.";

    private static readonly int[] JpegQualities = [90, 75, 60, 45];

    private static readonly DecoderOptions FirstFrameOnly = new() { MaxFrames = 1 };

    private static readonly string AcceptedFormats = string.Join('/', Configuration.Default.ImageFormats.Select(format => format.Name));

    public static IDisposable Register(Context ctx, ReadImageCaps? caps = null)
    {
        var resolved = caps ?? new ReadImageCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:read_image", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            MediatesFileAccess = true,
            Description = $"Read a local image file ({AcceptedFormats}) and return the image itself to the model. Images whose long edge exceeds {resolved.MaxLongEdge}px are downscaled automatically; the result states the original size and the coordinate multiplier.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["file_path"] = ToolSchemas.StringParam("Path to the image file, resolved like the read tool (relative paths use the session working directory). Extension-less files are identified by their content."),
                },
                "file_path"),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path", "image"],
                      "properties": {
                        "path": { "type": "string" },
                        "image": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["attachmentId", "mediaType", "bytes", "width", "height"],
                          "properties": {
                            "attachmentId": { "type": "string" },
                            "mediaType": { "type": "string" },
                            "bytes": { "type": "integer" },
                            "width": { "type": "integer" },
                            "height": { "type": "integer" },
                            "name": { "type": "string" }
                          }
                        },
                        "original": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["width", "height"],
                          "properties": {
                            "width": { "type": "integer" },
                            "height": { "type": "integer" }
                          }
                        }
                      }
                    }
                    """),
                (_, value) => Render(value)),
            Execute = (args, exec) => Execute(ctx, args, exec, resolved),
            IsConcurrencySafe = _ => true,
        });
        return new CompositeDisposable(section, registration);
    }

    private static async Task<object?> Execute(Context ctx, JsonElement args, ToolRunContext exec, ReadImageCaps caps)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        if (filePath.Trim().Length == 0)
            throw new ArgumentException("file_path must be a non-empty string");
        var extension = Path.GetExtension(filePath);
        if (extension.Length > 0 && !Configuration.Default.ImageFormatsManager.TryFindFormatByFileExtension(extension, out _))
            throw new HarnessException($"cannot read \"{filePath}\": the {extension} extension is not an image format; read_image accepts {AcceptedFormats} files (extension-less files are identified by content); use read for text files", "UNSUPPORTED_IMAGE");
        var store = ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false)
            ?? throw new HarnessException($"cannot read \"{filePath}\" as an image: no attachment store is mounted", "ATTACHMENTS_UNAVAILABLE");
        AssertRouteAcceptsImages(ctx, exec, filePath);
        var target = WorkspacePath.ResolveExistingFile(exec, filePath);
        var length = new FileInfo(target).Length;
        if (length > caps.MaxSourceBytes)
            throw new HarnessException($"cannot read \"{target}\": the file is {length} bytes, which exceeds the {caps.MaxSourceBytes}-byte read_image limit; downscale the image and read the smaller copy", "FS_TOO_LARGE");
        var source = await SafetyFileAccess.ReadBytesAsync(target, exec, caps.MaxSourceBytes).ConfigureAwait(false);
        var normalized = await Task.Run(() => Normalize(source, target, caps), exec.Signal);
        var reference = store.Put(normalized.Bytes, normalized.MediaType, normalized.Size.Width, normalized.Size.Height, Path.GetFileName(target));
        return new ReadImageValue(target, reference, normalized.Original);
    }

    /** 只在路由明确声明不接收图片时拒绝; 未声明模态的路由(如中转站)交给 provider 自己判定。 */
    private static void AssertRouteAcceptsImages(Context ctx, ToolRunContext exec, string requestedPath)
    {
        var routed = exec.Agent?.Session.RequestHeader()?.Config;
        var provider = routed?.Provider ?? exec.Agent?.Options.Provider;
        var model = routed?.Model ?? exec.Agent?.Options.Model;
        if (provider is null || model is null || ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false) is not { } llm)
            return;
        if (llm.ResolveModelInfo(provider, model).InputModalities is { } modalities && !modalities.Contains(ImageModality))
            throw new HarnessException($"cannot read \"{requestedPath}\" as an image: model \"{model}\" does not accept image input; switch to an image-capable model to read images", "UNSUPPORTED_CONTENT");
    }

    private sealed record NormalizedImage(byte[] Bytes, string MediaType, PixelSize Size, PixelSize? Original);

    /** 小于上限、方向正常的 PNG/JPEG 原样保存(与用户粘贴的同一文件共享内容寻址); 其余解码、摆正、按长边缩放后重新编码。 */
    private static NormalizedImage Normalize(byte[] source, string target, ReadImageCaps caps)
    {
        var info = Identify(source, target);
        if ((long)info.Width * info.Height > caps.MaxSourcePixels)
            throw new HarnessException($"cannot read \"{target}\": the image is {info.Width}x{info.Height} px, which exceeds the {caps.MaxSourcePixels}-pixel decode limit; downscale the image and read the smaller copy", "IMAGE_TOO_LARGE");
        var mediaType = info.Metadata.DecodedImageFormat?.DefaultMimeType;
        var upright = info.Metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var orientation) != true
            || orientation?.Value is null or ExifOrientationMode.TopLeft or ExifOrientationMode.Unknown;
        if (mediaType is PngMediaType or JpegMediaType && upright
            && Math.Max(info.Width, info.Height) <= caps.MaxLongEdge && source.Length <= caps.MaxStoredBytes)
            return new NormalizedImage(source, mediaType, new PixelSize(info.Width, info.Height), null);
        using var image = Image.Load(FirstFrameOnly, source);
        image.Mutate(context => context.AutoOrient());
        var oriented = new PixelSize(image.Width, image.Height);
        if (Math.Max(image.Width, image.Height) > caps.MaxLongEdge)
            image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(caps.MaxLongEdge, caps.MaxLongEdge), Mode = ResizeMode.Max }));
        var (bytes, encodedType) = Encode(image, mediaType == JpegMediaType, caps.MaxStoredBytes, target);
        var size = new PixelSize(image.Width, image.Height);
        return new NormalizedImage(bytes, encodedType, size, size == oriented ? null : oriented);
    }

    private static ImageInfo Identify(byte[] source, string target)
    {
        try
        {
            return Image.Identify(source);
        }
        catch (Exception error) when (error is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new HarnessException($"cannot read \"{target}\": the file content is not a supported image format; read_image accepts {AcceptedFormats}", "UNSUPPORTED_IMAGE", error);
        }
    }

    /** 非照片源优先无损 PNG; 超出字节上限(或源本身是 JPEG)时铺白底后按质量阶梯编码 JPEG。 */
    private static (byte[] Bytes, string MediaType) Encode(Image image, bool photographic, int maxBytes, string target)
    {
        if (!photographic && EncodeWith(image, new PngEncoder()) is { } png && png.Length <= maxBytes)
            return (png, PngMediaType);
        image.Mutate(context => context.BackgroundColor(Color.White));
        foreach (var quality in JpegQualities)
        {
            var jpeg = EncodeWith(image, new JpegEncoder { Quality = quality });
            if (jpeg.Length <= maxBytes)
                return (jpeg, JpegMediaType);
        }
        throw new HarnessException($"cannot read \"{target}\": the image cannot be stored within the {maxBytes}-byte attachment limit even after downscaling; downscale it further and read the smaller copy", "IMAGE_TOO_LARGE");
    }

    private static byte[] EncodeWith(Image image, IImageEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static IReadOnlyList<ContentBlock> Render(JsonElement value)
    {
        var result = DshJson.Deserialize<ReadImageValue>(value)
            ?? throw new JsonException("read_image result value is malformed");
        return [new TextBlock(FormatEnvelope(result)), new ImageBlock(result.Image)];
    }

    /** 与 read 同形的信封; 降采样时写明源尺寸与坐标换算倍数, 便于模型回到原图定位。 */
    private static string FormatEnvelope(ReadImageValue result)
    {
        var image = result.Image;
        var scaled = "";
        if (result.Original is { } original)
        {
            var x = ((double)original.Width / image.Width).ToString("F2", CultureInfo.InvariantCulture);
            var y = ((double)original.Height / image.Height).ToString("F2", CultureInfo.InvariantCulture);
            var advice = x == y ? $"multiply coordinates by {x}" : $"multiply x coordinates by {x} and y coordinates by {y}";
            scaled = $" (downscaled from {original.Width}x{original.Height} px; {advice} to locate features in the original file)";
        }
        return $"<path>{result.Path}</path>\n<type>image</type>\n<content>\n{image.MediaType} image, {image.Width}x{image.Height} px, {image.Bytes} bytes{scaled}\n</content>";
    }
}
