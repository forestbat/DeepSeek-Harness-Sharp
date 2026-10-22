namespace Dsh.Android.Services;

public sealed record ShareItem(string Text, string Name, string MediaType, byte[] Image, string Error)
{
    public static ShareItem ForText(string text) => new(text, "", "", [], "");

    public static ShareItem ForImage(string name, string mediaType, byte[] image) => new("", name, mediaType, image, "");

    public static ShareItem ForError(string error) => new("", "", "", [], error);
}

/**
 * 系统分享(ACTION_SEND)的交接点: Activity 把分享内容放进来, ViewModel 取走。
 * 只存解析后的字符串与图片字节, 不持有 Intent、Uri 或输入流。
 */
public static class ShareInbox
{
    private static readonly Lock Gate = new();
    private static readonly Queue<ShareItem> Pending = new();

    public static event Action? Changed;

    public static void Offer(ShareItem item)
    {
        lock (Gate)
            Pending.Enqueue(item);
        Changed?.Invoke();
    }

    public static ShareItem? Take()
    {
        lock (Gate)
            return Pending.Count > 0 ? Pending.Dequeue() : null;
    }
}
