// QqMusicProfileGen —— QQ 音乐版本画像半自动生成器（适配加速工具）。
//
// 背景：QQ 音乐每次升级，profile（src/Erbai.Connector/profiles/qqmusic/*.json）里的
// 函数/数据 RVA 都会漂移，此前全靠人工 IDA 定位。本工具把「最贵的一步——定位会漂移的
// 函数入口 RVA」自动化：从旧版 DLL 提取归一化的指令序列签名，在新版 DLL 里搜索定位。
//
// 安全模型不变（docs/04 §1.5.3）：产出仍只是「候选画像」，必须经实机哈希校验 + 点歌
// 端到端验证后，才人工确认写入发布画像。本工具绝不放宽「哈希/机器码校验不过安全拒绝」。
//
// 用法:
//   dotnet run --project tools/QqMusicProfileGen -- \
//     --old-dll <旧QQMusic.dll> --old-common <旧QQMusicCommon.dll> --old-profile <旧profile.json> \
//     --new-dll <新QQMusic.dll> --new-common <新QQMusicCommon.dll> \
//     [--out <新profile候选.json>]

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

class Program
{
    static int Main(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);

        if (args.ShowHelp || args.OldDll is null || args.OldCommon is null || args.OldProfile is null
            || args.NewDll is null || args.NewCommon is null)
        {
            Console.WriteLine("""
                QQ 音乐画像半自动生成器

                用法:
                  QqMusicProfileGen --old-dll <旧QQMusic.dll> --old-common <旧QQMusicCommon.dll>
                      --old-profile <旧profile.json> --new-dll <新QQMusic.dll> --new-common <新QQMusicCommon.dll>
                      [--out <新profile候选.json>]

                说明: 从旧版 DLL 提取归一化指令签名，在新版 DLL 定位漂移的函数入口 RVA；
                生成候选画像，仍需实机哈希校验 + 点歌端到端验证后人工确认。
                """);
            return 2;
        }

        var oldDll = File.ReadAllBytes(args.OldDll);
        var oldCommon = File.ReadAllBytes(args.OldCommon);
        var newDll = File.ReadAllBytes(args.NewDll);
        var newCommon = File.ReadAllBytes(args.NewCommon);
        var oldProfile = Profile.Load(args.OldProfile);

        var oldPe = PeImage.Parse(oldDll, "QQMusic.dll");
        var newPe = PeImage.Parse(newDll, "QQMusic.dll");

        var newVersion = FileVersionInfo.GetVersionInfo(args.NewDll).FileVersion ?? "";
        var newClientSha = Convert.ToHexString(SHA256.HashData(newDll));
        var newCommonSha = Convert.ToHexString(SHA256.HashData(newCommon));

        Console.WriteLine("=== 新版基本信息（机械计算，必准） ===");
        Console.WriteLine($"fileVersion  = {newVersion}");
        Console.WriteLine($"clientSha256 = {newClientSha}");
        Console.WriteLine($"commonSha256 = {newCommonSha}");
        Console.WriteLine();

        // 需要定位的「函数入口」字段。getCatManagerRva / getQqUinExRva 在 QQMusicCommon.dll，
        // 其余在 QQMusic.dll。
        var functionEntryFields = new (string Name, int OldRva, bool InCommon)[]
        {
            ("getCatManagerRva", oldProfile.GetCatManagerRva, true),
            ("getQqUinExRva", oldProfile.GetQqUinExRva, true),
            ("songItemConstructorRva", oldProfile.SongItemConstructorRva, false),
            ("songItemDestructorRva", oldProfile.SongItemDestructorRva, false),
            ("addSongsRva", oldProfile.AddSongsRva, false),
            ("getListRootRva", oldProfile.GetListRootRva, false),
            ("getListHelperRva", oldProfile.GetListHelperRva, false),
            ("getCategoryCountRva", oldProfile.GetCategoryCountRva, false),
        };

        var results = new Dictionary<string, string>();
        var confidence = new Dictionary<string, string>();

        foreach (var (name, oldRva, inCommon) in functionEntryFields)
        {
            var oldModule = inCommon ? oldCommon : oldDll;
            var newModule = inCommon ? newCommon : newDll;
            var oldModPe = PeImage.Parse(oldModule, name);
            var newModPe = PeImage.Parse(newModule, name);

            var signature = Signature.Extract(oldModule, oldModPe, oldRva, 32);
            if (signature is null)
            {
                results[name] = "manual";
                confidence[name] = "manual";
                Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} 无法反汇编 → 需人工确认");
                continue;
            }

