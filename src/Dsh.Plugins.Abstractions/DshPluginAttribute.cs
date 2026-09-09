namespace Dsh.Plugins;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DshPluginAttribute(string packageName) : Attribute
{
    public string PackageName { get; } = packageName;
}
