using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Erbai.Live.Bilibili.Protocol;

namespace Erbai.Live.Bilibili.Tests;

/// <summary>
/// 测试用 B站弹幕 WS 假服务器：原生 TCP 实现最小 WebSocket 服务端
/// （握手 + 二进制帧收发 + 掩码解包），按 BiliFrame 协议应答。
/// 无 HttpListener（免 URL ACL）、无第三方包。
/// </summary>
internal sealed class FakeBiliWsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<WsClient> _clients = new();
    private readonly ConcurrentQueue<(int Op, string? Json)> _received = new();

    public FakeBiliWsServer(int? port = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>已收到的客户端帧（op, body JSON；心跳 body 为空串）。</summary>
    public IReadOnlyCollection<(int Op, string? Json)> Received => _received;

    /// <summary>各客户端的 WS 握手 Cookie 头。</summary>
    public IReadOnlyList<string> CookieHeaders => _clients.Select(c => c.CookieHeader).Where(h => h is not null).Cast<string>().ToList();

    /// <summary>true 时认证回复 code=-101（触发插件 AuthError → 重新 init_room）。</summary>
    public bool AuthReject { get; set; }

    public async Task WaitForOpAsync(int op, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_received.Any(x => x.Op == op))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"等待 op={op} 超时；已收到: {string.Join(",", _received.Select(x => x.Op))}");
    }

    public async Task WaitForCountAsync(int op, int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_received.Count(x => x.Op == op) >= count)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"等待 op={op}×{count} 超时；当前 {_received.Count(x => x.Op == op)}");
    }

    /// <summary>向所有客户端推送一条业务消息（cmd JSON；brotli=true 时 ver=3 压缩整帧，对齐 blivedm 语义）。</summary>
    public async Task PushAsync(string cmdJson, bool brotli = false)
    {
        var body = Encoding.UTF8.GetBytes(cmdJson);
        var inner = BiliFrame.Wrap(BiliFrame.OperationSendMsgReply, body);
        var frame = brotli
            ? BiliFrame.Wrap(BiliFrame.OperationSendMsgReply, Compress(inner, brotli: true), ver: 3)
            : inner;
        foreach (var client in _clients)
        {
            try
            {
                await client.SendAsync(ServerFrame(frame));
            }
            catch (IOException)
            {
                PushFailures++;
            }
        }
    }

    /// <summary>推送时写失败的次数（诊断用）。</summary>
    public int PushFailures { get; private set; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var (headers, key, cookieHeader) = await ReadHandshakeAsync(stream);
            if (key is null)
            {
                return;
            }

            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);

            var ws = new WsClient(stream, cookieHeader, () => AuthReject);
            _clients.Enqueue(ws);
            await ws.RunAsync(_received, _cts.Token);
            _clients.TryDequeue(out _);
        }
    }

    private static async Task<(string Headers, string? Key, string? CookieHeader)> ReadHandshakeAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var ms = new MemoryStream();
        while (ms.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return ("", null, null);
            }

            ms.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(ms.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return ExtractHeaders(text);
            }
        }

        return ("", null, null);
    }

    private static (string Headers, string? Key, string? CookieHeader) ExtractHeaders(string text)
    {
        var headerText = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        string? key = null;
        string? cookieHeader = null;
        foreach (var line in headerText.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
            else if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookieHeader = value;
            }
        }

        return (headerText, key, cookieHeader);
    }

    private static byte[] ServerFrame(byte[] payload)
    {
        var result = new List<byte> { 0x82 };
        if (payload.Length < 126)
        {
            result.Add((byte)payload.Length);
        }
        else if (payload.Length < 65536)
        {
            result.Add(126);
            result.Add((byte)(payload.Length >> 8));
            result.Add((byte)payload.Length);
        }
        else
        {
            result.Add(127);
            for (var i = 7; i >= 0; i--)
            {
                result.Add((byte)((ulong)payload.Length >> (8 * i)));
            }
        }

        result.AddRange(payload);
        return result.ToArray();
    }

    private static byte[] Compress(byte[] data, bool brotli)
    {
        using var output = new MemoryStream();
        using (var compressor = brotli
                   ? (Stream)new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)
                   : new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _listener.Stop();
    }

    /// <summary>单个客户端连接：读帧（解掩码）→ 记录 op/body → 应答认证/关闭。</summary>
    private sealed class WsClient : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly Func<bool> _authReject;

        public WsClient(NetworkStream stream, string? cookieHeader, Func<bool> authReject)
        {
            _stream = stream;
            CookieHeader = cookieHeader;
            _authReject = authReject;
        }

        public string? CookieHeader { get; }

        public async Task SendAsync(byte[] frame, CancellationToken ct = default) =>
            await _stream.WriteAsync(frame, ct);

        public async Task RunAsync(ConcurrentQueue<(int Op, string? Json)> received, CancellationToken ct)
        {
            var header = new byte[2];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactlyAsync(_stream, header, ct))
                    {
                        return;
                    }

                    var opcode = header[0] & 0x0f;
                    var masked = (header[1] & 0x80) != 0;
                    var len = header[1] & 0x7f;
                    if (len == 126)
                    {
                        var ext = new byte[2];
                        if (!await ReadExactlyAsync(_stream, ext, ct))
                        {
                            return;
                        }

                        len = (ext[0] << 8) | ext[1];
                    }
                    else if (len == 127)
                    {
                        var ext = new byte[8];
                        if (!await ReadExactlyAsync(_stream, ext, ct))
                        {
                            return;
                        }

                        len = (int)((ulong)ext[0] << 56 | (ulong)ext[1] << 48 | (ulong)ext[2] << 40 | (ulong)ext[3] << 32 |
                                    (ulong)ext[4] << 24 | (ulong)ext[5] << 16 | (ulong)ext[6] << 8 | ext[7]);
                    }

                    byte[] mask = [];
                    if (masked)
                    {
                        mask = new byte[4];
                        if (!await ReadExactlyAsync(_stream, mask, ct))
                        {
                            return;
                        }
                    }

                    var payload = new byte[len];
                    if (len > 0 && !await ReadExactlyAsync(_stream, payload, ct))
                    {
                        return;
                    }

                    if (masked)
                    {
                        for (var i = 0; i < payload.Length; i++)
                        {
                            payload[i] ^= mask[i & 3];
                        }
                    }

                    if (opcode == 8)
                    {
                        // 客户端关闭：回关闭帧
                        await _stream.WriteAsync(new byte[] { 0x88, 0x00 }, ct);
                        return;
                    }

                    if (opcode != 2 || payload.Length < 16)
                    {
                        continue;
                    }

                    var op = BiliFrame.EnumeratePackets(payload).FirstOrDefault().Operation;
                    var body = payload.Length > 16 ? payload[16..] : [];
                    received.Enqueue((op, body.Length == 0 ? "" : Encoding.UTF8.GetString(body)));
                    if (op == BiliFrame.OperationAuth)
                    {
                        var reply = _authReject()
                            ? """{"code":-101,"message":"风控拒绝"}"""
                            : """{"code":0,"message":"ok"}""";
                        await _stream.WriteAsync(ServerFrame(BiliFrame.Wrap(BiliFrame.OperationAuthReply, Encoding.UTF8.GetBytes(reply))), ct);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
