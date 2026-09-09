namespace Erbai.Connector.QQMusic;

/// <summary>
/// 极简 x86 机器码发射器（trampoline 构建用，机制参考 docs/04 §1.5.3）：
/// 字节序列 + 32 位立即数 + 标签化相对跳转（0F 8x / 0F 8x 两字节 opcode）。
/// 代码表达沿用上游。
/// </summary>
public sealed class X86Emitter
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<(int Position, int OpcodeSize, string Target)> _jumps = [];

    public void Bytes(params byte[] bytes) => _bytes.AddRange(bytes);

    public void Byte(byte value) => _bytes.Add(value);

    public void UInt32(uint value) => _bytes.AddRange(BitConverter.GetBytes(value));

    /// <summary>Mov dword ptr [edi+offset], imm32（C7 47 xx / C7 07 xx）。</summary>
    public void MovDwordAtEdi(int offset, int value)
    {
        if (offset == 0)
        {
            _bytes.Add(0xC7);
            _bytes.Add(0x07);
        }
        else
        {
            _bytes.Add(0xC7);
            _bytes.Add(0x47);
            _bytes.Add((byte)offset);
        }

        UInt32(unchecked((uint)value));
    }

    /// <summary>条件相对跳转（0F 8x rel32），目标以 Label 占位。</summary>
    public void Jump32(byte opcodePrefix, byte opcode, string target)
    {
        _bytes.Add(opcodePrefix);
        _bytes.Add(opcode);
        _jumps.Add((_bytes.Count, 2, target)); // position = rel32 占位起始（opcode 之后）
        UInt32(0); // 占位
    }

    public void Label(string name) => _labels[name] = _bytes.Count;

    public byte[] Build()
    {
        foreach (var (position, _, target) in _jumps)
        {
            if (!_labels.TryGetValue(target, out var labelPosition))
            {
                throw new InvalidOperationException($"缺失标签 {target}");
            }

            // position = rel32 占位起始（opcode 之后），指令从 position-2 起共 6 字节，
            // 下一条指令 = position + 4。旧实现 `position + opcodeSize + 4` 多算 2 字节，
            // 使 jns 落到 xor 兜底上、index 恒 0（2026-09-03 05:04 实机日志实证）。
            var nextInstruction = position + 4;
            var displacement = labelPosition - nextInstruction;
            var raw = BitConverter.GetBytes(displacement);
            for (var i = 0; i < 4; i++)
            {
                _bytes[position + i] = raw[i];
            }
        }

        return _bytes.ToArray();
    }
}