            var hits = Signature.Search(newModule, newModPe, signature);
            if (hits.Count == 1)
            {
                results[name] = Rva(hits[0]);
                confidence[name] = "auto";
                Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 新 {Rva(hits[0])}  (auto, 唯一)");
            }
            else if (hits.Count == 0)
            {
                results[name] = "manual";
                confidence[name] = "manual";
                Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 未找到匹配（签名漂移过大）→ 需人工确认");
            }
            else
            {
                results[name] = string.Join("|", hits.Take(4).Select(Rva));
                confidence[name] = "ambiguous";
                Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → {hits.Count} 个候选: {string.Join(", ", hits.Take(4).Select(Rva))}  (ambiguous)");
            }
        }

        Console.WriteLine();
        LocateDispatch(oldDll, newDll, oldPe, newPe, oldProfile, results, confidence);

        Console.WriteLine();
        LocateHiddenCategory(oldDll, newDll, oldPe, newPe, oldProfile, results, confidence);

        Console.WriteLine();
        Console.WriteLine("=== 定位汇总 ===");
        foreach (var (name, value) in results)
        {
            Console.WriteLine($"  {name,-28} {value,-18} ({confidence[name]})");
        }

        WriteCandidate(args, newVersion, newClientSha, newCommonSha, results, confidence, oldProfile);
        Console.WriteLine();
        return 0;
    }

    static void LocateDispatch(
        byte[] oldDll, byte[] newDll, PeImage oldPe, PeImage newPe,
        Profile oldProfile, Dictionary<string, string> results, Dictionary<string, string> confidence)
    {
        const string name = "singleSongPlayDispatchRva";
        var oldRva = oldProfile.SingleSongPlayDispatchRva;

        var fnStart = Signature.FindFunctionStart(oldDll, oldPe, oldRva);
        if (fnStart < 0)
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} 向上未找到函数序言 → 需人工确认");
            return;
        }

        var ctx = Signature.ExtractWindow(oldDll, oldPe, fnStart, oldRva, before: 12, after: 4);
        if (ctx is null)
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} 上下文提取失败 → 需人工确认");
            return;
        }

        var fnSig = Signature.Extract(oldDll, oldPe, fnStart, 32);
        var newFnStarts = fnSig is null ? new List<int>() : Signature.Search(newDll, newPe, fnSig);

        var found = -1;
        foreach (var newFn in newFnStarts)
        {
            var hit = Signature.SearchContext(newDll, newPe, newFn, ctx, anchorIndex: 12);
            if (hit >= 0)
            {
                found = hit;
                break;
            }
        }

        if (found >= 0)
        {
            results[name] = Rva(found);
            confidence[name] = "auto";
            var off = newPe.RvaToOffset(found);
            var lead = off >= 0 && off < newDll.Length ? newDll[off] : (byte)0;
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 新 {Rva(found)}  (auto；新位置首字节 {lead:X2}，expectedPlayDispatchBytes 应为 E8 开头)");
        }
        else
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 上下文未唯一匹配 → 需人工确认");
        }
    }

    static void LocateHiddenCategory(
        byte[] oldDll, byte[] newDll, PeImage oldPe, PeImage newPe,
        Profile oldProfile, Dictionary<string, string> results, Dictionary<string, string> confidence)
    {
        const string name = "hiddenCategoryIdRva";
        var oldRva = oldProfile.HiddenCategoryIdRva;
        var oldVa = (uint)(oldPe.ImageBase + oldRva);

        var (refOff, moffsOffset) = Signature.FindAbsMemoryReference(oldDll, oldPe, oldVa);
        if (refOff < 0)
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} 未找到 mov r32,[moffs32] 引用点 → 需人工确认（数据地址定位较脆弱）");
            return;
        }

        var refRva = oldPe.OffsetToRva(refOff);
        var ctx = Signature.ExtractAt(oldDll, oldPe, refRva, before: 4, after: 4);
        if (ctx is null)
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} 引用点上下文提取失败 → 需人工确认");
            return;
        }

        var fnStart = Signature.FindFunctionStart(oldDll, oldPe, refRva);
        var newFnStarts = fnStart < 0
            ? new List<int>()
            : Signature.Search(newDll, newPe, Signature.Extract(oldDll, oldPe, fnStart, 32) ?? Array.Empty<string>());

        var newAddr = -1L;
        foreach (var newFn in newFnStarts)
        {
            var hit = Signature.SearchContext(newDll, newPe, newFn, ctx, anchorIndex: 4);
            if (hit >= 0)
            {
                var off = newPe.RvaToOffset(hit);
                if (off >= 0 && off + moffsOffset + 4 <= newDll.Length)
                {
                    newAddr = BitConverter.ToUInt32(newDll, off + moffsOffset);
                    break;
                }
            }
        }

        if (newAddr >= newPe.ImageBase && newAddr < newPe.ImageBase + newPe.SizeOfImage)
        {
            var newRva = (int)(newAddr - newPe.ImageBase);
            results[name] = Rva(newRva);
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 候选 {Rva(newRva)}  (manual：数据地址易与相邻全局变量混淆，需人工用 IDA 在 SingleSongPlayDispatch 内核对 mov r32,[abs] 后接 cmp r,-1 的那条)");
        }
        else
        {
            results[name] = "manual";
            confidence[name] = "manual";
            Console.WriteLine($"[{name}] 旧 {Rva(oldRva)} → 引用点未唯一匹配 → 需人工确认");
        }
    }

    static void WriteCandidate(
        Args args, string version, string clientSha, string commonSha,
        Dictionary<string, string> results, Dictionary<string, string> confidence, Profile oldProfile)
    {
        if (args.Out is null)
        {
            Console.WriteLine("（未指定 --out，跳过候选 json 输出）");
            return;
        }

        string Resolve(string field, string fallbackOld) =>
            confidence.GetValueOrDefault(field) == "auto" ? results[field] : fallbackOld;

        var obj = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["fileVersion"] = version,
            ["clientSha256"] = clientSha,
            ["commonSha256"] = commonSha,
            ["singleSongPlayDispatchRva"] = Resolve("singleSongPlayDispatchRva", args.ProfileText("singleSongPlayDispatchRva")),
            ["expectedPlayDispatchBytes"] = args.ProfileText("expectedPlayDispatchBytes"), // 机器码前置校验，人工确认
            ["getCatManagerRva"] = Resolve("getCatManagerRva", args.ProfileText("getCatManagerRva")),
            ["getQqUinExRva"] = Resolve("getQqUinExRva", args.ProfileText("getQqUinExRva")),
            ["songItemConstructorRva"] = Resolve("songItemConstructorRva", args.ProfileText("songItemConstructorRva")),
            ["songItemDestructorRva"] = Resolve("songItemDestructorRva", args.ProfileText("songItemDestructorRva")),
            ["addSongsRva"] = Resolve("addSongsRva", args.ProfileText("addSongsRva")),
            ["hiddenCategoryIdRva"] = Resolve("hiddenCategoryIdRva", args.ProfileText("hiddenCategoryIdRva")),
            ["getListRootRva"] = Resolve("getListRootRva", args.ProfileText("getListRootRva")),
            ["getListHelperRva"] = Resolve("getListHelperRva", args.ProfileText("getListHelperRva")),
            ["getCategoryCountRva"] = Resolve("getCategoryCountRva", args.ProfileText("getCategoryCountRva")),
            ["songItemSize"] = args.ProfileText("songItemSize"),
            ["evidence"] = $"auto-generated candidate from {args.ProfileText("fileVersion")}; 需实机哈希校验 + 点歌端到端验证后人工确认",
        };

        File.WriteAllText(args.Out, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"候选画像已写入: {args.Out}（manual/ambiguous 字段沿用旧值，务必人工核对）");
    }

    static string Rva(int value) => $"0x{value:X8}";
}

