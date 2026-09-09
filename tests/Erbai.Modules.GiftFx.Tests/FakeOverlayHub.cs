using System.Text.Json;
using Erbai.Contracts.Abstractions;

namespace Erbai.Modules.GiftFx.Tests;

/// <summary>记录发布内容的 fake OverlayHub（可注入延迟模拟慢消费者）。</summary>
internal sealed class FakeOverlayHub : IOverlayHub
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();

    /// <summary>每次推送模拟耗时（验证串行化：同一时间只播一个）。</summary>
    public int DelayMs { get; init; }

    public List<(string Channel, string PayloadJson)> Published { get; } = [];

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return Published.Count;
            }
        }
    }

    public async Task PublishAsync(string channel, object payload, CancellationToken ct = default)
    {
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, ct);
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        lock (_sync)
        {
            Published.Add((channel, json));
        }
    }

    public (string Channel, JsonElement Payload) At(int index)
    {
        lock (_sync)
        {
            var (channel, json) = Published[index];
            return (channel, JsonDocument.Parse(json).RootElement);
        }
    }
}
