using Dsh.Runtime;

namespace Dsh.Boot;

public sealed class SafetyPolicySource(Context context, Func<HarnessSettings> read)
    : Service(context, ServiceName)
{
    public const string ServiceName = "safetyPolicySource";
    public HarnessSettings Read() => read();
}