/// <summary>PE 只读解析（image base / 段表 / RVA↔文件偏移）。</summary>
sealed class PeImage
{
    public required uint ImageBase { get; init; }
    public required int SizeOfImage { get; init; }
    public required IReadOnlyList<(string Name, int VAddr, int VSize, int RawPtr, int RawSize)> Sections { get; init; }

    public static PeImage Parse(byte[] code, string label)
    {
        if (code.Length < 0x40 || code[0] != 'M' || code[1] != 'Z')
            throw new InvalidOperationException($"{label} 不是有效 PE（缺 MZ 头）");

        var peOffset = BitConverter.ToInt32(code, 0x3C);
        if (peOffset < 0 || peOffset + 24 > code.Length || code[peOffset] != 'P' || code[peOffset + 1] != 'E')
            throw new InvalidOperationException($"{label} 不是有效 PE（缺 PE 签名）");

        var sectionCount = BitConverter.ToUInt16(code, peOffset + 6);
        var optSize = BitConverter.ToUInt16(code, peOffset + 20);
        var opt = peOffset + 24;
        var imageBase = BitConverter.ToUInt32(code, opt + 28);
        var sizeOfImage = BitConverter.ToInt32(code, opt + 56);
        var sections = new List<(string, int, int, int, int)>();
        var sectionTable = opt + optSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var s = sectionTable + i * 40;
            if (s + 40 > code.Length) break;
            var name = System.Text.Encoding.ASCII.GetString(code, s, 8).TrimEnd('\0');
            var vSize = BitConverter.ToInt32(code, s + 8);
            var vAddr = BitConverter.ToInt32(code, s + 12);
            var rawSize = BitConverter.ToInt32(code, s + 16);
            var rawPtr = BitConverter.ToInt32(code, s + 20);
            sections.Add((name, vAddr, vSize, rawPtr, rawSize));
        }

