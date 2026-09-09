namespace Erbai.Live.Douyin.Hosting;

/// <summary>WinINET 系统代理设置快照（HKCU Internet Settings 的四个键）。</summary>
public sealed record ProxySnapshot
{
    public string ProxyEnable { get; init; } = "";

    public string ProxyServer { get; init; } = "";

    public string ProxyOverride { get; init; } = "";

    public string AutoConfigURL { get; init; } = "";
}

/// <summary>
/// 系统代理注册表访问隔离接口（docs/04 §3.2 可测性缝：测试 fake 注册表，绝不打真注册表）。
/// 宿主只通过此接口读写 WinINET 代理；实现 <see cref="WinInetProxyStore"/>。
/// </summary>
public interface ISystemProxyStore
{
    /// <summary>读取当前系统代理设置（键缺失时返回空串；读失败也返回空快照）。</summary>
    ProxySnapshot Read();

    /// <summary>
    /// 读取当前系统代理设置；读取失败（注册表键被锁/不存在）返回 null。
    /// 用于还原前的快照采集：调用方拿到 null 必须放弃本轮代理接管，
    /// 不得当作"空代理"快照去 Restore（会删掉用户自己的代理配置）。
    /// </summary>
    ProxySnapshot? TryRead();

    /// <summary>
    /// 把系统代理写为快照值并广播刷新（INTERNET_OPTION_SETTINGS_CHANGED/REFRESH）。
    /// ProxyEnable 缺省/非法置 0（关闭代理）；ProxyServer/ProxyOverride 快照为空时删除键
    /// （还原场景当前值正是抓包器写入的，避免残留）；AutoConfigURL（PAC）快照缺失时保留现值
    /// （清理场景只有部分快照，误删会破坏用户 PAC 配置）。
    /// </summary>
    void Restore(ProxySnapshot snapshot);
}

/// <summary>端口探测接口（测试注入假端口，宿主不写死 8888）。</summary>
public interface ITcpProbe
{
    /// <summary>指定 host:port 是否有监听（连接成功即 true）。</summary>
    bool IsOpen(string host, int port, int timeoutMs);
}
