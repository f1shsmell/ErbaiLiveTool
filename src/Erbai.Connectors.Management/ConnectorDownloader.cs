using System.Net;
using System.Net.Http.Headers;

namespace Erbai.Connectors.Management;

/// <summary>下载进度。</summary>
public sealed record ConnectorDownloadProgress(long Received, long Total, int Percent);

/// <summary>一次分块下载重试事件，供日志与 UI 展示。</summary>
public sealed record ConnectorDownloadRetry(int Attempt, int MaxAttempts, long Start, long End, string Error);

/// <summary>
/// 连接器归档下载器：分块 Range（2 MB）+ 单请求超时 + 线性退避重试，站点失败时回退 GitHub Release 直链。
/// </summary>
/// <remarks>
/// 分块的意义不是"更快"，而是<b>可恢复</b>：单个 7 MB 请求在弱网下中途断掉就要整包重来，
/// 分块后只重试失败的那 2 MB。若服务器不支持 Range（返回 200 而非 206），代码会识别出
/// "整包已给"的情况并直接采用，而不是报错——这是参考实现的行为，也避免了硬依赖 Range。
/// </remarks>
public sealed class ConnectorDownloader
{
    /// <summary>分块大小。</summary>
    public const int DefaultChunkSize = 2 * 1024 * 1024;

    /// <summary>单个分块的最大尝试次数。</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>单个请求超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>重试退避基数（实际等待 = 基数 × 第几次尝试）。</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>归档大小上限，与清单校验保持一致。</summary>
    private const long MaxArchiveBytes = 256L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;

    /// <param name="httpClient">用于发请求的客户端（测试可注入假 handler）。</param>
    /// <param name="retryDelay">重试退避基数；传 <see cref="TimeSpan.Zero"/> 可让测试免等待。</param>
    /// <param name="maxAttempts">单个分块的最大尝试次数。</param>
    /// <param name="timeout">单个请求超时。</param>
    public ConnectorDownloader(
        HttpClient httpClient,
        TimeSpan? retryDelay = null,
        int? maxAttempts = null,
        TimeSpan? timeout = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxAttempts = maxAttempts ?? DefaultMaxAttempts;
        _timeout = timeout ?? DefaultTimeout;

        if (_maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "重试次数必须为正数。");
        }
    }

    /// <summary>
    /// 下载归档。主地址整体失败时，若提供了 <paramref name="fallbackUrl"/> 则改用回退地址重试一次。
    /// </summary>
    /// <exception cref="ConnectorManagementException">主地址与回退地址都失败，或大小不符合预期。</exception>
    public async Task<byte[]> DownloadAsync(
        string url,
        long expectedSize,
        string? fallbackUrl = null,
        IProgress<ConnectorDownloadProgress>? progress = null,
        Action<ConnectorDownloadRetry>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize <= 0 || expectedSize > MaxArchiveBytes)
        {
            throw new ConnectorManagementException($"下载目标大小超出允许范围：{expectedSize}。");
        }

        try
        {
            return await DownloadWithRangesAsync(
                url, expectedSize, progress, onRetry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception primary)
        {
            if (string.IsNullOrEmpty(fallbackUrl))
            {
                throw new ConnectorManagementException($"下载失败：{primary.Message}", primary);
            }

            progress?.Report(new ConnectorDownloadProgress(0, expectedSize, 0));
            onRetry?.Invoke(new ConnectorDownloadRetry(0, DefaultMaxAttempts, 0, expectedSize - 1,
                $"主站失败，改用回退地址：{primary.Message}"));

            try
            {
                return await DownloadWithRangesAsync(
                    fallbackUrl, expectedSize, progress, onRetry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallback)
            {
                throw new ConnectorManagementException(
                    $"下载失败：主站 {primary.Message}；回退地址 {fallback.Message}", fallback);
            }
        }
    }

    private async Task<byte[]> DownloadWithRangesAsync(
        string url,
        long expectedSize,
        IProgress<ConnectorDownloadProgress>? progress,
        Action<ConnectorDownloadRetry>? onRetry,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new((int)expectedSize);
        long received = 0;

        while (received < expectedSize)
        {
            long end = Math.Min(received + DefaultChunkSize - 1, expectedSize - 1);

            (byte[] data, bool completeArchive) = await DownloadRangeAsync(
                url, expectedSize, received, end, onRetry, cancellationToken).ConfigureAwait(false);

            if (completeArchive)
            {
                progress?.Report(new ConnectorDownloadProgress(expectedSize, expectedSize, 100));
                return data;
            }

            buffer.Write(data, 0, data.Length);
            received += data.Length;

            progress?.Report(new ConnectorDownloadProgress(
                received,
                expectedSize,
                (int)Math.Min(100, received * 100 / expectedSize)));
        }

        byte[] archive = buffer.ToArray();
        if (archive.LongLength != expectedSize)
        {
            throw new ConnectorManagementException(
                $"下载结果大小不匹配：{archive.LongLength}/{expectedSize}。");
        }

        return archive;
    }

    private async Task<(byte[] Data, bool CompleteArchive)> DownloadRangeAsync(
        string url,
        long expectedSize,
        long start,
        long end,
        Action<ConnectorDownloadRetry>? onRetry,
        CancellationToken cancellationToken)
    {
        long expectedRangeSize = end - start + 1;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= DefaultMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(DefaultTimeout);

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

                using HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new ConnectorManagementException(
                        $"下载 HTTP {(int)response.StatusCode}（{response.StatusCode}）。");
                }

                byte[] data = await response.Content
                    .ReadAsByteArrayAsync(timeoutSource.Token)
                    .ConfigureAwait(false);

                // 服务器忽略 Range，直接把整包给了我们。仅在确实是第一个分块且长度吻合时接受。
                if (response.StatusCode == HttpStatusCode.OK
                    && start == 0
                    && data.LongLength == expectedSize)
                {
                    return (data, true);
                }

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new ConnectorManagementException(
                        $"下载服务器未返回分块响应：HTTP {(int)response.StatusCode}。");
                }

                (long rangeStart, long rangeEnd, long rangeTotal)? contentRange =
                    ParseContentRange(response.Content.Headers.ContentRange);

                if (contentRange is null
                    || contentRange.Value.rangeStart != start
                    || contentRange.Value.rangeEnd != end
                    || contentRange.Value.rangeTotal != expectedSize)
                {
                    throw new ConnectorManagementException(
                        $"下载分块范围不匹配：{response.Content.Headers.ContentRange?.ToString() ?? "缺失"}。");
                }

                if (data.LongLength != expectedRangeSize)
                {
                    throw new ConnectorManagementException(
                        $"下载分块大小不匹配：{data.LongLength}/{expectedRangeSize}。");
                }

                return (data, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex is OperationCanceledException
                    ? new ConnectorManagementException($"下载分块 {start}-{end} 超时。", ex)
                    : ex;

                if (attempt >= _maxAttempts)
                {
                    break;
                }

                onRetry?.Invoke(new ConnectorDownloadRetry(
                    attempt, _maxAttempts, start, end, lastError.Message));

                await Task.Delay(_retryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ConnectorManagementException(
            $"下载分块 {start}-{end} 失败：{lastError?.Message ?? "未知错误"}", lastError!);
    }

    private static (long rangeStart, long rangeEnd, long rangeTotal)? ParseContentRange(
        ContentRangeHeaderValue? value)
    {
        if (value is null || value.From is null || value.To is null || value.Length is null)
        {
            return null;
        }

        return (value.From.Value, value.To.Value, value.Length.Value);
    }
}
