using BarrageGrab;
using Jint;
using Jint.Native;

namespace Erbai.Live.Douyin.Tests;

/// <summary>
/// Grabber JsEngine（Jint 注入的 encoder/bitConvert 辅助对象）单测：
/// 经 CreateNewEngine 端到端 JS 绑定验证（encoder/bitConvert 为 JsEngine 私有嵌套类，
/// 生产侧脚本同样只能经 JS 调用，与测试路径一致）。
/// Logger 静态构造依赖 Grabber 程序集的 nlog.config 嵌入资源，本测试不触发
/// console / jsonParse 异常路径，规避文件日志副作用。
/// </summary>
public class JsEngineTests
{
    private static Engine NewEngine() => JsEngine.CreateNewEngine();

    private static int[] EvalByteArray(string script)
    {
        var arr = NewEngine().Evaluate(script).AsArray();
        var result = new int[(int)arr.Get("length").AsNumber()];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (int)arr.Get((uint)i).AsNumber();
        }

        return result;
    }

    // ── JS 绑定端到端（CreateNewEngine → engine.Evaluate 调用 encoder/bitConvert）──

    [Theory]
    [InlineData("abc", new[] { 97, 98, 99 })]
    [InlineData("中文", new[] { 228, 184, 173, 230, 150, 135 })]
    public void Utf8ToBytes_经JS绑定_返回UTF8字节序列(string text, int[] expected)
    {
        Assert.Equal(expected, EvalByteArray($"Array.from(encoder.utf8ToBytes('{text}'))"));
    }

    [Fact]
    public void Utf32ToBytes_经JS绑定_UTF32LE四字节()
    {
        Assert.Equal(new[] { 65, 0, 0, 0 }, EvalByteArray("Array.from(encoder.utf32ToBytes('A'))"));
    }

    [Fact]
    public void AsciiToBytes_经JS绑定_ASCII字节()
    {
        Assert.Equal(new[] { 97, 98, 99 }, EvalByteArray("Array.from(encoder.asciiToBytes('abc'))"));
    }

    [Fact]
    public void 字符串往返_utf8_经JS绑定保真()
    {
        var engine = NewEngine();
        Assert.Equal("中文", engine.Evaluate("encoder.utf8ToString(encoder.utf8ToBytes('中文'))").AsString());
    }

    [Fact]
    public void 字符串往返_utf32_经JS绑定保真()
    {
        var engine = NewEngine();
        Assert.Equal("中文", engine.Evaluate("encoder.utf32ToString(encoder.utf32ToBytes('中文'))").AsString());
    }

    [Fact]
    public void 字符串往返_ascii_经JS绑定保真()
    {
        var engine = NewEngine();
        Assert.Equal("abc", engine.Evaluate("encoder.asciiToString(encoder.asciiToBytes('abc'))").AsString());
    }

    [Fact]
    public void 空字符串_utf8ToBytes_返回空数组()
    {
        Assert.Empty(EvalByteArray("Array.from(encoder.utf8ToBytes(''))"));
        Assert.Empty(EvalByteArray("Array.from(encoder.utf8ToBytes('   '))"));
    }

    [Fact]
    public void BitConvert_ToNumber_经JS绑定_IEEE754双精度()
    {
        var engine = NewEngine();
        Assert.Equal(0.0, engine.Evaluate("bitConvert.toNumber(new Uint8Array([0,0,0,0,0,0,0,0]))").AsNumber());
        // 1.0 的 IEEE754 LE 字节：3FF0000000000000 → 00 00 00 00 00 00 F0 3F
        Assert.Equal(1.0, engine.Evaluate("bitConvert.toNumber(new Uint8Array([0,0,0,0,0,0,240,63]))").AsNumber());
    }

    [Theory]
    [InlineData("bitConvert.toBoolean(new Uint8Array([1]))", true)]
    [InlineData("bitConvert.toBoolean(new Uint8Array([0]))", false)]
    public void BitConvert_ToBoolean_经JS绑定(string script, bool expected)
    {
        Assert.Equal(expected, NewEngine().Evaluate(script).AsBoolean());
    }

    [Fact]
    public void BitConvert_ToString_经JS绑定_大写十六进制连字符()
    {
        var engine = NewEngine();
        Assert.Equal("00-01-02", engine.Evaluate("bitConvert.toString(new Uint8Array([0,1,2]), 0)").AsString());
        Assert.Equal("01-02", engine.Evaluate("bitConvert.toString(new Uint8Array([0,1,2,3]), 1, 2)").AsString());
    }

    [Fact]
    public void BitConvert_GetBytes_经JS绑定_布尔一字节_数字八字节()
    {
        // BitConverter.GetBytes(bool) = 1 字节；Number 走 GetBytes(double) = 8 字节 LE
        Assert.Equal(new[] { 1 }, EvalByteArray("Array.from(bitConvert.getBytes(true))"));
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 240, 63 }, EvalByteArray("Array.from(bitConvert.getBytes(1))"));
    }

    [Fact]
    public void BitConvert_GetBytes_字符串_抛异常()
    {
        // Jint 把 .NET 方法内异常包装后透出（System.Exception，非 JavaScriptException）
        var ex = Assert.ThrowsAny<Exception>(() => NewEngine().Evaluate("bitConvert.getBytes('abc')"));
        Assert.Contains("不支持该类型转换", ex.Message);
    }

    [Fact]
    public void ByteToUint8Array_注入函数_可用()
    {
        Assert.Equal(new[] { 1, 2, 3 }, EvalByteArray("Array.from(byteToUint8Array([1,2,3]))"));
    }
}
