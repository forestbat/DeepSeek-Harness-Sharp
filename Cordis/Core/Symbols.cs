namespace Cordis;

public sealed class CordisSymbol
{
    private static readonly Dictionary<string, CordisSymbol> Registry = new();
    private static readonly Lock Sync = new();

    public string Name { get; }

    private CordisSymbol(string name)
    {
        Name = name;
    }

    public static CordisSymbol For(string name)
    {
        lock (Sync)
        {
            if (Registry.TryGetValue(name, out var symbol)) return symbol;
            return Registry[name] = new CordisSymbol(name);
        }
    }

    public static CordisSymbol New(string name) => new(name);

    public override string ToString() => $"Symbol({Name})";
}

public static class Symbols
{
    public static readonly CordisSymbol Shadow = CordisSymbol.For("cordis.shadow");
    public static readonly CordisSymbol Caller = CordisSymbol.For("cordis.caller");
    public static readonly CordisSymbol Receiver = CordisSymbol.For("cordis.receiver");
    public static readonly CordisSymbol Original = CordisSymbol.For("cordis.original");
    public static readonly CordisSymbol Metadata = CordisSymbol.For("cordis.metadata");
    public static readonly CordisSymbol InitHooks = CordisSymbol.For("cordis.initHooks");
    public static readonly CordisSymbol CheckProto = CordisSymbol.For("cordis.checkProto");

    public static readonly CordisSymbol Effect = CordisSymbol.For("cordis.effect");
    public static readonly CordisSymbol Filter = CordisSymbol.For("cordis.filter");
    public static readonly CordisSymbol Isolate = CordisSymbol.For("cordis.isolate");
    public static readonly CordisSymbol Intercept = CordisSymbol.For("cordis.intercept");

    public static readonly CordisSymbol Init = CordisSymbol.For("cordis.init");
    public static readonly CordisSymbol Check = CordisSymbol.For("cordis.check");
    public static readonly CordisSymbol Config = CordisSymbol.For("cordis.config");
    public static readonly CordisSymbol Invoke = CordisSymbol.For("cordis.invoke");
    public static readonly CordisSymbol Extend = CordisSymbol.For("cordis.extend");
    public static readonly CordisSymbol Tracker = CordisSymbol.For("cordis.tracker");
    public static readonly CordisSymbol ResolveConfig = CordisSymbol.For("cordis.resolveConfig");

    public static readonly CordisSymbol Entry = CordisSymbol.For("cordis.entry");
    public static readonly CordisSymbol Group = CordisSymbol.For("cordis.group");
}
