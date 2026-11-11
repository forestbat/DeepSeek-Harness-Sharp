using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Dsh.Agent;

namespace Dsh.Tests;

public partial class ToolsTests
{
    /** read_image: 复用 read 的路径解析, 图片落进内容寻址附件库, 结果带 image block; 大图降采样, 纯文本路由拒绝。 */
    public sealed class ReadImage : IDisposable
    {
        private const string Provider = "vision-test";
        private const string VisionModel = "vision";
        private const string TextOnlyModel = "text-only";
        private const string UndeclaredModel = "undeclared";

        private readonly ToolsTests _outer = new();
        private readonly FileAttachmentStore _store;
        private readonly string _attachmentsDir;

        public ReadImage()
        {
            _attachmentsDir = _outer.TempPath("attachments");
            _store = new FileAttachmentStore(_outer._ctx, _attachmentsDir);
            new LlmRuntime(_outer._ctx).RegisterAdapter([Provider], new ModalityAdapter());
        }

        public void Dispose() => _outer.Dispose();

        private Task<ToolExecutionResult> Read(string path, string model = VisionModel)
            => _outer.Execute(
                ReadImageTool.ToolName,
                JsonSerializer.Serialize(new { file_path = path }, DshJson.Options),
                new FakeAgent(_outer._ctx, _outer._tempDir, new AgentOptions(Provider, model)));

        private string WriteImage(string name, int width, int height, Action<Image<Rgba32>, Stream>? save = null)
        {
            using var image = new Image<Rgba32>(width, height, new Rgba32(30, 120, 200));
            var path = _outer.TempPath(name);
            using var stream = File.Create(path);
            (save ?? ((img, output) => img.SaveAsPng(output)))(image, stream);
            return path;
        }

        private static ImageAttachmentRef AttachmentOf(ToolExecutionResult result)
            => Assert.Single(result.Content.OfType<ImageBlock>()).Attachment;

        [Fact]
        public async Task SmallPng_RelativePath_StoresOriginalBytesAndReturnsImageBlock()
        {
            ReadImageTool.Register(_outer._ctx);
            var path = WriteImage("small.png", 64, 48);

            var result = await Read("small.png");

            Assert.False(result.IsError, TextOf(result));
            var attachment = AttachmentOf(result);
            Assert.Equal(("image/png", 64, 48, "small.png"), (attachment.MediaType, attachment.Width, attachment.Height, attachment.Name));
            Assert.True(_store.TryRead(attachment.AttachmentId, out var stored));
            Assert.Equal(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), stored);
            var text = TextOf(result);
            Assert.Contains($"<path>{path}</path>", text);
            Assert.Contains("<type>image</type>", text);
            Assert.Contains("64x48 px", text);
            Assert.DoesNotContain("downscaled", text);
        }

        [Fact]
        public async Task LargeImage_IsDownscaledToTheLongEdgeCap()
        {
            var caps = new ReadImageCaps();
            ReadImageTool.Register(_outer._ctx, caps);
            WriteImage("wide.png", caps.MaxLongEdge * 2, caps.MaxLongEdge / 2);

            var result = await Read("wide.png");

            Assert.False(result.IsError, TextOf(result));
            var attachment = AttachmentOf(result);
            Assert.Equal((caps.MaxLongEdge, caps.MaxLongEdge / 4), (attachment.Width, attachment.Height));
            Assert.Contains($"downscaled from {caps.MaxLongEdge * 2}x{caps.MaxLongEdge / 2} px; multiply coordinates by 2.00", TextOf(result));
            var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
            Assert.Equal(caps.MaxLongEdge * 2, value.GetProperty("original").GetProperty("width").GetInt32());
        }

