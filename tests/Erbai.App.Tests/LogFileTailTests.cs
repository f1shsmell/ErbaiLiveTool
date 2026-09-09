using System.IO;
using System.Linq;
using Erbai.App.Views;

namespace Erbai.App.Tests;

/// <summary>
/// 日志页尾部只读窗口的单测（LogFileTail 通过源文件链接编译进本测试程序集）。
/// 覆盖行边界、截断、空文件、CRLF、无尾随换行等直接决定"打开日志页"历史窗口
/// 正确性的边界——该逻辑一旦算错（丢行/截半行/乱序），日志回看内容就不对。
/// </summary>
public class LogFileTailTests
{
    private static string TempFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ErbaiLogTailTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "erbai-test.log");
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ReadTail_FileWithinLimit_ReturnsAllLinesInOrder()
    {
        var path = TempFile("a\nb\nc\nd\n");
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 100);
            Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_MoreLinesThanLimit_ReturnsOnlyTailWindow()
    {
        var path = TempFile(string.Concat(Enumerable.Range(0, 100).Select(i => $"line{i}\n")));
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 10);
            // 只返回最后 10 行（line90..line99），且顺序与文件中一致（正序）
            Assert.Equal(10, lines.Count);
            Assert.Equal(Enumerable.Range(90, 10).Select(i => $"line{i}"), lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_CrlfEndings_TrimsCarriageReturns()
    {
        var path = TempFile("a\r\nb\r\nc\r\n");
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 100);
            Assert.Equal(new[] { "a", "b", "c" }, lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_NoTrailingNewline_KeepsLastLine()
    {
        var path = TempFile("a\nb\nno-newline-at-end") ; // 最后一行无 \n
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 100);
            Assert.Equal(new[] { "a", "b", "no-newline-at-end" }, lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_EmptyFile_ReturnsEmpty()
    {
        var path = TempFile("");
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 100);
            Assert.Empty(lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_ZeroLimit_ReturnsNothing()
    {
        var path = TempFile("a\nb\n");
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 0);
            Assert.Empty(lines);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_LargeBeyondSingleBlock_TruncatesAtHeadNotTail()
    {
        // 远超 64KB 块但每行内容短——验证跨块读取时尾部窗口正确、头部截断正确
        var path = TempFile(string.Concat(Enumerable.Range(0, 5000).Select(i => $"x{i}\n")));
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 50);
            Assert.Equal(50, lines.Count);
            Assert.Equal("x4950", lines[0]);
            Assert.Equal("x4999", lines[^1]);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ReadTail_SingleLineLargerThanLimit_StillReturnsWholeLine()
    {
        // 无换行的超大单行：窗口会一路扩到文件头也不应丢内容
        var big = new string('z', 200_000);
        var path = TempFile(big);
        try
        {
            var lines = LogFileTail.ReadTailLinesShared(path, maxLines: 1);
            Assert.Single(lines);
            Assert.Equal(big, lines[0]);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }
}