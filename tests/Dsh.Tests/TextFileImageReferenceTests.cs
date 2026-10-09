using Dsh.Llm;

namespace Dsh.Tests;

public sealed class TextFileImageReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(), "artifacts020", "imageref", Guid.NewGuid().ToString("N"));

    public TextFileImageReferenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Scan_Collects_Images_Referenced_By_A_Text_File()
    {
        var image = Path.Combine(_root, "shot.png");
        File.WriteAllBytes(image, [1, 2, 3]);
        File.WriteAllText(Path.Combine(_root, "notes.md"), "见 shot.png 附图\n以及 <img src=\"shot.png\">");

        Assert.Contains(image, TextFileImageReferences.Scan("@notes.md", _root));
    }

    [Fact]
    public void Scan_Resolves_Relative_Paths_Against_The_Text_File_Directory()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        var image = Path.Combine(sub, "pic.jpg");
        File.WriteAllBytes(image, [0]);
        File.WriteAllText(Path.Combine(sub, "doc.txt"), "asset: pic.jpg");

        Assert.Contains(image, TextFileImageReferences.Scan("see @sub/doc.txt", _root));
    }

    [Fact]
    public void Scan_Ignores_Missing_And_Direct_Image_References()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "nothing here but missing.png");
        File.WriteAllBytes(Path.Combine(_root, "direct.png"), [9]);

        Assert.Empty(TextFileImageReferences.Scan("@a.txt", _root));
        Assert.Empty(TextFileImageReferences.Scan("@direct.png", _root));
    }
}
