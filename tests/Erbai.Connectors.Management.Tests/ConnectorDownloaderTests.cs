using System.Net;
using System.Net.Http.Headers;

namespace Erbai.Connectors.Management.Tests;

/// <summary>
/// <see cref="ConnectorDownloader"/> 的分块 / 重试 / 回退行为。
/// </summary>
/// <remarks>
/// 重试退避传 <see cref="TimeSpan.Zero"/>，否则失败路径的用例要真等好几秒。
/// </remarks>
public class ConnectorDownloaderTests
{
    private const string Url = "https://app.enkianss.us/connectors/v2/download/netease/1.0.0/pkg.zip";

    private static ConnectorDownloader CreateDownloader(
        StubHttpMessageHandler handler,
        int maxAttempts = 5) =>
        new(new HttpClient(handler), TimeSpan.Zero, maxAttempts, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task DownloadAsync_AcceptsSingleChunk()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        StubHttpMessageHandler handler = new((_, _) =>
            StubHttpMessageHandler.PartialContent(payload, 0, payload.Length));

        byte[] result = await CreateDownloader(handler).DownloadAsync(Url, payload.Length);

        Assert.Equal(payload, result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_AssemblesMultipleChunks()
    {
        int total = ConnectorDownloader.DefaultChunkSize + 10;
        byte[] payload = new byte[total];
        Random.Shared.NextBytes(payload);

        StubHttpMessageHandler handler = new((request, _) =>
        {
            (long start, long end) = ReadRange(request);
            byte[] slice = payload[(int)start..(int)(end + 1)];
            return StubHttpMessageHandler.PartialContent(slice, start, total);
        });

        byte[] result = await CreateDownloader(handler).DownloadAsync(Url, total);

        Assert.Equal(total, result.Length);
        Assert.Equal(payload, result);
        Assert.Equal(2, handler.CallCount);
    }

    /// <summary>服务器不支持 Range、直接返回整包（200）时应当接受，而不是报错。</summary>
    [Fact]
    public async Task DownloadAsync_AcceptsWholeResponseWhenServerIgnoresRange()
    {
        byte[] payload = [9, 8, 7];
        StubHttpMessageHandler handler = new((_, _) =>
            StubHttpMessageHandler.WholeContent(payload));

        byte[] result = await CreateDownloader(handler).DownloadAsync(Url, payload.Length);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task DownloadAsync_RetriesThenSucceeds()
    {
        byte[] payload = [1, 2, 3];
        StubHttpMessageHandler handler = new((_, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : StubHttpMessageHandler.PartialContent(payload, 0, payload.Length));

        List<ConnectorDownloadRetry> retries = [];
        byte[] result = await CreateDownloader(handler)
            .DownloadAsync(Url, payload.Length, onRetry: retries.Add);

        Assert.Equal(payload, result);
        Assert.Equal(2, handler.CallCount);
        Assert.Single(retries);
        Assert.Equal(1, retries[0].Attempt);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsAfterExhaustingAttempts()
    {
        StubHttpMessageHandler handler = new((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        List<ConnectorDownloadRetry> retries = [];
        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateDownloader(handler, maxAttempts: 3)
                .DownloadAsync(Url, 100, onRetry: retries.Add));

        Assert.Equal(3, handler.CallCount);
        Assert.Equal(2, retries.Count);
        Assert.Contains("下载分块", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Content-Range 与实际请求不一致说明服务器给错了区间，必须拒绝而不是拼接出错误的归档。
    /// </summary>
    [Fact]
    public async Task DownloadAsync_RejectsMismatchedContentRange()
    {
        byte[] payload = [1, 2, 3];
        StubHttpMessageHandler handler = new((_, _) =>
            StubHttpMessageHandler.PartialContent(payload, 0, 999));

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateDownloader(handler, maxAttempts: 1).DownloadAsync(Url, payload.Length));
    }

    [Fact]
    public async Task DownloadAsync_RejectsChunkWithWrongLength()
    {
        StubHttpMessageHandler handler = new((_, _) =>
            StubHttpMessageHandler.PartialContent([1, 2], 0, 3));

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateDownloader(handler, maxAttempts: 1).DownloadAsync(Url, 3));
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToAlternateUrl()
    {
        const string fallback = "https://github.com/Enkianssus/awoo-connectors/releases/download/netease-v1.0.0/pkg.zip";
        byte[] payload = [4, 5, 6];

        StubHttpMessageHandler handler = new((request, _) =>
            request.RequestUri!.ToString() == Url
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : StubHttpMessageHandler.PartialContent(payload, 0, payload.Length));

        byte[] result = await CreateDownloader(handler, maxAttempts: 1)
            .DownloadAsync(Url, payload.Length, fallback);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task DownloadAsync_ReportsBothFailuresWhenFallbackAlsoFails()
    {
        StubHttpMessageHandler handler = new((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        ConnectorManagementException ex = await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateDownloader(handler, maxAttempts: 1)
                .DownloadAsync(Url, 100, "https://github.com/fallback/pkg.zip"));

        Assert.Contains("主站", ex.Message, StringComparison.Ordinal);
        Assert.Contains("回退地址", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999L * 1024 * 1024 * 1024)]
    public async Task DownloadAsync_RejectsImplausibleSizes(long expectedSize)
    {
        StubHttpMessageHandler handler = new((_, _) =>
            StubHttpMessageHandler.WholeContent([1]));

        await Assert.ThrowsAsync<ConnectorManagementException>(
            () => CreateDownloader(handler).DownloadAsync(Url, expectedSize));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgressPerChunk()
    {
        int total = ConnectorDownloader.DefaultChunkSize + 10;
        byte[] payload = new byte[total];

        StubHttpMessageHandler handler = new((request, _) =>
        {
            (long start, long end) = ReadRange(request);
            return StubHttpMessageHandler.PartialContent(payload[(int)start..(int)(end + 1)], start, total);
        });

        SyncProgress<ConnectorDownloadProgress> progress = new();
        byte[] result = await CreateDownloader(handler).DownloadAsync(Url, total, progress: progress);

        Assert.Equal(total, result.Length);

        // 两个分块 → 两次进度回报，最后一次必须是 100%。
        Assert.Equal(2, progress.Items.Count);
        Assert.Equal(100, progress.Items[^1].Percent);
        Assert.Equal(total, progress.Items[^1].Received);
    }

    private static (long Start, long End) ReadRange(HttpRequestMessage request)
    {
        RangeItemHeaderValue range = request.Headers.Range!.Ranges.First();
        return (range.From!.Value, range.To!.Value);
    }
}
