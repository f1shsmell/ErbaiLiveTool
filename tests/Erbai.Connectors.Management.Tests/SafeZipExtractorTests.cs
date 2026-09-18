using System.IO.Compression;
using System.Text;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="SafeZipExtractor"/> 的安全规则验证。连接器包来自互联网，
/// 解压环节是"恶意归档"唯一的入口，所以这里逐条钉死拒绝规则。
/// </summary>
public class SafeZipExtractorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------------------------
    // 路径规范化：直接测纯函数，不依赖 ZipArchive 是否允许构造畸形条目名
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("c:evil.txt")]
    [InlineData("a//b.txt")]
    [InlineData("./a.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("nul")]
    [InlineData("LPT1.log")]
    [InlineData("a/com3.dll")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("a/stream:name.txt")]
    [InlineData("")]
    public void NormalizeEntryPath_RejectsUnsafeNames(string entryName)
    {
        ConnectorManagementException ex =
            Assert.Throws<ConnectorManagementException>(() => SafeZipExtractor.NormalizeEntryPath(entryName));

        Assert.Contains("不安全路径", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeEntryPath_RejectsEmbeddedNul()
    {
        Assert.Throws<ConnectorManagementException>(
            () => SafeZipExtractor.NormalizeEntryPath("a\0b.txt"));
    }

    [Theory]
    [InlineData("Awoo.Connector.Netease.exe", "Awoo.Connector.Netease.exe")]
    [InlineData("lib/deps.json", "lib\\deps.json")]
    [InlineData("lib\\deps.json", "lib\\deps.json")]
    [InlineData("a/b/c.txt", "a\\b\\c.txt")]
    [InlineData("a/", "a")]
    public void NormalizeEntryPath_AcceptsSafeNames(string entryName, string expected)
    {
        Assert.Equal(expected, SafeZipExtractor.NormalizeEntryPath(entryName));
    }

    // ---------------------------------------------------------------------------------
    // 完整解压
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Extract_WritesNestedFilesAndCreatesDirectories()
    {
        byte[] zip = ArchiveBuilder.CreateZip(
            ("Awoo.Connector.Netease.exe", "exe-bytes"),
            ("runtimes/lib.dll", "dll-bytes"));

        string destination = _temp.Combine("staging");
        SafeZipExtractor.Extract(new MemoryStream(zip), destination);

        Assert.Equal("exe-bytes", File.ReadAllText(Path.Combine(destination, "Awoo.Connector.Netease.exe")));
        Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(destination, "runtimes", "lib.dll")));
    }

    /// <summary>
    /// Zip-slip 端到端：若 .NET 允许构造 <c>../</c> 条目名，解压必须拒绝且不得越界落盘。
    /// 若 .NET 自己就拒绝构造这种条目，则本用例无事可做（记录为提前返回）。
    /// </summary>
    [Fact]
    public void Extract_RejectsZipSlipEntry()
    {
        byte[] zip;
        try
        {
            zip = ArchiveBuilder.CreateZip(("../escaped.txt", "pwned"));
        }
        catch (ArgumentException)
        {
            // ZipArchive 自己就不允许这种条目名——同样达到了防护目的。
            return;
        }

        string destination = _temp.Combine("staging");

        Assert.Throws<ConnectorManagementException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), destination));

        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_temp.Path)!, "escaped.txt")));
    }

    [Fact]
    public void Extract_RejectsTooManyEntries()
    {
        (string Name, string Content)[] entries =
            [.. Enumerable.Range(0, 12).Select(i => ($"file{i}.txt", "x"))];

        byte[] zip = ArchiveBuilder.CreateZip(entries);
        string destination = _temp.Combine("staging");

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), destination, maxEntries: 10));

        Assert.Contains("条目数超过上限", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 压缩炸弹：归档声明的大小不可信，必须按<b>实际写出的字节数</b>中止。
    /// 这里故意给一个远小于真实内容的预算。
    /// </summary>
    [Fact]
    public void Extract_AbortsWhenActualBytesExceedBudget()
    {
        string payload = new('A', 200_000);
        byte[] zip = ArchiveBuilder.CreateZip(("big.bin", payload));

        string destination = _temp.Combine("staging");

        ConnectorManagementException ex = Assert.Throws<ConnectorManagementException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), destination, maxUncompressedBytes: 1_000));

        Assert.Contains("超过安全上限", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RejectsNonZipContent()
    {
        byte[] notAZip = Encoding.UTF8.GetBytes("this is definitely not a zip archive");

        Assert.Throws<ConnectorManagementException>(
            () => SafeZipExtractor.Extract(new MemoryStream(notAZip), _temp.Combine("staging")));
    }

    /// <summary>不覆盖既有文件（对应参考实现的 <c>wx</c> 语义）。</summary>
    [Fact]
    public void Extract_DoesNotOverwriteExistingFile()
    {
        byte[] zip = ArchiveBuilder.CreateZip(("existing.txt", "from-zip"));

        string destination = _temp.Combine("staging");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "already-here");

        Assert.ThrowsAny<IOException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), destination));

        Assert.Equal("already-here", File.ReadAllText(Path.Combine(destination, "existing.txt")));
    }

    [Fact]
    public void Extract_RejectsInvalidLimits()
    {
        byte[] zip = ArchiveBuilder.CreateZip(("a.txt", "x"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), _temp.Combine("s1"), maxEntries: 0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SafeZipExtractor.Extract(new MemoryStream(zip), _temp.Combine("s2"), maxUncompressedBytes: 0));
    }

    /// <summary>目录条目（以 <c>/</c> 结尾）应当只建目录，不当文件写。</summary>
    [Fact]
    public void Extract_CreatesDirectoryEntries()
    {
        using MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("subdir/");
            ZipArchiveEntry entry = archive.CreateEntry("subdir/file.txt");
            using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes("hello");
            stream.Write(bytes, 0, bytes.Length);
        }

        string destination = _temp.Combine("staging");
        SafeZipExtractor.Extract(new MemoryStream(buffer.ToArray()), destination);

        Assert.True(Directory.Exists(Path.Combine(destination, "subdir")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "subdir", "file.txt")));
    }
}