        [Fact]
        public async Task OversizedEncoding_FallsBackToJpegWithinTheStoredByteCap()
        {
            var path = WriteImage("noise.png", 256, 256, (image, output) =>
            {
                var random = new Random(7);
                image.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < rows.Height; y++)
                    {
                        foreach (ref var pixel in rows.GetRowSpan(y))
                            pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                    }
                });
                image.SaveAsPng(output);
            });
            var caps = new ReadImageCaps { MaxStoredBytes = (int)(new FileInfo(path).Length / 2) };
            ReadImageTool.Register(_outer._ctx, caps);

            var result = await Read("noise.png");

            Assert.False(result.IsError, TextOf(result));
            var attachment = AttachmentOf(result);
            Assert.Equal("image/jpeg", attachment.MediaType);
            Assert.True(attachment.Bytes <= caps.MaxStoredBytes);
        }

        [Fact]
        public async Task GifWithoutExtension_IsSniffedAndNormalizedToPng()
        {
            ReadImageTool.Register(_outer._ctx);
            WriteImage("animation", 32, 32, (image, output) => image.SaveAsGif(output, new GifEncoder()));

            var result = await Read("animation");

            Assert.False(result.IsError, TextOf(result));
            Assert.Equal("image/png", AttachmentOf(result).MediaType);
        }

        [Theory]
        [InlineData("notes.txt", "hello", "use read for text files")]
        [InlineData("broken.png", "not really a png", "not a supported image")]
        public async Task NonImageContent_FailsWithoutStoringAnything(string name, string content, string expected)
        {
            ReadImageTool.Register(_outer._ctx);
            await File.WriteAllTextAsync(_outer.TempPath(name), content, TestContext.Current.CancellationToken);

            var result = await Read(name);

            Assert.True(result.IsError);
            Assert.Contains(expected, TextOf(result));
            Assert.False(Directory.Exists(_attachmentsDir));
        }

        [Fact]
        public async Task MissingFile_ReusesTheReadToolNotFoundError()
        {
            ReadImageTool.Register(_outer._ctx);

            var result = await Read("absent.png");

            var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
            Assert.Equal("FS_NOT_FOUND", failure.Error.Info?.Code);
        }

        [Fact]
        public async Task SourceAboveByteCap_IsRefusedBeforeDecoding()
        {
            ReadImageTool.Register(_outer._ctx, new ReadImageCaps { MaxSourceBytes = 64 });
            WriteImage("big.png", 64, 64);

            var result = await Read("big.png");

            Assert.True(result.IsError);
            Assert.Contains("exceeds the 64-byte read_image limit", TextOf(result));
        }

        [Fact]
        public async Task TextOnlyRoute_IsRefusedBeforeAnyFileWork()
        {
            ReadImageTool.Register(_outer._ctx);
            WriteImage("shot.png", 16, 16);

            var result = await Read("shot.png", TextOnlyModel);

            Assert.True(result.IsError);
            Assert.Contains($"model \"{TextOnlyModel}\" does not accept image input", TextOf(result));
            Assert.False(Directory.Exists(_attachmentsDir));
        }

        [Fact]
        public async Task UndeclaredModalities_AreLeftToTheProvider()
        {
            ReadImageTool.Register(_outer._ctx);
            WriteImage("shot.png", 16, 16);

            var result = await Read("shot.png", UndeclaredModel);

            Assert.False(result.IsError, TextOf(result));
        }

        /** 只声明模态的适配器: vision 带 image, text-only 只有 text, undeclared 不声明。 */
        private sealed class ModalityAdapter : LlmAdapter
        {
            public override LlmProviderInfo ProviderInfo { get; } = new(Provider, Provider);

            public override ResolvedRetryPolicy ProviderRetryPolicy { get; } = ResolvedRetryPolicy.Resolve(null, Provider);

            public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
                => throw new NotSupportedException("read_image tests never stream");

            public override LlmResolvedModelInfo ResolveModel(string model)
                => new(Provider, model, model, InputModalities: model switch
                {
                    VisionModel => ["text", "image"],
                    TextOnlyModel => ["text"],
                    _ => null,
                });
        }
    }
}
