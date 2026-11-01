// Core/Native/TccModLoader.cs —— Chronos .tccmod 镜像的内存加载器（托管实现）
//
// 为什么用托管实现而不是等 Chronos 的 C loader DLL：
//   Project-Chronos-Collapse 的 Phase 3（序列化器）/ Phase 4（loader）还没完成，
//   ChronosArtifacts 里目前只有 Phase 1 的 SandboxProbe，没有能直接用的 ARM32 DLL。
//   而 Phase 1 已经在真机上验证过：AppContainer 里 VirtualAllocFromApp +
//   VirtualProtectFromApp(PAGE_EXECUTE_READ) 这条路是通的（需要 codeGeneration 能力）。
//   所以这里用 C# 直接实现同一套加载流程，不依赖对方构建产物。
//
// 加载流程（与 docs/CHRONOS_COLLAPSE_GUIDE.md §2 的格式一致）：
//   1) 校验头（magic "TCCM" / 版本 / 段表偏移）
//   2) 按各段 mem_size 一次性 VirtualAllocFromApp(PAGE_READWRITE)
//   3) 拷入文件数据（BSS 段本来就不在文件里，保持零）
//   4) 建符号地址表：base + 段基址 + value
//   5) 解析导入符号（section_index == 0 的外部符号）——由调用方提供的宿主表提供
//   6) 应用重定位（ARM ABS32 / Thumb BL / Thumb B / MOVW / MOVT）
//   7) 按段切换保护：数据段 RW、代码段 RX（W^X）
//   8) 入口点：entry_rva → 地址；THUMB2 模块最低位置 1（Thumb 状态）
//
// 安全边界：模块只能拿到宿主显式导出的符号（tccmod_add_symbol 的那些），
// 不提供任何"任意 Windows API"入口 —— 与 HelloChronos 样例里 chronos_add 的最小
// 宿主 ABI 思路一致。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Yanshuai.Messages.Core.Native
{
    /// <summary>ARM Thumb-2 重定位类型（与规范 §2.4 一致）。</summary>
    internal static class ArmReloc
    {
        public const ushort None = 0;
        public const ushort Abs32 = 2;
        public const ushort ThumbCall = 10;      // BL / BLX
        public const ushort ThumbJump24 = 30;    // B.W / Bcc.W
        public const ushort ThumbMovwAbsNc = 47; // MOVW
        public const ushort ThumbMovtAbs = 48;   // MOVT
    }

    /// <summary>段类型。</summary>
    internal static class TccSectionType
    {
        public const uint Text = 1;
        public const uint Data = 2;
        public const uint RoData = 3;
        public const uint Bss = 4;
        public const uint Custom = 5;
    }

    /// <summary>段标志。</summary>
    internal static class TccSectionFlags
    {
        public const uint Write = 0x1;
        public const uint Alloc = 0x2;
        public const uint ExecInstr = 0x4;
    }

    /// <summary>模块级标志。</summary>
    internal static class TccModuleFlags
    {
        public const uint Thumb2 = 0x1;
        public const uint HardFp = 0x2;
        public const uint Pic = 0x4;
        public const uint Debug = 0x8;
    }

    public sealed class TccModule : IDisposable
    {
        private IntPtr _base;
        private bool _disposed;
        private readonly List<IntPtr> _sectionBases = new List<IntPtr>();
        private readonly Dictionary<string, IntPtr> _exports = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        internal TccModule(IntPtr baseAddress)
        {
            _base = baseAddress;
        }

        /// <summary>镜像总大小（诊断用）。</summary>
        public long Size { get; internal set; }

        /// <summary>入口点地址（已按 Thumb 规则置位）。</summary>
        public IntPtr EntryPoint { get; internal set; }

        /// <summary>模块是否要求 Thumb-2 代码。</summary>
        public bool IsThumb2 { get; internal set; }

        public IReadOnlyDictionary<string, IntPtr> Exports { get { return _exports; } }

        internal void AddExport(string name, IntPtr address)
        {
            if (!string.IsNullOrEmpty(name)) _exports[name] = address;
        }

        internal void TrackSection(IntPtr address)
        {
            _sectionBases.Add(address);
        }

        /// <summary>按加载顺序取第 index 段的基址（与镜像里的段表顺序一致）。</summary>
        internal IntPtr SectionBaseAt(int index)
        {
            if (index < 0 || index >= _sectionBases.Count) return IntPtr.Zero;
            return _sectionBases[index];
        }

        /// <summary>取导出符号地址（函数或数据）。</summary>
        public IntPtr GetSymbol(string name)
        {
            IntPtr address;
            return _exports.TryGetValue(name ?? "", out address) ? address : IntPtr.Zero;
        }

        /// <summary>把导出函数绑成委托。ARM32 Thumb 函数的指针最低位是 1，CLR 能正确识别。</summary>
        public T Bind<T>(string name) where T : class
        {
            var address = GetSymbol(name);
            if (address == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_base != IntPtr.Zero)
            {
                NativeMethods.VirtualFree(_base, IntPtr.Zero, NativeMethods.MemRelease);
                _base = IntPtr.Zero;
            }
            _exports.Clear();
            _sectionBases.Clear();
        }
    }

    /// <summary>
    /// 从字节数组加载一个 .tccmod 镜像。
    /// hostSymbol 用于解析模块导入的宿主符号（返回 IntPtr.Zero 表示没有，弱符号放行，强符号报错）。
    /// </summary>
    public static class TccModLoader
    {
        private const uint Magic = 0x4D434354; // "TCCM" little-endian

        public static TccModule Load(byte[] image, Func<string, IntPtr> hostSymbol)
        {
            if (image == null || image.Length < 80) throw new ArgumentException("镜像太小，不是合法的 .tccmod。");
            if (hostSymbol == null) throw new ArgumentNullException("hostSymbol");

            var header = ReadHeader(image);
            if (header.Magic != Magic) throw new ArgumentException("镜像 magic 不对，不是 .tccmod。");
            if (header.VersionMajor != 1) throw new ArgumentException("不支持的 .tccmod 版本：" + header.VersionMajor + "." + header.VersionMinor);

            // ── 2) 分配一整块内存，按段切 ──
            long total = 0;
            for (int i = 0; i < header.Sections.Count; i++) total += AlignUp(header.Sections[i].MemSize, 64);
            if (total <= 0) throw new ArgumentException("镜像没有任何需要加载的段。");

            IntPtr baseAddress = NativeMethods.VirtualAllocFromApp(
                IntPtr.Zero, new IntPtr(total), NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
            if (baseAddress == IntPtr.Zero)
            {
                throw new InvalidOperationException("VirtualAllocFromApp 失败（" + Marshal.GetLastWin32Error()
                    + "）。AppContainer 里分配可执行内存需要 codeGeneration 能力。");
            }

            var module = new TccModule(baseAddress) { Size = total, IsThumb2 = (header.Flags & TccModuleFlags.Thumb2) != 0 };

            // ── 3) 拷数据 ──
            long cursor = 0;
            for (int i = 0; i < header.Sections.Count; i++)
            {
                var section = header.Sections[i];
                IntPtr sectionBase = new IntPtr(baseAddress.ToInt64() + cursor);
                module.TrackSection(sectionBase);
                if (section.FileSize > 0)
                {
                    if (section.FileOffset + section.FileSize > image.Length)
                    {
                        module.Dispose();
                        throw new ArgumentException("段数据越界：" + section.Name);
                    }
                    Marshal.Copy(image, (int)section.FileOffset, sectionBase, (int)section.FileSize);
                }
                cursor += AlignUp(section.MemSize, 64);
            }

            // ── 4) 符号地址表 ──
            var symbolAddress = new IntPtr[header.Symbols.Count];
            for (int i = 0; i < header.Symbols.Count; i++)
            {
                var symbol = header.Symbols[i];
                if (symbol.SectionIndex == 0 || symbol.SectionIndex > header.Sections.Count)
                {
                    symbolAddress[i] = IntPtr.Zero;   // 外部符号，等第 5 步解析
                    continue;
                }
                symbolAddress[i] = new IntPtr(
                    SectionBaseOf(module, (int)symbol.SectionIndex - 1).ToInt64() + (long)symbol.Value);
                if (symbol.Binding == 1 /* GLOBAL */)
                {
                    module.AddExport(symbol.Name, symbolAddress[i]);
                }
            }

            // ── 5) 解析导入 ──
            for (int i = 0; i < header.Symbols.Count; i++)
            {
                var symbol = header.Symbols[i];
                if (symbol.SectionIndex != 0) continue;
                var address = hostSymbol(symbol.Name);
                if (address == IntPtr.Zero && symbol.Binding != 2 /* WEAK */)
                {
                    var missing = symbol.Name;
                    module.Dispose();
                    throw new InvalidOperationException("模块需要的宿主符号没有提供：" + missing);
                }
                symbolAddress[i] = address;
            }

            // ── 6) 重定位 ──
            foreach (var reloc in header.Relocs)
            {
                if (reloc.SymbolIndex >= symbolAddress.Length) continue;
                var target = symbolAddress[reloc.SymbolIndex];
                if (reloc.SectionIndex == 0 || reloc.SectionIndex > header.Sections.Count) continue;
                var section = header.Sections[(int)reloc.SectionIndex - 1];
                IntPtr place = new IntPtr(SectionBaseOf(module, (int)reloc.SectionIndex - 1).ToInt64() + reloc.Offset);
                if (place.ToInt64() < baseAddress.ToInt64() || place.ToInt64() >= baseAddress.ToInt64() + total)
                {
                    module.Dispose();
                    throw new InvalidOperationException("重定位越界（段 " + section.Name + " +0x" + reloc.Offset.ToString("X") + "）。");
                }

                switch (reloc.Type)
                {
                    case ArmReloc.Abs32:
                        WriteInt32(place, (int)(target.ToInt64() + reloc.Addend));
                        break;
                    case ArmReloc.ThumbCall:
                    case ArmReloc.ThumbJump24:
                        ApplyThumbBranch24(place, target, reloc.Addend);
                        break;
                    case ArmReloc.ThumbMovwAbsNc:
                        ApplyThumbMovImm(place, (int)(target.ToInt64() + reloc.Addend), false);
                        break;
                    case ArmReloc.ThumbMovtAbs:
                        ApplyThumbMovImm(place, (int)(target.ToInt64() + reloc.Addend), true);
                        break;
                    case ArmReloc.None:
                    default:
                        break;
                }
            }

            // ── 7) 切保护：数据段 RW、代码段 RX ──
            for (int i = 0; i < header.Sections.Count; i++)
            {
                var section = header.Sections[i];
                if (section.MemSize <= 0) continue;
                IntPtr sectionBase = SectionBaseOf(module, i);
                uint protect = (section.Flags & TccSectionFlags.ExecInstr) != 0
                    ? NativeMethods.PageExecuteRead
                    : NativeMethods.PageReadWrite;
                uint old;
                if (!NativeMethods.VirtualProtectFromApp(sectionBase, new IntPtr(AlignUp(section.MemSize, 64)), protect, out old))
                {
                    var error = Marshal.GetLastWin32Error();
                    module.Dispose();
                    throw new InvalidOperationException("VirtualProtectFromApp 失败（" + error
                        + "）。段：" + section.Name + " —— AppContainer 内切可执行页需要 codeGeneration 能力。");
                }
            }

            // ── 8) 入口点 ──
            if (header.EntryRva != 0)
            {
                var entry = RvaToAddress(module, header, header.EntryRva);
                if (module.IsThumb2) entry = new IntPtr(entry.ToInt64() | 1);
                module.EntryPoint = entry;
            }

            return module;
        }

        // ── 内部：镜像结构解析 ──────────────────────────────────────────

        private sealed class SectionInfo
        {
            public string Name = "";
            public uint Type;
            public uint Flags;
            public uint FileOffset;
            public uint FileSize;
            public uint MemSize;
            public uint Alignment;
        }

        private sealed class SymbolInfo
        {
            public string Name = "";
            public uint Value;
            public uint Size;
            public ushort SectionIndex;
            public byte Type;
            public byte Binding;
        }

        private sealed class RelocInfo
        {
            public uint Offset;
            public uint SymbolIndex;
            public ushort Type;
            public ushort SectionIndex;
            public int Addend;
        }

        private sealed class HeaderInfo
        {
            public uint Magic;
            public ushort VersionMajor;
            public ushort VersionMinor;
            public uint Flags;
            public uint EntryRva;
            public List<SectionInfo> Sections = new List<SectionInfo>();
            public List<SymbolInfo> Symbols = new List<SymbolInfo>();
            public List<RelocInfo> Relocs = new List<RelocInfo>();
            public int StringTableOffset;
        }

        private static HeaderInfo ReadHeader(byte[] image)
        {
            // 头部偏移必须严格按规范 §2.1（共 80 字节）：
            //   magic@0 ver_major@4 ver_minor@6 flags@8 timestamp@12 checksum@16
            //   entry_rva@20 sect_off@24 sect_cnt@28(u16) sym_off@32 sym_cnt@36
            //   str_off@40 str_size@44 reloc_off@48 reloc_cnt@52
            //   imp_off@56 imp_cnt@60 exp_off@64 exp_cnt@68
            // 曾经从 12 开始读（漏掉 timestamp/checksum/entry 共 12 字节），
            // 结果 flags 读到 timestamp、段表偏移读到 4 —— 真机上必崩。
            var header = new HeaderInfo();
            header.Magic = ReadUInt32(image, 0);
            header.VersionMajor = ReadUInt16(image, 4);
            header.VersionMinor = ReadUInt16(image, 6);
            header.Flags = ReadUInt32(image, 8);
            header.EntryRva = ReadUInt32(image, 20);

            uint sectionOffset = ReadUInt32(image, 24);
            ushort sectionCount = ReadUInt16(image, 28);
            uint symbolOffset = ReadUInt32(image, 32);
            uint symbolCount = ReadUInt32(image, 36);
            header.StringTableOffset = (int)ReadUInt32(image, 40);
            uint relocOffset = ReadUInt32(image, 48);
            uint relocCount = ReadUInt32(image, 52);

            for (int i = 0; i < sectionCount; i++)
            {
                int o = (int)sectionOffset + i * 32;
                if (o + 32 > image.Length) throw new ArgumentException("段表越界。");
                header.Sections.Add(new SectionInfo
                {
                    Name = ReadString(image, header.StringTableOffset + (int)ReadUInt32(image, o)),
                    Type = ReadUInt32(image, o + 4),
                    Flags = ReadUInt32(image, o + 8),
                    FileOffset = ReadUInt32(image, o + 12),
                    FileSize = ReadUInt32(image, o + 16),
                    MemSize = ReadUInt32(image, o + 20),
                    Alignment = ReadUInt32(image, o + 24)
                });
            }

            for (int i = 0; i < symbolCount; i++)
            {
                int o = (int)symbolOffset + i * 24;
                if (o + 24 > image.Length) throw new ArgumentException("符号表越界。");
                header.Symbols.Add(new SymbolInfo
                {
                    Name = ReadString(image, header.StringTableOffset + (int)ReadUInt32(image, o)),
                    Value = ReadUInt32(image, o + 4),
                    Size = ReadUInt32(image, o + 8),
                    SectionIndex = ReadUInt16(image, o + 12),
                    Type = image[o + 14],
                    Binding = image[o + 15]
                });
            }

            for (int i = 0; i < relocCount; i++)
            {
                int o = (int)relocOffset + i * 16;
                if (o + 16 > image.Length) throw new ArgumentException("重定位表越界。");
                header.Relocs.Add(new RelocInfo
                {
                    Offset = ReadUInt32(image, o),
                    SymbolIndex = ReadUInt32(image, o + 4),
                    Type = ReadUInt16(image, o + 8),
                    SectionIndex = ReadUInt16(image, o + 10),
                    Addend = ReadInt32(image, o + 12)
                });
            }

            return header;
        }

        private static IntPtr SectionBaseOf(TccModule module, int index)
        {
            // 段基址按加载顺序存放（与解析段表一致）
            return module.SectionBaseAt(index);
        }

        private static IntPtr RvaToAddress(TccModule module, HeaderInfo header, uint rva)
        {
            long cursor = 0;
            for (int i = 0; i < header.Sections.Count; i++)
            {
                long size = AlignUp(header.Sections[i].MemSize, 64);
                if (rva >= cursor && rva < cursor + size)
                {
                    return new IntPtr(module.SectionBaseAt(i).ToInt64() + (rva - cursor));
                }
                cursor += size;
            }
            return IntPtr.Zero;
        }

        // ── 内部：ARM Thumb-2 指令补丁 ─────────────────────────────────

        private static void ApplyThumbBranch24(IntPtr place, IntPtr target, int addend)
        {
            ushort hi = (ushort)Marshal.ReadInt16(place);
            ushort lo = (ushort)Marshal.ReadInt16(place, 2);

            // 目标去掉 Thumb 位；PC 基准是 place + 4
            long destination = (target.ToInt64() & ~1L) + addend;
            long offset = destination - (place.ToInt64() + 4);

            // offset 必须能放进 25 位有符号数
            if (offset < -(1L << 24) || offset > (1L << 24) - 1)
            {
                throw new InvalidOperationException("Thumb 分支跳转距离超范围（" + offset + " 字节）。");
            }

            int s = (int)((offset >> 24) & 1);
            int i1 = (int)((offset >> 23) & 1);
            int i2 = (int)((offset >> 22) & 1);
            int imm10 = (int)((offset >> 12) & 0x3FF);
            int imm11 = (int)((offset >> 1) & 0x7FF);
            int j1 = 1 - (i1 ^ s);
            int j2 = 1 - (i2 ^ s);

            ushort newHi = (ushort)((hi & 0xF800) | (s << 10) | imm10);
            ushort newLo = (ushort)((lo & 0xD000) | (j1 << 13) | (1 << 12) | (j2 << 11) | imm11);
            Marshal.WriteInt16(place, (short)newHi);
            Marshal.WriteInt16(place, 2, (short)newLo);
        }

        private static void ApplyThumbMovImm(IntPtr place, int value, bool high)
        {
            ushort hi = (ushort)Marshal.ReadInt16(place);
            ushort lo = (ushort)Marshal.ReadInt16(place, 2);
            int imm16 = high ? ((value >> 16) & 0xFFFF) : (value & 0xFFFF);
            int imm4 = (imm16 >> 12) & 0xF;
            int i = (imm16 >> 11) & 0x1;
            int imm3 = (imm16 >> 8) & 0x7;
            int imm8 = imm16 & 0xFF;

            ushort newHi = (ushort)((hi & 0xFBF0) | (i << 10) | imm4);
            ushort newLo = (ushort)((lo & 0x8F00) | (imm3 << 8) | imm8);
            Marshal.WriteInt16(place, (short)newHi);
            Marshal.WriteInt16(place, 2, (short)newLo);
        }

        // ── 内部：小工具 ───────────────────────────────────────────────

        private static long AlignUp(long value, long alignment)
        {
            if (alignment <= 1) return value;
            return (value + alignment - 1) / alignment * alignment;
        }

        private static void WriteInt32(IntPtr place, int value)
        {
            Marshal.WriteInt32(place, value);
        }

        private static uint ReadUInt32(byte[] image, int offset)
        {
            return (uint)(image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24));
        }

        private static int ReadInt32(byte[] image, int offset)
        {
            return image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);
        }

        private static ushort ReadUInt16(byte[] image, int offset)
        {
            return (ushort)(image[offset] | (image[offset + 1] << 8));
        }

        private static string ReadString(byte[] image, int offset)
        {
            if (offset < 0 || offset >= image.Length) return "";
            int end = offset;
            while (end < image.Length && image[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(image, offset, end - offset);
        }

    }

    /// <summary>AppContainer 允许的内存 API（*FromApp 变体）。</summary>
    internal static class NativeMethods
    {
        public const uint MemCommit = 0x1000;
        public const uint MemReserve = 0x2000;
        public const uint MemRelease = 0x8000;
        public const uint PageReadWrite = 0x04;
        public const uint PageExecuteRead = 0x20;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr VirtualAllocFromApp(IntPtr baseAddress, IntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool VirtualProtectFromApp(IntPtr address, IntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool VirtualFree(IntPtr address, IntPtr size, uint freeType);
    }
}