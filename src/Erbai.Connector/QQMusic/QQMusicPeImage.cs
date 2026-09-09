using System.Security.Cryptography;
using System.Text;

namespace Erbai.Connector.QQMusic;

/// <summary>补丁点机器码校验结果（读 PE 文件，RVA → 文件偏移）。</summary>
public sealed record PatchPointCheck(bool Passed, byte[] Actual, byte[] Expected);

/// <summary>
/// 只读 PE 解析（x86 校验所需最小面）：Machine 字段 + 段表做 RVA→文件偏移
/// 换算，读取补丁点前置机器码。代码表达沿用上游。
/// </summary>
public static class QQMusicPeImage
{
    public const ushort ImageFileMachineI386 = 0x014C;

    /// <summary>读取 RVA 处的机器码（最多 16 字节）；越界/非 x86 返回空。</summary>
    public static byte[] ReadMachineCode(string filePath, int rva, int length)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var offset = RvaToFileOffset(bytes, rva);
            if (offset < 0 || offset + length > bytes.Length)
            {
                return [];
            }

            var result = new byte[length];
            Array.Copy(bytes, offset, result, 0, length);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>PE 是否为 x86（Machine == IMAGE_FILE_MACHINE_I386）。</summary>
    public static bool IsX86(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                return false;
            }

            var peOffset = BitConverter.ToInt32(bytes, 0x3C);
            if (peOffset < 0 || peOffset + 24 > bytes.Length
                || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
            {
                return false;
            }

            return BitConverter.ToUInt16(bytes, peOffset + 4) == ImageFileMachineI386;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Sha256(string filePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));

    /// <summary>RVA → 文件偏移（按段表匹配；未命中返回 -1）。</summary>
    private static int RvaToFileOffset(byte[] pe, int rva)
    {
        if (pe.Length < 0x40 || pe[0] != (byte)'M' || pe[1] != (byte)'Z')
        {
            return -1;
        }

        var peOffset = BitConverter.ToInt32(pe, 0x3C);
        if (peOffset < 0 || peOffset + 24 > pe.Length
            || pe[peOffset] != (byte)'P' || pe[peOffset + 1] != (byte)'E')
        {
            return -1;
        }

        var numberOfSections = BitConverter.ToUInt16(pe, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(pe, peOffset + 20);
        var sectionTable = peOffset + 24 + optionalHeaderSize;
        for (var i = 0; i < numberOfSections; i++)
        {
            var section = sectionTable + i * 40;
            if (section + 40 > pe.Length)
            {
                return -1;
            }

            var virtualAddress = BitConverter.ToInt32(pe, section + 12);
            var virtualSize = BitConverter.ToInt32(pe, section + 8);
            var rawPointer = BitConverter.ToInt32(pe, section + 20);
            var rawSize = BitConverter.ToInt32(pe, section + 16);
            if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
            {
                return rawPointer + (rva - virtualAddress);
            }
        }

        return -1;
    }
}
