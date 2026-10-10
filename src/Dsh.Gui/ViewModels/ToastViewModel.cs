namespace Dsh.Gui.ViewModels;

/** 右下角气泡: 标题 + 正文 + 是否错误(错误用红色强调)。 */
public sealed record ToastViewModel(string Title, string Message, bool IsError)
{
    public bool IsSuccess => !IsError;
}
