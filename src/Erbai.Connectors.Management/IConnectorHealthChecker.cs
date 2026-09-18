namespace Erbai.Connectors.Management;

/// <summary>
/// 连接器启动健康检查的抽象。抽出来是为了让安装器的测试可以注入假实现，
/// 不必真的去拉起一个连接器进程。
/// </summary>
public interface IConnectorHealthChecker
{
    /// <summary>
    /// 对指定可执行文件执行一次健康检查。
    /// </summary>
    /// <param name="executablePath">连接器可执行文件的绝对路径。</param>
    /// <param name="playerKey">期望的平台键；会与连接器自报的 <c>connectorId</c> 比对。</param>
    /// <param name="expectedVersion">期望的版本；为 <see langword="null"/> 时跳过版本比对。</param>
    /// <param name="environment">启动时额外注入的环境变量（如私有运行时的 <c>DOTNET_ROOT</c>）。</param>
    Task<ConnectorHealthResult> CheckAsync(
        string executablePath,
        string playerKey,
        string? expectedVersion = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}
