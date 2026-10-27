namespace Erbai.Modules.SongRequest.Import;

/// <summary>导入模块内部格式化工具（与 SearchProviders.FormatInterval 同语义：秒 → mm:ss）。</summary>
internal static class ImportFormat
{
    public static string FormatInterval(int seconds) =>
        seconds <= 0 ? "" : $"{seconds / 60:00}:{seconds % 60:00}";
}
