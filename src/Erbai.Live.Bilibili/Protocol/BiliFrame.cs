using System.Buffers.Binary;
using System.IO.Compression;

namespace Erbai.Live.Bilibili.Protocol;

/// <summary>
/// B站直播弹幕 WS 帧协议（docs/04-协议与接口契约.md §2.2）。
/// 16 字节大端帧头：pack_len u32 / raw_header_size u16 / ver u16 / operation u32 / seq_id u32。
/// 压缩 ver：0/1 明文，2 zlib，3 brotli（解压后为多个子包，递归切片）。
/// </summary>
public static class BiliFrame
{
    public const int HeaderSize = 16;

    /// <summary>op=7 认证；body 为 JSON。</summary>
    public const int OperationAuth = 7;

    /// <summary>op=8 认证回复。</summary>
    public const int OperationAuthReply = 8;

    /// <summary>op=2 心跳；body 可为空。</summary>
    public const int OperationHeartbeat = 2;

    /// <summary>op=3 心跳回复；body 前 4 字节大端人气值。</summary>
    public const int OperationHeartbeatReply = 3;

    /// <summary>op=5 业务消息。</summary>
    public const int OperationSendMsgReply = 5;

    public static byte[] Wrap(int operation, byte[] body, ushort ver = 1)
    {
        var buf = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)buf.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), ver);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)operation);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 1); // seq_id
        body.CopyTo(buf, HeaderSize);
        return buf;
    }

    /// <summary>
    /// 解包一帧（可能含多个子包）；ver=2/3 时递归解压子包。
    /// 头部损坏或长度越界即停止（blivedm 语义：解析失败跳过该帧，不抛异常）。
    /// </summary>
    public static IEnumerable<(int Operation, byte[] Body)> Unwrap(byte[] frame)
    {
        foreach (var header in EnumeratePackets(frame))
        {
            if (header.Ver is 0 or 1)
            {
                yield return (header.Operation, header.Body);
            }
            else
            {
                // 解压负载损坏/未知版本: 跳过该子包继续(blivedm"坏包跳过"语义,
                // 审计 T3-1——此前 InvalidDataException 逃逸导致整条连接断开重连)。
                // 解压与 yield 分离: C# 不允许在带 catch 的 try 内 yield return。
                byte[] decompressed;
                try
                {
                    using var src = new MemoryStream(header.Body);
                    using var dec = header.Ver switch
                    {
                        2 => (Stream)new ZLibStream(src, CompressionMode.Decompress),
                        3 => new BrotliStream(src, CompressionMode.Decompress),
                        _ => throw new InvalidDataException($"未知压缩版本 ver={header.Ver}"),
                    };
                    using var outMs = new MemoryStream();
                    dec.CopyTo(outMs);
                    decompressed = outMs.ToArray();
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                foreach (var inner in Unwrap(decompressed))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>按 16 字节头逐个切片出（op, ver, body）；不处理压缩。</summary>
    public static IEnumerable<(int Operation, int Ver, byte[] Body)> EnumeratePackets(byte[] frame)
    {
        int offset = 0;
        while (offset + HeaderSize <= frame.Length)
        {
            uint packLen = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(offset, 4));
            if (packLen < HeaderSize || offset + packLen > frame.Length)
            {
                yield break;
            }

            var ver = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset + 6, 2));
            var operation = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(offset + 8, 4));
            var body = new byte[packLen - HeaderSize];
            Array.Copy(frame, offset + HeaderSize, body, 0, body.Length);
            offset += (int)packLen;
            yield return ((int)operation, ver, body);
        }
    }

    /// <summary>心跳回复 body 前 4 字节 = 人气值（大端）。</summary>
    public static int ReadPopularity(byte[] heartbeatBody) =>
        heartbeatBody.Length >= 4
            ? BinaryPrimitives.ReadInt32BigEndian(heartbeatBody.AsSpan(0, 4))
            : 0;
}
