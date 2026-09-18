using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Erbai.Connectors.Management;

namespace Erbai.Connectors.Management.Tests;

/// <summary>用完即删的临时目录。所有涉及落盘的用例都落在临时目录，绝不碰真实安装根。</summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "erbai-connmgmt-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试清理是尽力而为。
        }
    }
}

/// <summary>可编程的 HTTP 消息处理器，用来替掉真实网络。</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private readonly List<string> _requestUris = [];
    private int _callCount;

    internal StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    internal int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// 可选异步闸门：在调用 responder 之前先 await。
    /// </summary>
    /// <remarks>
    /// 内存替身默认是同步完成的（<c>Task.FromResult</c>），整条 provisioning 链路会在
    /// 调用线程上一口气跑完——于是"取消进行中的请求"这类断言根本等不到进行中的状态，
    /// 退化成竞态。挂上闸门才能真正把请求按在飞行中。
    /// </remarks>
    internal Func<HttpRequestMessage, Task>? Gate { get; set; }

    /// <summary>统计命中条件的请求数（用于验证"只拉了一次元数据"这类断言）。</summary>
    internal int CountRequests(Func<string, bool> predicate)
    {
        lock (_requestUris)
        {
            return _requestUris.Count(predicate);
        }
    }

    internal static HttpResponseMessage PartialContent(byte[] data, long start, long total)
    {
        ByteArrayContent content = new(data);
        content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + data.Length - 1, total);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    internal static HttpResponseMessage WholeContent(byte[] data) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_requestUris)
        {
            _requestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
        }

        // 计数在闸门之前自增：断言的是"请求已到达"，不是"请求已应答"。
        int call = Interlocked.Increment(ref _callCount);

        if (Gate is { } gate)
        {
            await gate(request).ConfigureAwait(false);
        }

        return _responder(request, call);
    }
}

/// <summary>永远返回指定结论的健康检查替身。</summary>
internal sealed class StubHealthChecker : IConnectorHealthChecker
{
    internal StubHealthChecker(bool healthy = true, string message = "stub")
    {
        IsHealthy = healthy;
        Message = message;
    }

    internal bool IsHealthy { get; set; }

    internal string Message { get; set; }

    internal int CallCount { get; set; }

    public Task<ConnectorHealthResult> CheckAsync(
        string executablePath,
        string playerKey,
        string? expectedVersion = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ConnectorHealthResult(IsHealthy, IsHealthy ? string.Empty : Message));
    }
}

/// <summary>
/// 读走真实存储、写必定失败的存储替身。用来把安装器逼进回滚分支——
/// 这是"更新失败不能把能用的旧版本也弄没"这一保证的唯一可测入口。
/// </summary>
internal sealed class FailingWriteStore : IConnectorStore
{
    private readonly IConnectorStore _inner;

    internal FailingWriteStore(IConnectorStore inner) => _inner = inner;

    public ActiveConnector? ReadActive(string playerKey) => _inner.ReadActive(playerKey);

    public void WriteActive(string playerKey, ActiveConnector active) =>
        throw new IOException("模拟 active.json 写入失败");

    public void DeleteActive(string playerKey) => _inner.DeleteActive(playerKey);

    public bool IsInstalled(string playerKey) => _inner.IsInstalled(playerKey);
}

/// <summary>
/// 同步收集进度回报。<see cref="Progress{T}"/> 会把回调投递到同步上下文，
/// 在测试里是非确定性的，所以用这个替身。
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    internal List<T> Items { get; } = [];

    public void Report(T value) => Items.Add(value);
}

/// <summary>构造测试用 ZIP / 归档字节。</summary>
internal static class ArchiveBuilder
{
    /// <summary>按给定条目名与内容构造 ZIP。</summary>
    internal static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        return CreateZipBytes([.. entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content)))]);
    }

    /// <summary>按给定条目名与二进制内容构造 ZIP（用于构造大体量、不可压缩的条目）。</summary>
    internal static byte[] CreateZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 构造一个"看起来像连接器发布包"的 ZIP：顶层含指定可执行文件名。
    /// 内容不是真的 PE 文件，仅供安装管线测试（健康检查用替身）。
    /// </summary>
    internal static byte[] CreateConnectorPackage(string executableName, string marker = "stub")
    {
        return CreateZip(
            (executableName, marker),
            ("Awoo.Connector.deps.json", "{}"),
            ("README.txt", "test package"));
    }
}

/// <summary>
/// 可控时钟 + 可控定时器。
/// </summary>
/// <remarks>
/// 用来验证 30 分钟周期轮询而不真的等 30 分钟。同时充当
/// <c>ConnectorCatalogClient</c> 的 TTL 时钟，因此推进时间会同时让缓存过期——
/// 这一点必须记住，否则会出现"以为在测定时器、其实在测缓存"的假通过。
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>已创建且未释放的定时器数量。</summary>
    internal int ActiveTimerCount
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        FakeTimer timer = new(this, callback, state, dueTime, period);

        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>推进时钟，并让所有到期的定时器各触发一次。</summary>
    internal void Advance(TimeSpan delta)
    {
        _now += delta;

        FakeTimer[] snapshot;
        lock (_timers)
        {
            snapshot = [.. _timers];
        }

        foreach (FakeTimer timer in snapshot)
        {
            timer.FireIfDue(_now);
        }
    }

    internal void Remove(FakeTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    internal sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly TimeSpan _period;

        private DateTimeOffset? _dueAt;
        private bool _disposed;

        internal FakeTimer(
            FakeTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            _dueAt = ResolveDue(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void FireIfDue(DateTimeOffset now)
        {
            if (_disposed || _dueAt is null || now < _dueAt.Value)
            {
                return;
            }

            _dueAt = _period > TimeSpan.Zero ? now + _period : null;
            _callback(_state);
        }

        private DateTimeOffset? ResolveDue(TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
    }
}

/// <summary>轮询等待条件成立。用于验证异步副作用，避免用固定 sleep 猜时间。</summary>
internal static class TestWait
{
    internal static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.True(condition(), because);
    }
}
