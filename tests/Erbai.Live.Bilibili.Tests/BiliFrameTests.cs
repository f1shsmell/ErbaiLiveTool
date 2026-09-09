using System.IO.Compression;
using System.Text;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

public class BiliFrameTests
{
    [Fact]
    public void Wrap_ProducesBigEndianHeader()
    {
        var frame = BiliFrame.Wrap(BiliFrame.OperationAuth, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(16 + 2, frame.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 18 }, frame[..4]);          // pack_len = 18
        Assert.Equal(new byte[] { 0, 16 }, frame[4..6]);               // raw_header_size = 16
        Assert.Equal(new byte[] { 0, 1 }, frame[6..8]);                // ver = 1 明文
        Assert.Equal(new byte[] { 0, 0, 0, 7 }, frame[8..12]);         // op = 7 认证
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, frame[12..16]);        // seq_id = 1
        Assert.Equal("{}", Encoding.UTF8.GetString(frame[16..]));
    }

    [Fact]
    public void Unwrap_SingleFrame_Roundtrips()
    {
        var body = Encoding.UTF8.GetBytes("{\"cmd\":\"LIVE\"}");
        var frame = BiliFrame.Wrap(BiliFrame.OperationSendMsgReply, body);

        var packets = BiliFrame.Unwrap(frame).ToList();

        var packet = Assert.Single(packets);
        Assert.Equal(BiliFrame.OperationSendMsgReply, packet.Operation);
        Assert.Equal(body, packet.Body);
    }

    [Fact]
    public void Unwrap_MultipleConcatenatedFrames_YieldsAll()
    {
        var a = BiliFrame.Wrap(3, new byte[] { 0, 0, 0, 100 });
        var b = BiliFrame.Wrap(5, Encoding.UTF8.GetBytes("x"));
        var combined = a.Concat(b).ToArray();

        var packets = BiliFrame.Unwrap(combined).ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal(3, packets[0].Operation);
        Assert.Equal(5, packets[1].Operation);
    }

    [Fact]
    public void Unwrap_Ver2Zlib_RecursivelySplitsSubPackets()
    {
        var inner1 = BiliFrame.Wrap(5, Encoding.UTF8.GetBytes("{\"cmd\":\"LIVE\"}"));
        var inner2 = BiliFrame.Wrap(5, Encoding.UTF8.GetBytes("{\"cmd\":\"PREPARING\"}"));
        var compressed = Compress(inner1.Concat(inner2).ToArray(), zlib: true);
        var outer = BiliFrame.Wrap(5, compressed, ver: 2);

        var packets = BiliFrame.Unwrap(outer).ToList();

        Assert.Equal(2, packets.Count);
        Assert.Equal("{\"cmd\":\"LIVE\"}", Encoding.UTF8.GetString(packets[0].Body));
        Assert.Equal("{\"cmd\":\"PREPARING\"}", Encoding.UTF8.GetString(packets[1].Body));
    }

    [Fact]
    public void Unwrap_Ver3Brotli_RecursivelySplitsSubPackets()
    {
        var inner = BiliFrame.Wrap(5, Encoding.UTF8.GetBytes("{\"cmd\":\"LIVE\"}"));
        var compressed = Compress(inner, zlib: false);
        var outer = BiliFrame.Wrap(5, compressed, ver: 3);

        var packets = BiliFrame.Unwrap(outer).ToList();

        var packet = Assert.Single(packets);
        Assert.Equal("{\"cmd\":\"LIVE\"}", Encoding.UTF8.GetString(packet.Body));
    }

    [Fact]
    public void Unwrap_TruncatedFrame_StopsSafely()
    {
        var frame = BiliFrame.Wrap(5, Encoding.UTF8.GetBytes("abc"));
        var truncated = frame[..^2];

        var packets = BiliFrame.Unwrap(truncated).ToList();

        Assert.Empty(packets);
    }

    [Fact]
    public void Unwrap_CorruptHeader_StopsWithoutThrowing()
    {
        // pack_len 声明 100 但实际只有 17 字节
        var corrupt = new byte[17];
        corrupt[0] = 0;
        corrupt[1] = 0;
        corrupt[2] = 0;
        corrupt[3] = 100;

        var packets = BiliFrame.Unwrap(corrupt).ToList();

        Assert.Empty(packets);
    }

    [Fact]
    public void ReadPopularity_ParsesBigEndian()
    {
        var body = new byte[] { 0x00, 0x01, 0xE2, 0x40, 1, 2, 3 };

        Assert.Equal(123456, BiliFrame.ReadPopularity(body));
    }

    [Fact]
    public void ReadPopularity_ShortBody_ReturnsZero()
    {
        Assert.Equal(0, BiliFrame.ReadPopularity(new byte[] { 1, 2 }));
    }

    private static byte[] Compress(byte[] data, bool zlib)
    {
        using var output = new MemoryStream();
        using (var compressor = zlib
                   ? (Stream)new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)
                   : new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }
}
