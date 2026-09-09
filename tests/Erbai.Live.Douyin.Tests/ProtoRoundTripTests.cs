using System.Text;
using BarrageGrab.Modles.ProtoEntity;
using ProtoBuf;

namespace Erbai.Live.Douyin.Tests;

/// <summary>
/// Grabber protobuf 报文模型（protobuf-net 生成，my.proto）单测：
/// 序列化往返保真 + 手写 wire 字节反序列化验证字段号映射。
/// 报文模型是抓包链路两侧（Grabber 子进程 ↔ 宿主解析）的契约载体。
/// </summary>
public class ProtoRoundTripTests
{
    private static T RoundTrip<T>(T original)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, original);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }

    [Fact]
    public void WssResponse_往返保真()
    {
        var original = new WssResponse
        {
            Seqid = 42,
            Logid = 43,
            Service = 44,
            Method = 45,
            Payload = new byte[] { 1, 2, 3 },
        };
        original.Headers["hk"] = "hv";

        var round = RoundTrip(original);

        Assert.Equal(42ul, round.Seqid);
        Assert.Equal(43ul, round.Logid);
        Assert.Equal(44ul, round.Service);
        Assert.Equal(45ul, round.Method);
        Assert.Equal("hv", round.Headers["hk"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, round.Payload);
    }

    [Fact]
    public void Response_含Message列表_往返保真()
    {
        var original = new Response
        {
            Cursor = "c1",
            fetchInterval = 100,
            Now = 200,
            needAck = true,
            liveCursor = "lc",
        };
        original.Messages.Add(new Message
        {
            Method = "WebcastGiftMessage",
            Payload = new byte[] { 9, 8, 7 },
            msgId = 7,
            msgType = 3,
            Offset = 5,
        });
        original.routeParams["rp"] = "rv";

        var round = RoundTrip(original);

        Assert.Single(round.Messages);
        Assert.Equal("c1", round.Cursor);
        Assert.Equal(100, round.fetchInterval);
        Assert.Equal(200, round.Now);
        Assert.True(round.needAck);
        Assert.Equal("lc", round.liveCursor);
        Assert.Equal("rv", round.routeParams["rp"]);

        var message = round.Messages[0];
        Assert.Equal("WebcastGiftMessage", message.Method);
        Assert.Equal(new byte[] { 9, 8, 7 }, message.Payload);
        Assert.Equal(7, message.msgId);
        Assert.Equal(3, message.msgType);
        Assert.Equal(5, message.Offset);
    }

    [Fact]
    public void 手写Wire字节_WssResponse字段映射()
    {
        // field1 (Seqid) varint 42 → 08 2A；field8 (Payload) bytes {1,2} → 42 02 01 02
        var bytes = new byte[] { 0x08, 0x2A, 0x42, 0x02, 0x01, 0x02 };
        using var stream = new MemoryStream(bytes);

        var round = Serializer.Deserialize<WssResponse>(stream);

        Assert.Equal(42ul, round.Seqid);
        Assert.Equal(new byte[] { 1, 2 }, round.Payload);
    }

    [Fact]
    public void 手写Wire字节_Response内嵌Message字段映射()
    {
        // Response.field1 (Messages) 内嵌 Message：field1 method="WebcastGiftMessage"(19)、
        // field2 payload={1,2}；Message 体长 25；随后 Response.field2 (Cursor)="c"
        var method = Encoding.UTF8.GetBytes("WebcastGiftMessage");
        var inner = new List<byte> { 0x0A, (byte)method.Length };
        inner.AddRange(method);
        inner.AddRange(new byte[] { 0x12, 0x02, 0x01, 0x02 });

        var bytes = new List<byte> { 0x0A, (byte)inner.Count };
        bytes.AddRange(inner);
        bytes.AddRange(new byte[] { 0x12, 0x01, 0x63 }); // field2 string "c"

        using var stream = new MemoryStream(bytes.ToArray());
        var round = Serializer.Deserialize<Response>(stream);

        var message = Assert.Single(round.Messages);
        Assert.Equal("WebcastGiftMessage", message.Method);
        Assert.Equal(new byte[] { 1, 2 }, message.Payload);
        Assert.Equal("c", round.Cursor);
    }
}
