using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/** 输入区的一个图片附件: 内容寻址引用 + PNG 字节(供缩略图/预览经转换器渲染, 不持有 Avalonia 位图以便 headless 测试)。 */
public sealed class AttachmentViewModel(ImageAttachmentRef reference, byte[] png)
{
    public ImageAttachmentRef Reference { get; } = reference;

    public byte[] Png { get; } = png;

    public string Name { get; } = reference.Name ?? "image";
}
