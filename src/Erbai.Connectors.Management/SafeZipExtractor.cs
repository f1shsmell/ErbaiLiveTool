using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Erbai.Connectors.Management;

/// <summary>
/// 安全解压 ZIP（照搬参考实现 <c>electron/safe-zip.ts</c> 的规则）。
/// </summary>
/// <remarks>
/// <para>拒绝的条目形态：绝对路径、盘符、<c>..</c> / <c>.</c> 段、含 <c>:</c> 的段、
/// 以点或空格结尾的段、Windows 保留名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）、含 NUL、
/// 加密条目、以及解压后总量超限的归档。</para>
/// <para>
/// 与参考实现的一处差异（有意加固）：参考实现信任 ZIP 头部声明的解压后大小
/// （yauzl 的 <c>validateEntrySizes</c>），本实现<b>另外统计实际写出的字节数</b>并在超限时中止。
/// 头部字段由攻击者控制，只信声明值挡不住"声明 1KB、实际解出 10GB"的压缩炸弹。
/// </para>
/// <para>
/// 关于符号链接 / reparse point：ZIP 只能通过 external attributes 表达这类条目，而本实现
/// 是<b>逐条目读流后自己创建普通文件</b>（<see cref="FileMode.CreateNew"/>），从不调用
/// 系统解压器，也从不创建链接，因此即使归档里存在链接条目，落盘结果也只是一个内容为
/// 目标路径的普通文件——不会发生经由链接的越界写入。这是结构性缓解，而非依赖某个标志位。
/// </para>
/// </remarks>
public static class SafeZipExtractor
{
    /// <summary>归档内条目数上限。</summary>
    public const int DefaultMaxEntries = 20_000;

    /// <summary>解压后总字节数上限（2 GiB）。</summary>
    public const long DefaultMaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    private static readonly Regex WindowsReservedName =
        new(@"^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把归档内的条目名规范化为相对路径，并拒绝一切不安全形态。
    /// </summary>
    /// <exception cref="ConnectorManagementException">条目名不安全。</exception>
    public static string NormalizeEntryPath(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        string normalized = fileName.Replace('\\', '/');
        string withoutTrailingSlash = normalized.EndsWith('/')
            ? normalized[..^1]
            : normalized;

        if (withoutTrailingSlash.Length == 0
            || withoutTrailingSlash.Contains('\0')
            || normalized.StartsWith('/')
            || (normalized.Length >= 2 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0])))
        {
            throw UnsafePath(fileName);
        }

        string[] segments = withoutTrailingSlash.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.Contains(':')
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || WindowsReservedName.IsMatch(segment))
            {
                throw UnsafePath(fileName);
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// 解压 <paramref name="archivePath"/> 到 <paramref name="destinationDirectory"/>。
    /// 目标目录会被创建；已存在的同名文件会导致失败（不覆盖）。
    /// </summary>
    /// <exception cref="ConnectorManagementException">归档损坏、含不安全条目或超出限额。</exception>
    public static void Extract(
        string archivePath,
        string destinationDirectory,
        int maxEntries = DefaultMaxEntries,
        long maxUncompressedBytes = DefaultMaxUncompressedBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Extract(stream, destinationDirectory, maxEntries, maxUncompressedBytes);
    }

    /// <summary>解压流形式的 ZIP。</summary>
    /// <exception cref="ConnectorManagementException">归档损坏、含不安全条目或超出限额。</exception>
    public static void Extract(
        Stream archiveStream,
        string destinationDirectory,
        int maxEntries = DefaultMaxEntries,
        long maxUncompressedBytes = DefaultMaxUncompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "条目数上限必须为正数。");
        }

        if (maxUncompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes), "解压字节上限必须为正数。");
        }

        string destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new ConnectorManagementException($"归档不是合法 ZIP：{ex.Message}", ex);
        }

        using (archive)
        {
            int entryCount = 0;
            long totalWritten = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                entryCount++;
                if (entryCount > maxEntries)
                {
                    throw new ConnectorManagementException($"归档条目数超过上限 {maxEntries}。");
                }

                if (entry.Length < 0)
                {
                    throw new ConnectorManagementException($"归档条目 {entry.FullName} 声明了无效大小。");
                }

                string relativePath = NormalizeEntryPath(entry.FullName);
                string targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                if (!IsInside(destinationRoot, targetPath))
                {
                    throw UnsafePath(entry.FullName);
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                string? parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                totalWritten += WriteEntry(entry, targetPath, maxUncompressedBytes - totalWritten);
            }
        }
    }

    private static long WriteEntry(ZipArchiveEntry entry, string targetPath, long remainingBudget)
    {
        Stream input;
        try
        {
            input = entry.Open();
        }
        catch (InvalidDataException ex)
        {
            // .NET 不支持加密 ZIP；打开时抛错。这里选择失败关闭而不是跳过该条目。
            throw new ConnectorManagementException(
                $"归档条目 {entry.FullName} 无法读取（可能被加密或使用了不支持的压缩方法）：{ex.Message}",
                ex);
        }

        using (input)
        using (FileStream output = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            return CopyWithLimit(input, output, remainingBudget, entry.FullName);
        }
    }

    /// <summary>
    /// 复制流并统计实际写出的字节数；超出预算立即中止。
    /// 这是对 ZIP 头部声明值的独立校验（见类型注释）。
    /// </summary>
    private static long CopyWithLimit(Stream input, Stream output, long budget, string entryName)
    {
        byte[] buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            written += read;
            if (written > budget)
            {
                throw new ConnectorManagementException(
                    $"归档条目 {entryName} 实际解压字节数超过安全上限。");
            }

            output.Write(buffer, 0, read);
        }

        return written;
    }

    /// <summary>目标路径是否确实位于根目录之内（按目录边界比较，防前缀误判）。</summary>
    private static bool IsInside(string root, string target)
    {
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return target.StartsWith(
            rootWithSeparator,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectorManagementException UnsafePath(string fileName) =>
        new($"ZIP 包含不安全路径：{fileName}");
}
