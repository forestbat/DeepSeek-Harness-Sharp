using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;
using Dsh.Runtime;
using Message = Dsh.Llm.Message;

namespace Dsh.Tests;

public sealed class AttachmentTests : IDisposable
{
    private const string VisionModel = "vision-model";
    private const string TextOnlyModel = "text-model";

    private readonly Context _ctx = new();
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"dsh-attachments-{Guid.NewGuid():N}");
    private readonly FileAttachmentStore _store;

    public AttachmentTests()
    {
        Directory.CreateDirectory(_home);
        _store = new FileAttachmentStore(_ctx, Path.Combine(_home, "attachments"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03];

    [Fact]
    public void Put_IsContentAddressedAndRoundTrips()
    {
        var bytes = PngBytes();
        var first = _store.Put(bytes, "image/png", 4, 2, "shot.png");
        var second = _store.Put(bytes, "image/png", 4, 2, "shot.png");

        Assert.Equal(first.AttachmentId, second.AttachmentId);
        Assert.Equal(bytes.Length, first.Bytes);
        Assert.Equal("image/png", first.MediaType);
        Assert.True(_store.TryRead(first.AttachmentId, out var read));
        Assert.Equal(bytes, read);
    }

    [Fact]
    public void Put_RejectsNonImageBytes()
        => Assert.Throws<ArgumentException>(() => _store.Put([0x00, 0x01, 0x02, 0x03], "image/png", 1, 1));

    [Fact]
    public void TryRead_RejectsMalformedIds()
    {
        Assert.False(_store.TryRead($"{new string('a', 60)}..", out _));
        Assert.False(_store.TryRead("../../etc/passwd", out _));
        Assert.False(_store.TryRead(new string('a', 64), out _));
    }

    [Fact]
    public void Sniff_DetectsPngAndRejectsUnknown()
    {
        Assert.Equal("image/png", ImageAttachments.Sniff(PngBytes()));
        Assert.Null(ImageAttachments.Sniff([0, 1, 2, 3]));
    }

    [Fact]
    public void Hydration_CollectsImageBytesFromMessages()
    {
        var reference = _store.Put(PngBytes(), "image/png", 1, 1);
        Message[] messages =
        [
            new()
            {
                Id = MessageFactory.NewId(),
                Role = MessageRole.User,
                Content = [new ImageBlock(reference)],
            },
        ];

        var attachments = AttachmentHydration.Collect(messages, _store);

        Assert.NotNull(attachments);
        Assert.True(attachments!.ContainsKey(reference.AttachmentId));
    }

    [Fact]
    public void DeepSeek_SerializesImageAsDataUrlPart()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1);
        var options = new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = "deepseek-v4-flash",
            Messages =
            [
                new Message
                {
                    Id = MessageFactory.NewId(),
                    Role = MessageRole.User,
                    Content = [new TextBlock("what is this"), new ImageBlock(reference)],
                },
            ],
            Attachments = new Dictionary<string, byte[]> { [reference.AttachmentId] = bytes },
        };

        var wire = WireSerialize.SerializeMessages(options);
        var node = JsonNode.Parse(JsonSerializer.Serialize(wire[0], DeepSeekWireJsonContext.Default.WireMessage))!;
        var content = node["content"]!.AsArray();

        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void DeepSeek_RelaysToolResultImagesAfterTheToolMessage()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1);
        var options = ToolResultImageOptions("deepseek-v4-flash-vision-exp", reference, bytes);

        var wire = WireSerialize.SerializeMessages(options)
            .Select(message => JsonNode.Parse(JsonSerializer.Serialize(message, DeepSeekWireJsonContext.Default.WireMessage))!)
            .ToList();

        Assert.Equal(["user", "assistant", "tool", "tool", "user"], wire.Select(message => message["role"]!.GetValue<string>()));
        Assert.Equal("envelope", wire[2]["content"]!.GetValue<string>());
        var relayed = wire[4]["content"]!.AsArray();
        Assert.Contains("call_1", relayed[0]!["text"]!.GetValue<string>());
        Assert.Equal(AttachmentResolution.DataUrl(options, reference), relayed[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeepSeek_VisionModelSendsToolResultImages()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1);
        var handler = new DeepSeekCapture();

        await foreach (var _ in DeepSeekAdapterFor(handler).Stream(ToolResultImageOptions(VisionModel, reference, bytes), CancellationToken.None))
        {
        }

        Assert.Contains("image_url", handler.Body);
    }

    [Fact]
    public async Task DeepSeek_TextOnlyModelRejectsImagesNestedInToolResults()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1);
        var handler = new DeepSeekCapture();

        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in DeepSeekAdapterFor(handler).Stream(ToolResultImageOptions(TextOnlyModel, reference, bytes), CancellationToken.None))
            {
            }
        });

        Assert.Equal("UNSUPPORTED_CONTENT", error.Failure.Code);
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task Runtime_SendsPastedImagesToTextOnlyModelAsOmittedText()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1, "shot.png");
        var handler = new DeepSeekCapture();
        var llm = new LlmRuntime(_ctx);
        using var registration = llm.RegisterAdapter(["deepseek-official"], DeepSeekAdapterFor(handler));
        var options = new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = TextOnlyModel,
            Messages = [MessageFactory.CreateUserMessage([new TextBlock("what is this"), new ImageBlock(reference)])],
            Purpose = GeneratePurpose.Compaction,
            Attachments = new Dictionary<string, byte[]> { [reference.AttachmentId] = bytes },
        };

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in llm.Stream(options))
            chunks.Add(chunk);

        Assert.DoesNotContain(chunks, chunk => chunk is StreamChunk.Finish { Reason: FinishReason.Error });
        Assert.DoesNotContain("image_url", handler.Body);
        Assert.Contains(TextOnlyImages.OmittedText(reference), handler.Body);
        Assert.IsType<ImageBlock>(options.Messages[0].Content[1]);
    }

    /**
     * 模型目录(内置 Catalog)没写输入模态时, 以 models.dev 快照为准:
     * deepseek-v4-flash 在快照里是多模态的, 漏查会把用户贴的图换成 "无法看图" 的说明文字。
     */
    [Fact]
    public void ResolvedModalities_ComeFromTheCatalogSnapshot_WhenTheModelDoesNotDeclareThem()
    {
        var adapter = DeepSeekAdapterFor(new DeepSeekCapture());

        Assert.Contains("image", adapter.ResolveModel("deepseek-v4-flash").InputModalities!);

        // 快照里明确只有文本的模型必须保持纯文本。
        Assert.DoesNotContain("image", adapter.ResolveModel("deepseek-v4-pro").InputModalities!);
    }

    /** 目录里声明为多模态的 DeepSeek 模型, 粘贴的图片必须真的发到线上, 而不是被替换成说明文字。 */
    [Fact]
    public async Task Runtime_SendsPastedImagesToAModelTheSnapshotDeclaresMultimodal()
    {
        var bytes = PngBytes();
        var reference = _store.Put(bytes, "image/png", 1, 1, "shot.png");
        var handler = new DeepSeekCapture();
        var llm = new LlmRuntime(_ctx);
        using var registration = llm.RegisterAdapter(["deepseek-official"], DeepSeekAdapterFor(handler));
        var options = new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = "deepseek-v4-flash",
            Messages = [MessageFactory.CreateUserMessage([new TextBlock("what is this"), new ImageBlock(reference)])],
            Purpose = GeneratePurpose.Compaction,
            Attachments = new Dictionary<string, byte[]> { [reference.AttachmentId] = bytes },
        };

        await foreach (var _ in llm.Stream(options))
        {
        }

        Assert.Contains("image_url", handler.Body);
        Assert.DoesNotContain(TextOnlyImages.OmittedText(reference), handler.Body);
    }

    private static GenerateOptions ToolResultImageOptions(string model, ImageAttachmentRef reference, byte[] bytes)
    {
        var callId = ToolCallId.Create("call_1");
        var parallelId = ToolCallId.Create("call_2");
        return new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = model,
            Messages =
            [
                MessageFactory.CreateUserText("look"),
                MessageFactory.CreateAssistantMessage(
                    [new ToolCallBlock(callId, "read_image", """{"file_path":"a.png"}"""), new ToolCallBlock(parallelId, "bash", """{"command":"ls"}""")],
                    "deepseek-official",
                    model),
                MessageFactory.CreateToolResultMessage(callId, [new TextBlock("envelope"), new ImageBlock(reference)], false),
                MessageFactory.CreateToolResultMessage(parallelId, [new TextBlock("a.png")], false),
            ],
            Attachments = new Dictionary<string, byte[]> { [reference.AttachmentId] = bytes },
        };
    }

    private static DeepSeekAdapter DeepSeekAdapterFor(HttpMessageHandler handler)
    {
        var connection = new DeepSeekConnectionOptions(
            "https://example.invalid",
            "TEST_KEY",
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            [new DeepSeekCatalogModel(VisionModel, InputModalities: ["text", "image"]), new DeepSeekCatalogModel(TextOnlyModel)],
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "test"));
        return new DeepSeekAdapter("deepseek-official", new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (_, _) => Task.FromResult("sk-test"),
            ResolveUserId = () => "test-user",
            HttpClient = new HttpClient(handler),
        });
    }

    private sealed class DeepSeekCapture : HttpMessageHandler
    {
        private const string Sse = """
            data: {"choices":[{"delta":{"content":"ok"}}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{Sse}\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    [Fact]
    public void AttachmentResolution_ThrowsWhenBytesMissing()
    {
        var reference = new ImageAttachmentRef(new string('f', 64), "image/png", 10, 1, 1);
        var options = new GenerateOptions { Provider = "p", Model = "m", Messages = [] };

        Assert.Throws<LlmException>(() => AttachmentResolution.DataUrl(options, reference));
    }
}