        return new PeImage { ImageBase = imageBase, SizeOfImage = sizeOfImage, Sections = sections };
    }

    public int RvaToOffset(int rva)
    {
        foreach (var (_, vAddr, vSize, rawPtr, rawSize) in Sections)
        {
            if (rva >= vAddr && rva < vAddr + Math.Max(vSize, rawSize))
            {
                return rawPtr + (rva - vAddr);
            }
        }

        return -1;
    }

    public int OffsetToRva(int offset)
    {
        foreach (var (_, vAddr, vSize, rawPtr, rawSize) in Sections)
        {
            if (offset >= rawPtr && offset < rawPtr + rawSize)
            {
                return vAddr + (offset - rawPtr);
            }
        }

        return -1;
    }
}

/// <summary>归一化指令序列签名：对绝对地址/相对分支不敏感，对寄存器/栈操作敏感。</summary>
static class Signature
{
    /// <summary>从 RVA 起反汇编 count 条指令，返回归一化 mnemonic 序列。</summary>
    public static string[]? Extract(byte[] code, PeImage pe, int rva, int count)
    {
        var off = pe.RvaToOffset(rva);
        return off < 0 ? null : DecodeSequence(code, off, count);
    }

    /// <summary>从函数入口反汇编，取 anchor 前后各 before/after 条指令的归一化序列。</summary>
    public static string[]? ExtractWindow(byte[] code, PeImage pe, int fnStart, int anchorRva, int before, int after)
    {
        var off = pe.RvaToOffset(fnStart);
        if (off < 0) return null;

        var instructions = new List<(int Rva, string Token)>();
        var reader = new ByteArrayCodeReader(code) { Position = off };
        var decoder = Decoder.Create(32, reader, DecoderOptions.None);
        decoder.IP = (ulong)off;
        while (true)
        {
            var ip = (int)decoder.IP;
            decoder.Decode(out var insn);
            if (insn.Code == Code.INVALID) return null;
            instructions.Add((pe.OffsetToRva((int)ip), Normalize(insn)));
            if (ip >= pe.RvaToOffset(anchorRva)) break;
            if (instructions.Count > 5000) return null;
        }

        var idx = instructions.FindIndex(t => t.Rva == anchorRva);
        if (idx < 0) return null;
        var start = Math.Max(0, idx - before);
        var end = Math.Min(instructions.Count, idx + 1 + after);
        return instructions.Skip(start).Take(end - start).Select(t => t.Token).ToArray();
    }

    /// <summary>从 RVA 起取前后各 before/after 条指令的归一化序列（内部先定位所在函数）。</summary>
    public static string[]? ExtractAt(byte[] code, PeImage pe, int rva, int before, int after)
    {
        var fnStart = FindFunctionStart(code, pe, rva);
        return fnStart < 0 ? null : ExtractWindow(code, pe, fnStart, rva, before, after);
    }

