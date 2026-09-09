using Erbai.App.Diagnostics;
using Erbai.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Erbai.App.Views;

/// <summary>诊断页（阶段 6 M7）：7 组检查 + 脱敏导出。</summary>
public sealed partial class DiagnosticsPage : Page
{
    private readonly AppServices _services;

    public DiagnosticsPage()
    {
        InitializeComponent();
        _services = App.Services;
        Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Refresh();
        ExportStatus.Text = "";
    }

    private void Refresh()
    {
        DiagnosticsList.ItemsSource = DiagnosticsService.Collect(_services);
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var report = DiagnosticsService.BuildReport(_services);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ErbaiLiveTool");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            await File.WriteAllTextAsync(path, report);
            ExportStatus.Text = $"已导出（脱敏）：{path}";
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"导出失败：{ex.Message}";
        }
    }
}
