namespace Erbai.App;

/// <summary>自定义入口（DisableXamlGeneratedMain），FufuLauncher 手法。</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p => _ = new App());
    }
}
