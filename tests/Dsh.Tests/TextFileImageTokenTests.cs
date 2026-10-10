using Dsh.Llm;

namespace Dsh.Tests;

/** 复用文本文件图片扫描的识别规则(远端按其自行解析路径)。 */
public sealed class TextFileImageTokenTests
{
    [Fact]
    public void ImageTokens_FindsPathsAndDedupes()
        => Assert.Equal(["a.png", "b.jpg"], TextFileImageReferences.ImageTokens("see a.png and b.jpg and a.png"));

    [Fact]
    public void ReferenceTokens_ExtractsAtPaths()
        => Assert.Equal(["notes.md", "sub/shot.png"], TextFileImageReferences.ReferenceTokens("read @notes.md and @sub/shot.png please"));
}
