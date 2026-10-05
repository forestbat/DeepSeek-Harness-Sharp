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
    private readonly Context _ctx = new();
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"dsh-attachments-{Guid.NewGuid():N}");
    private readonly FileAttachmentStore _store;

    public AttachmentTests()
    {
        Directory.CreateDirectory(_home);
        _store = new FileAttachmentStore(_ctx, _home);
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
    public void AttachmentResolution_ThrowsWhenBytesMissing()
    {
        var reference = new ImageAttachmentRef(new string('f', 64), "image/png", 10, 1, 1);
        var options = new GenerateOptions { Provider = "p", Model = "m", Messages = [] };

        Assert.Throws<LlmException>(() => AttachmentResolution.DataUrl(options, reference));
    }
}
