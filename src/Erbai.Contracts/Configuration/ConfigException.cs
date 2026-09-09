namespace Erbai.Contracts.Configuration;

/// <summary>配置结构非法或校验失败（磁盘/内存均不写入）。</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
