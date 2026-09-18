namespace Erbai.Connectors.Management;

/// <summary>
/// 连接器管理内核的统一异常类型。所有可预期的失败（清单不可达、格式不合法、
/// 校验不通过、解压不安全、安装回滚）都以此类型抛出，便于 UI 层统一捕获并给出中文提示。
/// </summary>
public sealed class ConnectorManagementException : Exception
{
    public ConnectorManagementException(string message)
        : base(message)
    {
    }

    public ConnectorManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
