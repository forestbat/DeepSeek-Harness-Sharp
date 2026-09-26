namespace Dsh.Ptc;

/** PTC run 失败分类, 与上游 8 分类对齐。 */
public static class PtcFailureKinds
{
    public const string Exception = "exception";
    public const string Timeout = "timeout";
    public const string Abort = "abort";
    public const string WorkerExit = "worker-exit";
    public const string InvalidOutput = "invalid-output";
    public const string OutputLimit = "output-limit";
    public const string Protocol = "protocol";
    public const string SandboxUnavailable = "sandbox-unavailable";
}