    /// <summary>在新版 .text 里搜索签名，返回所有命中的函数入口 RVA。</summary>
    public static List<int> Search(byte[] code, PeImage pe, string[] signature)
    {
        var hits = new List<int>();
        foreach (var (name, vAddr, vSize, rawPtr, rawSize) in pe.Sections)
        {
            if (!name.Equals(".text", StringComparison.OrdinalIgnoreCase)) continue;
            var end = Math.Min(rawPtr + Math.Min(vSize, rawSize), code.Length);
            for (var off = rawPtr; off + 3 < end; off++)
            {
                if (code[off] != 0x55 || code[off + 1] != 0x8B || code[off + 2] != 0xEC) continue;
                var seq = DecodeSequence(code, off, signature.Length);
                if (seq is not null && seq.SequenceEqual(signature))
                {
                    hits.Add(vAddr + (off - rawPtr));
                }
            }
        }

        return hits;
    }

    /// <summary>在新版某函数内搜索上下文序列，返回命中的 anchor RVA（context 中 anchorIndex 处指令的地址）。</summary>
    public static int SearchContext(byte[] code, PeImage pe, int fnStart, string[] context, int anchorIndex)
    {
        var off = pe.RvaToOffset(fnStart);
        if (off < 0) return -1;

        var reader = new ByteArrayCodeReader(code) { Position = off };
        var decoder = Decoder.Create(32, reader, DecoderOptions.None);
        decoder.IP = (ulong)off;
        var window = new List<(int Rva, string Token)>();
        for (var i = 0; i < 5000; i++)
        {
            var ip = (int)decoder.IP;
            decoder.Decode(out var insn);
            if (insn.Code == Code.INVALID) break;
            window.Add((pe.OffsetToRva((int)ip), Normalize(insn)));
        }

        for (var i = 0; i + context.Length <= window.Count; i++)
        {
            var match = true;
            for (var j = 0; j < context.Length; j++)
            {
                if (window[i + j].Token != context[j]) { match = false; break; }
            }

            if (match) return window[i + anchorIndex].Rva;
        }

        return -1;
    }

    /// <summary>向上找函数入口（push ebp; mov ebp,esp，优先 0x10 对齐）。</summary>
    public static int FindFunctionStart(byte[] code, PeImage pe, int rva)
    {
        var addr = rva & ~0xF;
        for (; addr >= 0x1000; addr -= 0x10)
        {
            var off = pe.RvaToOffset(addr);
            if (off >= 0 && off + 2 < code.Length && code[off] == 0x55 && code[off + 1] == 0x8B && code[off + 2] == 0xEC)
                return addr;
        }

        addr = rva - 1;
        for (; addr >= 0x1000; addr--)
        {
            var off = pe.RvaToOffset(addr);
            if (off >= 0 && off + 2 < code.Length && code[off] == 0x55 && code[off + 1] == 0x8B && code[off + 2] == 0xEC)
                return addr;
        }

        return -1;
    }

    /// <summary>在代码段里找引用 targetVa 的 `mov r32,[moffs32]` 指令（A1 或 8B /r modrm=05 形式）。</summary>
    /// <returns>(引用点文件偏移, moffs32 相对引用点的字节偏移)。未找到返回 (-1,0)。</returns>
    public static (int Offset, int MoffsOffset) FindAbsMemoryReference(byte[] code, PeImage pe, uint targetVa)
    {
        var bytes = BitConverter.GetBytes(targetVa);
        foreach (var (_, vAddr, vSize, rawPtr, _) in pe.Sections)
        {
            if (vAddr < 0x1000 || vSize <= 0) continue;
            var end = Math.Min(rawPtr + vSize, code.Length);
            for (var i = rawPtr; i + 4 < end; i++)
            {
                // mov eax,[moffs32] —— opcode A1
                if (code[i] == 0xA1 && Match4(code, i + 1, bytes)) return (i, 1);
                // mov r32,[moffs32] —— 8B /r，modrm = 00 rrr 101（disp32 only）
                if (code[i] == 0x8B && i + 5 < end && (code[i + 1] & 0xC7) == 0x05 && Match4(code, i + 2, bytes))
                {
                    return (i, 2);
                }
            }
        }

        return (-1, 0);
    }

    private static bool Match4(byte[] code, int offset, byte[] bytes) =>
        offset + 4 <= code.Length
        && code[offset] == bytes[0] && code[offset + 1] == bytes[1]
        && code[offset + 2] == bytes[2] && code[offset + 3] == bytes[3];

