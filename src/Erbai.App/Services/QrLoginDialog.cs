using Erbai.Live.Bilibili.Login;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Erbai.App.Services;

/// <summary>
/// B站扫码登录窗口（docs/01 §3.1：WinUI 内容窗口 + QRCoder 生成二维码，无浏览器）。
/// ContentDialog 内渲染二维码图片 + 状态文本；用户关闭对话框即取消登录轮询。
/// </summary>
public static class QrLoginDialog
{
    public static async Task<BilibiliLoginResult?> ShowAsync(XamlRoot xamlRoot, BilibiliLoginService login)
    {
        var image = new Image { Width = 240, Height = 240 };
        var status = new TextBlock
        {
            Text = "正在获取二维码…",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
        };
        var dialog = new ContentDialog
        {
            Title = "B站扫码登录",
            Content = new StackPanel { Spacing = 12, Children = { image, status } },
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        using var cts = new CancellationTokenSource();
        var loginTask = login.QrLoginAsync(state =>
            EnqueueRender(dialog.DispatcherQueue, state, image, status, dialog), cts.Token);
        dialog.Closed += (_, _) => cts.Cancel();

        await dialog.ShowAsync();

        try
        {
            return await loginTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>把状态渲染调度到 UI 线程（async void 生命周期内自捕获异常，不逃逸）。</summary>
    private static void EnqueueRender(DispatcherQueue queue, QrLoginState state, Image image, TextBlock status, ContentDialog dialog)
    {
        queue.TryEnqueue(async () =>
        {
            try
            {
                await RenderAsync(state, image, status, dialog);
            }
            catch (Exception ex)
            {
                status.Text = $"渲染出错：{ex.Message}";
            }
        });
    }

    private static async Task RenderAsync(QrLoginState state, Image image, TextBlock status, ContentDialog dialog)
    {
        switch (state.Status)
        {
            case QrLoginState.StatusWaiting:
                if (state.QrUrl is not null)
                {
                    image.Source = await GenerateQrImageAsync(state.QrUrl);
                }

                status.Text = state.Message;
                break;
            case QrLoginState.StatusScanned:
                status.Text = state.Message;
                break;
            case QrLoginState.StatusSuccess:
            case QrLoginState.StatusExpired:
            case QrLoginState.StatusTimeout:
            case QrLoginState.StatusError:
            case QrLoginState.StatusCancelled:
                status.Text = state.Message;
                dialog.Hide();
                break;
        }
    }

    private static async Task<BitmapImage> GenerateQrImageAsync(string content)
    {
        // PNG 字节生成是 CPU 活（放后台线程，不阻塞 UI）；
        // SetSourceAsync 必须 await——在 UI 线程同步 GetResult() 会死锁
        // （SetSourceAsync 的完成依赖 UI 消息泵，而 GetResult 阻塞了它）
        var png = await Task.Run(() =>
        {
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            return qrCode.GetGraphic(6);
        });

        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }
}
