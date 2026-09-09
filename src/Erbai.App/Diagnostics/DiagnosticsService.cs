using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Erbai.App.Services;
using Erbai.Core.Configuration;
using Erbai.Core.Hosting;
using Erbai.Core.Storage;

namespace Erbai.App.Diagnostics;

/// <summary>诊断项（名称 / 级别 / 详情）。</summary>
public sealed record DiagnosticItem(string Name, DiagnosticLevel Level, string Detail);

public enum DiagnosticLevel
{
    Ok,
    Warn,
    Fail,
}

/// <summary>
/// 诊断服务（阶段 6 M7）：7 组检查（应用/环境/配置/数据库/Overlay/平台模块/
/// 播放器）+ 脱敏导出（token 打码，凭据不导出）。纯收集，页面只做展示。
/// </summary>
public static class DiagnosticsService
{
    public static IReadOnlyList<DiagnosticItem> Collect(AppServices services)
    {
        var items = new List<DiagnosticItem>();
        CollectApp(items);
        CollectEnvironment(items);
        CollectConfig(items, services);
        CollectStorage(items, services);
        CollectOverlay(items, services);
        CollectPlatforms(items, services);
        CollectPlayer(items, services);
        return items;
    }

    /// <summary>导出文本（脱敏）。</summary>
    public static string BuildReport(AppServices services)
    {
        var builder = new StringBuilder();
        builder.AppendLine("=== ErbaiLiveTool 诊断报告 ===");
        builder.AppendLine($"生成时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        foreach (var item in Collect(services))
        {
            builder.AppendLine($"[{LevelText(item.Level)}] {item.Name}: {item.Detail}");
        }

        builder.AppendLine();
        builder.AppendLine("--- 说明 ---");
        builder.AppendLine("本报告已脱敏：token 打码、凭据不导出。请勿直接粘贴完整配置/日志文件。");
        return builder.ToString();
    }

    private static void CollectApp(List<DiagnosticItem> items)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "?";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        items.Add(new DiagnosticItem(
            "应用版本",
            DiagnosticLevel.Ok,
            $"{informational} (assembly {version})"));
        items.Add(new DiagnosticItem(
            "进程",
            DiagnosticLevel.Ok,
            $"PID={Environment.ProcessId} 架构={RuntimeInformation.ProcessArchitecture}"));
    }

    private static void CollectEnvironment(List<DiagnosticItem> items)
    {
        items.Add(new DiagnosticItem(
            "运行时",
            DiagnosticLevel.Ok,
            $".NET {Environment.Version} ({RuntimeInformation.FrameworkDescription})"));
        items.Add(new DiagnosticItem(
            "操作系统",
            DiagnosticLevel.Ok,
            $"{RuntimeInformation.OSDescription}"));
        items.Add(new DiagnosticItem(
            "内存占用",
            DiagnosticLevel.Ok,
            $"{GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0:F1} MB（托管堆）"));
    }

    private static void CollectConfig(List<DiagnosticItem> items, AppServices services)
    {
        var config = services.Config;
        var fileExists = File.Exists(config.ConfigPath);
        var fileInfo = fileExists ? new FileInfo(config.ConfigPath) : null;
        items.Add(new DiagnosticItem(
            "配置文件",
            fileExists ? DiagnosticLevel.Ok : DiagnosticLevel.Fail,
            fileExists
                ? $"{config.ConfigPath}（{fileInfo!.Length / 1024.0:F1} KB）"
                : $"配置路径不存在：{config.ConfigPath}"));
        try
        {
            var validated = ConfigValidator.Validate(config.Settings);
            items.Add(new DiagnosticItem("配置校验", DiagnosticLevel.Ok, "全部配置段通过校验"));
        }
        catch (Exception ex)
        {
            items.Add(new DiagnosticItem("配置校验", DiagnosticLevel.Fail, ex.Message));
        }
    }

    private static void CollectStorage(List<DiagnosticItem> items, AppServices services)
    {
        var storage = services.Storage;
        var fileExists = File.Exists(storage.DatabasePath);
        var fileInfo = fileExists ? new FileInfo(storage.DatabasePath) : null;
        items.Add(new DiagnosticItem(
            "数据库",
            fileExists ? DiagnosticLevel.Ok : DiagnosticLevel.Fail,
            fileExists
                ? $"{storage.DatabasePath}（{fileInfo!.Length / 1024.0:F1} KB）"
                : $"数据库文件不存在：{storage.DatabasePath}"));
        items.Add(new DiagnosticItem(
            "数据库 schema",
            DiagnosticLevel.Ok,
            $"schema v{SqliteStorageEngine.SchemaVersion}"));
    }

    private static void CollectOverlay(List<DiagnosticItem> items, AppServices services)
    {
        if (services.Overlay is null)
        {
            items.Add(new DiagnosticItem("Overlay", DiagnosticLevel.Warn, "未装配（overlay 未启动）"));
            return;
        }

        var port = services.Overlay.BoundPort;
        items.Add(new DiagnosticItem(
            "Overlay 服务",
            port > 0 ? DiagnosticLevel.Ok : DiagnosticLevel.Warn,
            port > 0 ? $"端口 {port}（{services.OverlayUrl}）" : "尚未绑定端口"));
        // 2026-09：overlay token 已移除（回环绑定 + OBS 同机，见决策 #6 修订）——不再检查
    }

    private static void CollectPlatforms(List<DiagnosticItem> items, AppServices services)
    {
        foreach (var status in services.Supervisor.Status())
        {
            var level = status.Running ? DiagnosticLevel.Ok : status.LastError is not null ? DiagnosticLevel.Fail : DiagnosticLevel.Warn;
            var detail = status.Running
                ? "运行中"
                : status.LastError is not null
                    ? $"已停止（上次错误：{status.LastError}）"
                    : status.Stuck
                        ? "已停止（stuck：停止超时仍存活，禁止重启）"
                        : "未启动";
            items.Add(new DiagnosticItem($"平台 {status.Name}", level, detail));
        }

        foreach (var module in services.Modules.Modules)
        {
            var level = module.State switch
            {
                ModuleState.Started => DiagnosticLevel.Ok,
                ModuleState.StartFailed => DiagnosticLevel.Fail,
                ModuleState.Stopped => DiagnosticLevel.Warn,
                _ => DiagnosticLevel.Warn,
            };
            var detail = module.State switch
            {
                ModuleState.Started => "运行中",
                ModuleState.StartFailed => $"启动失败：{module.StartError}",
                ModuleState.Stopped => "已停止",
                _ => module.State.ToString(),
            };
            items.Add(new DiagnosticItem($"模块 {module.Key}（{module.DisplayName}）", level, detail));
        }
    }

    private static void CollectPlayer(List<DiagnosticItem> items, AppServices services)
    {
        var player = services.Player;
        if (player is null)
        {
            items.Add(new DiagnosticItem("播放器", DiagnosticLevel.Warn, "未装配播放器连接器"));
            return;
        }

        var capabilities = player.Capabilities.ToString();
        items.Add(new DiagnosticItem(
            "播放器",
            DiagnosticLevel.Ok,
            $"{player.DisplayName}（key={player.Key}，能力: {capabilities}）"));
    }

    /// <summary>token 脱敏：前 4 字符 + 星号。</summary>
    private static string Mask(string value) =>
        value.Length <= 4 ? "****" : value[..4] + "****";

    private static string LevelText(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Ok => "OK",
        DiagnosticLevel.Warn => "WARN",
        _ => "FAIL",
    };
}
