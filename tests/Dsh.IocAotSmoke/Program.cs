using Dsh.Runtime.Ioc;

// IServiceRegistry 的 NativeAOT 冒烟:注册/解析/注销/委托注册/作用域在 AOT 发布下可用。
// 运行方式(项目根目录):
//   DOTNET_ROOT=/home/pub/.dotnet /home/pub/.dotnet/dotnet publish tests/Dsh.IocAotSmoke/Dsh.IocAotSmoke.csproj -c Release -r linux-x64
//   ./tests/Dsh.IocAotSmoke/bin/Release/net11.0/linux-x64/publish/Dsh.IocAotSmoke

var registry = new DryIocServiceRegistry();
var root = new Probe("root");
var isolated = new Probe("isolated");
registry.Register("svc", root);
registry.Register("svc", isolated, isolateKey: "iso");

if (!ReferenceEquals(root, registry.Resolve("svc")) || !ReferenceEquals(isolated, registry.Resolve("svc", "iso")))
    return Fail("keyed resolve");
if (registry.ResolveMany("svc").Count != 2)
    return Fail("resolve many");

if (!registry.Unregister("svc", "iso") || registry.Resolve("svc", "iso") is not null)
    return Fail("unregister");
if (registry.Unregister("svc", "iso"))
    return Fail("unregister twice");

registry.RegisterFactory<Probe>("transient", _ => new Probe("t"), ServiceLifetime.Transient);
var transient1 = registry.Resolve("transient");
var transient2 = registry.Resolve("transient");
if (ReferenceEquals(transient1, transient2))
    return Fail("transient lifetime");
registry.RegisterFactory<Probe>("scoped", _ => new Probe("s"), ServiceLifetime.Scoped);
using (var scope = registry.OpenScope("scope"))
{
    var scoped1 = scope.Resolve("scoped");
    var scoped2 = scope.Resolve("scoped");
    if (!ReferenceEquals(scoped1, scoped2))
        return Fail("scoped lifetime");
}

Console.WriteLine("IOC-AOT-SMOKE-OK");
return 0;

static int Fail(string step)
{
    Console.Error.WriteLine($"IOC-AOT-SMOKE-FAILED: {step}");
    return 1;
}

internal sealed class Probe(string name)
{
    public string Name { get; } = name;
}
