using System.Text;
using System.Text.Json;
using Erbai.Contracts.Live;

namespace Erbai.Core.Logging;

/// <summary>
/// 直播事件落盘（logs/erbai-live-YYYYMMDD.log，按天滚动，UTF-8 JSON 行）。
/// 目的：直播事件（弹幕/礼物/进房…）只走 EventBus 内存广播，日志页没打开就永远错过；
/// 落盘后日志页打开时回看当天事件（docs/00 修复记录：日志页"只有工具类"）。
/// - 写入点：AppServices 平台 runner 桥接（bus.Publish 同处），B站/抖音共用；
/// - 行格式：单行 JSON（字段短名，正文不换行），可被 <see cref="TryParseLine"/> 读回；
/// - 失败不抛异常（与 LogBus 同铁律：日志路径绝不拖垮业务）；Dispose 刷缓冲。
/// </summary>
public sealed class LiveEventLogWriter : IDisposable
{
    private readonly string _directory;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private string? _currentDate;
    private bool _disposed;

    /// <summary>文件名前缀（erbai-live-20260903.log）。</summary>
    public const string FilePrefix = "erbai-live-";

    public LiveEventLogWriter(string logDirectory)
    {
        _directory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    /// <summary>追加一条直播事件（线程安全；非直播事件也记录，事件全部落盘）。</summary>
    public void Write(LiveEvent evt)
    {
        if (evt is null)
        {
            return;
        }

        try
        {
            var line = Serialize(evt);
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                EnsureWriter();
                _writer!.WriteLine(line);
            }
        }
        catch
        {
            // 落盘失败绝不抛（事件循环继续）
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;
        }
    }

    // ── 写文件管理（按天滚动） ────────────────────────────────────────────

    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyyMMdd");
        if (_writer is not null && _currentDate == date)
        {
            return;
        }

        _writer?.Flush();
        _writer?.Dispose();
        _currentDate = date;
        var path = Path.Combine(_directory, $"{FilePrefix}{date}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    // ── 序列化 / 反序列化（日志页回看共用；字段短名紧凑行） ───────────────

    /// <summary>单行 JSON：t=时间(p ISO8601) p=平台 rid=房间 k=类型 uid n=昵称 x=正文 g=礼物名
    /// gc=礼物数 coin=价值 ct=币种 admin/anchor=特权 fan=粉丝团 med=勋章。</summary>
    public static string Serialize(LiveEvent evt) =>
        JsonSerializer.Serialize(new
        {
            t = evt.Timestamp.ToString("o"),
            p = evt.Platform,
            rid = evt.RoomId,
            k = evt.Kind.ToString(),
            uid = evt.UserId,
            n = evt.Nickname,
            x = evt.Text,
            g = evt.GiftName,
            gc = evt.GiftCount,
            coin = evt.TotalCoin,
            ct = evt.CoinType,
            admin = evt.IsAdmin,
            anchor = evt.IsAnchor,
            fan = evt.FanLevel,
            med = evt.MedalLevel,
        });

    /// <summary>解析一行 JSON 回 <see cref="LiveEvent"/>（坏行返回 null，不抛出）。</summary>
    public static LiveEvent? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var kindText = GetString(root, "k");
            if (kindText is null || !Enum.TryParse<LiveEventKind>(kindText, out var kind))
            {
                return null;
            }

            var timestampText = GetString(root, "t");
            if (timestampText is null ||
                !DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return null;
            }

            return new LiveEvent
            {
                Platform = GetString(root, "p") ?? "",
                RoomId = GetString(root, "rid") ?? "",
                Kind = kind,
                UserId = GetInt64(root, "uid") ?? 0,
                Nickname = GetString(root, "n") ?? "",
                Text = GetString(root, "x"),
                GiftName = GetString(root, "g"),
                GiftCount = GetInt32(root, "gc") ?? 0,
                TotalCoin = GetInt64(root, "coin") ?? 0,
                CoinType = GetString(root, "ct"),
                IsAdmin = GetBool(root, "admin"),
                IsAnchor = GetBool(root, "anchor"),
                FanLevel = GetInt32(root, "fan"),
                MedalLevel = GetInt32(root, "med"),
                Timestamp = timestamp,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读取某日志文件全部事件（坏行跳过；供日志页回看加载当天）。</summary>
    public static List<LiveEvent> ReadFile(string path)
    {
        var events = new List<LiveEvent>();
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var line in lines)
            {
                var evt = TryParseLine(line);
                if (evt is not null)
                {
                    events.Add(evt);
                }
            }
        }
        catch
        {
            // 读失败返回已解析部分（不抛）
        }

        return events;
    }

    private static string? GetString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetInt64(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? GetInt32(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool? GetBool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}