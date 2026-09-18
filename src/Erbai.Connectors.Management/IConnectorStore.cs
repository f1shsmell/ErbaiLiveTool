namespace Erbai.Connectors.Management;

/// <summary>
/// <c>active.json</c> 存储的抽象。抽出来是为了让安装器的测试可以注入一个
/// "写到一半失败"的替身，从而真正走到回滚分支（把旧版本目录从备份改回来）。
/// </summary>
public interface IConnectorStore
{
    /// <summary>读取并自校验当前生效记录；不存在或不合法时返回 <see langword="null"/>。</summary>
    ActiveConnector? ReadActive(string playerKey);

    /// <summary>原子写入 active.json。</summary>
    void WriteActive(string playerKey, ActiveConnector active);

    /// <summary>删除 active.json。</summary>
    void DeleteActive(string playerKey);

    /// <summary>某平台是否已安装且记录可用。</summary>
    bool IsInstalled(string playerKey);
}
