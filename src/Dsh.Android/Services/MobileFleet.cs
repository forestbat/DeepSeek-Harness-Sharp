using Dsh.RemoteHost;

namespace Dsh.Android.Services;

/**
 * 同时保活的多条桌面机链路: 每台机器各有一条连接与自己的会话投影, 互相不共享状态。
 * 与桌面端的机器列表不同, 这里的"机器"直接由用户在手机上填写, 因此配置文件就是唯一来源。
 */
public sealed class MobileFleet(MachineStore store) : IAsyncDisposable
{
    private readonly Dictionary<string, MobileHostConnection> _links = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public event Action? Changed;

    public IReadOnlyList<MobileHostConnection> Links
    {
        get
        {
            lock (_gate)
                return [.. _links.Values];
        }
    }

    public MobileHostConnection? Find(string name)
    {
        lock (_gate)
            return _links.GetValueOrDefault(name);
    }

    /** 连接(或替换)一台机器; 已存在的旧链路先断开, 保证同一个机器名只有一条活连接。 */
    public async Task<MobileHostConnection> AttachAsync(HostProfile profile)
    {
        await DetachAsync(profile.Name);
        var keyPem = profile.Transport == HostTransportKind.Ssh ? store.LoadPrivateKey() : null;
        var link = new MobileHostConnection(
            profile,
            keyPem,
            fingerprint => store.SaveFingerprint(profile.Name, fingerprint));
        link.Changed += OnLinkChanged;
        lock (_gate)
            _links[profile.Name] = link;
        link.Start();
        RaiseChanged();
        return link;
    }

    public async Task DetachAsync(string name)
    {
        MobileHostConnection? link;
        lock (_gate)
        {
            if (!_links.Remove(name, out link))
                return;
        }
        link.Changed -= OnLinkChanged;
        await link.DisposeAsync();
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        List<MobileHostConnection> links;
        lock (_gate)
        {
            links = [.. _links.Values];
            _links.Clear();
        }
        foreach (var link in links)
        {
            link.Changed -= OnLinkChanged;
            await link.DisposeAsync();
        }
    }

    private void OnLinkChanged() => Changed?.Invoke();

    private void RaiseChanged() => Changed?.Invoke();
}
