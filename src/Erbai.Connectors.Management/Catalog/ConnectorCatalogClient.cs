using System.Net.Http.Headers;
using System.Text.Json;

namespace Erbai.Connectors.Management.Catalog;

/// <summary>
/// 拉取并校验上游 v2 连接器清单，带 5 分钟 TTL 缓存。
/// </summary>
/// <remarks>
/// 缓存是为了让「打开设置页 → 显示四个平台状态」这类 UI 行为不会每次都打上游；
/// <c>forceRefresh</c> 供「检查更新」按钮绕过缓存。
/// 时间源走 <see cref="TimeProvider"/>，便于单测直接推进时钟验证 TTL 过期。
/// </remarks>
public sealed class ConnectorCatalogClient
{
    /// <summary>上游 v2 清单地址。</summary>
    public const string DefaultCatalogUrl = "https://app.enkianss.us/connectors/v2/catalog.json";

    /// <summary>默认缓存有效期。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly HttpClient _http;
    private readonly string _catalogUrl;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<ConnectorCatalogEntry>? _cached;
    private IReadOnlyList<ConnectorCatalogRejection>? _cachedRejected;
    private DateTimeOffset _cachedAt;

    public ConnectorCatalogClient(
        HttpClient httpClient,
        string catalogUrl = DefaultCatalogUrl,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _catalogUrl = catalogUrl;
        _ttl = ttl ?? DefaultTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 返回已校验的连接器条目。命中缓存时不会发起网络请求。
    /// </summary>
    /// <exception cref="ConnectorManagementException">清单不可达或校验不通过。</exception>
    public async Task<IReadOnlyList<ConnectorCatalogEntry>> GetEntriesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ConnectorCatalogSnapshot snapshot =
            await GetSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        return snapshot.Entries;
    }

    /// <summary>
    /// 返回已校验的清单快照（可用条目 + 被逐条拒绝的条目）。命中缓存时不会发起网络请求。
    /// </summary>
    /// <remarks>
    /// 需要拒绝原因时用这个方法：设置页要能告诉用户"某平台的最新版协议不兼容"，
    /// 而不是让它从列表里静默消失。
    /// </remarks>
    /// <exception cref="ConnectorManagementException">清单不可达或校验不通过。</exception>
    public async Task<ConnectorCatalogSnapshot> GetSnapshotAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryGetFresh(out ConnectorCatalogSnapshot? cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双重检查：并发调用时只让第一个真正去拉取。
            if (!forceRefresh && TryGetFresh(out cached))
            {
                return cached;
            }

            ConnectorCatalogSnapshot snapshot = await FetchAsync(cancellationToken)
                .ConfigureAwait(false);
            _cached = snapshot.Entries;
            _cachedRejected = snapshot.Rejected;
            _cachedAt = _timeProvider.GetUtcNow();
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>查找指定平台的最新条目；不存在返回 <see langword="null"/>。</summary>
    public async Task<ConnectorCatalogEntry?> FindAsync(
        string playerKey,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ConnectorCatalogEntry> entries =
            await GetEntriesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Id, playerKey, StringComparison.Ordinal));
    }

    /// <summary>清空缓存，下次调用必定重新拉取。</summary>
    public void Invalidate()
    {
        _cached = null;
        _cachedRejected = null;
    }

    private bool TryGetFresh(out ConnectorCatalogSnapshot cached)
    {
        cached = _cached is null
            ? null!
            : new ConnectorCatalogSnapshot(_cached, _cachedRejected ?? []);

        return _cached is not null && _timeProvider.GetUtcNow() - _cachedAt < _ttl;
    }

    private async Task<ConnectorCatalogSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _catalogUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorManagementException(
                    $"连接器清单请求失败：HTTP {(int)response.StatusCode}。");
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectorManagementException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConnectorManagementException($"连接器清单不可达：{ex.Message}", ex);
        }

        return ParseWithRejections(json);
    }

    /// <summary>解析并校验清单文本。暴露为 internal 供单测直接调用。</summary>
    internal static IReadOnlyList<ConnectorCatalogEntry> Parse(string json) =>
        ParseWithRejections(json).Entries;

    /// <summary>解析并校验清单文本，保留被逐条拒绝的条目。暴露为 internal 供单测直接调用。</summary>
    internal static ConnectorCatalogSnapshot ParseWithRejections(string json)
    {
        ConnectorCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<ConnectorCatalog>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ConnectorManagementException($"连接器清单不是合法 JSON：{ex.Message}", ex);
        }

        if (catalog is null)
        {
            throw new ConnectorManagementException("连接器清单内容为空。");
        }

        return ConnectorCatalogValidator.ValidateWithRejections(catalog);
    }
}
