using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Erbai.Live.Douyin.Tests;

/// <summary>
/// 测试用抖音 Grabber WS 假服务器：原生 TCP 最小 WebSocket 服务端（握手 + 文本帧收发 +
/// 掩码解包）。无 HttpListener（免 URL ACL）、无第三方包。对齐阶段 3 FakeBiliWsServer 模式。
/// </summary>
internal sealed class FakeDouyinWsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<WsClient> _clients = new();
    private readonly ConcurrentQueue<string> _received = new();

    public FakeDouyinWsServer(int? port = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>已收到的客户端文本消息（帧 payload，含 {Type,Data} 命令等）。</summary>
    public IReadOnlyCollection<string> Received => _received;

    public async Task WaitForCountAsync(int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_received.Count >= count)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"等待 WS 消息×{count} 超时；当前 {_received.Count}");
    }

    /// <summary>向所有客户端推送一条文本报文（{Type, Data} 信封 JSON）。</summary>
    public async Task PushAsync(string json)
    {
        var frame = ServerFrame(Encoding.UTF8.GetBytes(json));
        foreach (var client in _clients)
        {
            try
            {
                await client.SendAsync(frame);
            }
            catch (IOException)
            {
            }
        }
    }

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
            var key = await ReadHandshakeAsync(stream);
            if (key is null)
            {
                return;
            }

            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);

            var ws = new WsClient(stream, _received);
            _clients.Enqueue(ws);
            await ws.RunAsync(_cts.Token);
            _clients.TryDequeue(out _);
        }
    }

    private static async Task<string?> ReadHandshakeAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var ms = new MemoryStream();
        while (ms.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(ms.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                foreach (var line in text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n").Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    if (line[..colon].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    {
                        return line[(colon + 1)..].Trim();
                    }
                }

                return null;
            }
        }

        return null;
    }

    private static byte[] ServerFrame(byte[] payload)
    {
        var result = new List<byte> { 0x81 };
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

    /// <summary>单个客户端连接：读帧（解掩码）→ 记录文本。</summary>
    private sealed class WsClient : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<string> _received;

        public WsClient(NetworkStream stream, ConcurrentQueue<string> received)
        {
            _stream = stream;
            _received = received;
        }

        public Task SendAsync(byte[] frame, CancellationToken ct = default) =>
            _stream.WriteAsync(frame, ct).AsTask();

        public async Task RunAsync(CancellationToken ct)
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
                        await _stream.WriteAsync(new byte[] { 0x88, 0x00 }, ct);
                        return;
                    }

                    if (opcode != 1 || len == 0)
                    {
                        continue;
                    }

                    _received.Enqueue(Encoding.UTF8.GetString(payload));
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
