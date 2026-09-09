using System.Reflection;
using System.Runtime.Loader;

namespace Erbai.Core.Plugins;

/// <summary>
/// 插件程序集的 AssemblyLoadContext（决策 #4 的 ALC 动态加载）。
/// 解析规则（共享式契约，docs/07 §3/§4 的落地，Stage A M5 后扩展为宿主优先）：
/// - <b>宿主优先</b>：先尝试解析到宿主默认 ALC（Default 已加载的程序集一律共享——
///   内置插件依赖 Erbai.Core 等宿主程序集、第三方插件引用的宿主导出契约都走这里，
///   <b>不加载插件目录里的同名副本</b>，规避 ALC 双版本冲突）；
/// - <b>目录 fallback</b>：宿主没有的程序集按 AssemblyDependencyResolver 从插件目录解析
///   （第三方插件把自己的依赖 DLL 与主 DLL 放同一目录即可）。
/// - 可回收（isCollectible）：卸载后实例引用清空即可被 GC 回收。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginDirectory, string? name = null)
        : base(name ?? $"Erbai.Plugin:{Path.GetFileName(pluginDirectory)}", isCollectible: true)
    {
        // config.ini 必然存在（加载前置已解析），作为依赖解析的目录锚点
        _resolver = new AssemblyDependencyResolver(Path.Combine(pluginDirectory, "config.ini"));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            return null;
        }

        // 宿主优先：默认上下文已加载（或可从默认探测路径解析）→ 共享宿主版本
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            // 宿主没有该程序集 → 落到插件目录
        }
        catch (FileLoadException)
        {
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}