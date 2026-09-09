using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Erbai.Connector.Tests;

/// <summary>lxmusic 测试用 fake handler：按路径路由 + SSE 推送流。</summary>
public sealed class FakeLxHandler : HttpMessageHandler
{
    /// <summary>路径 → 响应体（null = 非 200）。</summary>
    public Dictionary<string, string> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>路径 → 抛出的异常。</summary>
    public Dictionary<string, Exception> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SSE 推送流（按路径）。</summary>
    public Dictionary<string, SsePushStream> SseStreams { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RequestedPaths { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        RequestedPaths.Add(request.RequestUri.ToString());
        if (Failures.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        if (SseStreams.TryGetValue(path, out var stream))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { },
            });
        }

        if (Routes.TryGetValue(path, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>可推送的 SSE 响应流：写端 Push 文本，读端 ReadAsync 逐行阻塞。</summary>
public sealed class SsePushStream : Stream
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly MemoryStream _buffer = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Push(string line) => _lines.Writer.TryWrite(line);

    public void CloseWriter() => _lines.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_buffer.Length == 0)
        {
            string? line;
            try
            {
                line = await _lines.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return 0; // EOF
            }

            if (line is null)
            {
                return 0;
            }

            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _buffer.Write(bytes);
            _buffer.Position = 0;
        }

        var read = _buffer.Read(buffer.Span);
        if (_buffer.Position == _buffer.Length)
        {
            _buffer.SetLength(0);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