    private static string[]? DecodeSequence(byte[] code, int startOffset, int count)
    {
        var tokens = new List<string>(count);
        var reader = new ByteArrayCodeReader(code) { Position = startOffset };
        var decoder = Decoder.Create(32, reader, DecoderOptions.None);
        decoder.IP = (ulong)startOffset;
        for (var i = 0; i < count; i++)
        {
            decoder.Decode(out var insn);
            if (insn.Code == Code.INVALID) return null;
            tokens.Add(Normalize(insn));
        }

        return tokens.ToArray();
    }

    private static string Normalize(in Instruction insn)
    {
        var sb = new StringBuilder();
        sb.Append(insn.Mnemonic.ToString().ToLowerInvariant());
        for (var i = 0; i < insn.OpCount; i++)
        {
            sb.Append(i == 0 ? ' ' : ',');
            sb.Append(NormOp(insn, i));
        }

        return sb.ToString();
    }

    private static string NormOp(in Instruction insn, int i)
    {
        var kind = insn.GetOpKind(i);
        return kind switch
        {
            OpKind.Register => insn.GetOpRegister(i).ToString().ToLowerInvariant(),
            OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 => "rel",
            OpKind.Memory => NormMem(insn),
            OpKind.Immediate8 => $"0x{insn.Immediate8:x}",
            OpKind.Immediate16 => $"0x{insn.Immediate16:x}",
            OpKind.Immediate32 => IsAddress(insn.Immediate32) ? "abs" : $"0x{insn.Immediate32:x}",            _ => kind.ToString().ToLowerInvariant(),
        };
    }

    private static string NormMem(in Instruction insn)
    {
        // 绝对内存地址（含 IP-relative / 无基址）跨版本漂移 → abs；
        // 寄存器相对 [base+disp] 保留 base 与 disp（栈偏移/对象字段跨版本相对稳定）。
        if (insn.IsIPRelativeMemoryOperand || insn.MemoryBase == Register.None)
        {
            return "abs";
        }

        var baseName = insn.MemoryBase.ToString().ToLowerInvariant();
        var disp = unchecked((long)insn.MemoryDisplacement64);
        var dispText = disp == 0 ? "" : (disp > 0 ? $"+0x{disp:x}" : $"-0x{-disp:x}");
        return $"[{baseName}{dispText}]";
    }

    private static bool IsAddress(uint value) => value >= 0x100000 && value <= 0x7FFFFFFF;
}

/// <summary>profile json 模型（RVA 以 "0x..." 字符串存储，用 HexIntConverter 解析）。</summary>
sealed class Profile
{
    [JsonConverter(typeof(HexIntConverter))]
    public int SingleSongPlayDispatchRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int GetCatManagerRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int GetQqUinExRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int SongItemConstructorRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int SongItemDestructorRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int AddSongsRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int HiddenCategoryIdRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int GetListRootRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int GetListHelperRva { get; set; }

    [JsonConverter(typeof(HexIntConverter))]
    public int GetCategoryCountRva { get; set; }

    public static Profile Load(string path) =>
        JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("旧 profile 解析失败");
}

sealed class HexIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
        var s = reader.GetString()!.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value:X8}");
}

sealed class Args
{
    public string? OldDll { get; private set; }
    public string? OldCommon { get; private set; }
    public string? OldProfile { get; private set; }
    public string? NewDll { get; private set; }
    public string? NewCommon { get; private set; }
    public string? Out { get; private set; }
    public bool ShowHelp { get; private set; }

    private readonly Dictionary<string, string> _profileText = new(StringComparer.OrdinalIgnoreCase);

    public string ProfileText(string key) => _profileText.GetValueOrDefault(key, "");

    public static Args Parse(IEnumerable<string> raw)
    {
        var a = new Args();
        var list = raw.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            var next = i + 1 < list.Count ? list[i + 1] : null;
            switch (arg)
            {
                case "--old-dll": a.OldDll = next; i++; break;
                case "--old-common": a.OldCommon = next; i++; break;
                case "--old-profile": a.OldProfile = next; i++; break;
                case "--new-dll": a.NewDll = next; i++; break;
                case "--new-common": a.NewCommon = next; i++; break;
                case "--out": a.Out = next; i++; break;
                case "--help" or "-h": a.ShowHelp = true; break;
            }
        }

        if (a.OldProfile is not null && File.Exists(a.OldProfile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(a.OldProfile));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    a._profileText[prop.Name] = prop.Value.GetString()!;
                else if (prop.Value.ValueKind == JsonValueKind.Number)
                    a._profileText[prop.Name] = prop.Value.GetRawText();
            }
        }

        return a;
    }
}
