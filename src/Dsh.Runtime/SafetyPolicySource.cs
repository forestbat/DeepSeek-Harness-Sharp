namespace Dsh.Runtime;

public interface ISafetyPolicySettings
{
    PermissionRules GetSafetyRules(bool scriptPermissions);
}

public sealed class SafetyPolicySource(Context context, Func<ISafetyPolicySettings> read)
    : Service(context, ServiceName)
{
    public const string ServiceName = "safetyPolicySource";
    public ISafetyPolicySettings Read() => read();
}
