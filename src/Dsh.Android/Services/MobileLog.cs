namespace Dsh.Android.Services;

/**
 * 手机端的诊断输出: 只写 logcat。
 * 真机上没有控制台, 性能问题只能在 adb logcat 里看时间戳, 所以留一个统一的出口。
 */
public static class MobileLog
{
    private const string Tag = "DshMobile";

    public static void Info(string message)
    {
        global::Android.Util.Log.Info(Tag, message);
    }
}
