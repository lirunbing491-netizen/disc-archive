using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace DiscArchive
{
    internal sealed class DiscVolume
    {
        public string Format, Name, ConsistencyNote;
        public long Offset, Length;
    }

    internal sealed class DiscLayout
    {
        public List<DiscVolume> Volumes = new List<DiscVolume>();
        public string Note = "";
        public string IncompleteApple = "";
        public bool HasIso, HasUdf, HasPartitionMap;
    }

    // Metadata inspection only. A recognized filesystem still needs an archive
    // reader to enumerate real entries before it can be offered for copying.
    internal static class OpticalLayout
    {
        public static DiscLayout Inspect(Stream raw)
        {
            if (raw == null) throw new ArgumentNullException("raw");
            if (!raw.CanRead || !raw.CanSeek) throw new ArgumentException("需要可定位的只读数据源。", "raw");
            DiscLayout result = new DiscLayout();
            lock (DiscSlice.SourceLock(raw))
            {
                long saved = raw.Position;
                try
                {
                    long size = raw.Length;
                    if (size < 512) { Note(result, "数据不足以识别光盘布局。"); return result; }
                    ProbeCandidate(raw, result, 0, size, "裸卷");
                    ProbeOptical(raw, result, size);
                    byte[] boot = new byte[512];
                    TryProbe(result, "引导区", delegate { boot = Read(raw, 0, 512); });
                    bool apm = false;
                    int declared = BE16(boot, 0) == 0x4552 ? BE16(boot, 2) : 0;
                    if (declared == 512 || declared == 2048 || declared == 4096)
                        TryProbe(result, "APM", delegate { apm = Apm(raw, result, size, declared); });
                    if (!apm) TryProbe(result, "APM 512", delegate { apm = Apm(raw, result, size, 512); });
                    if (!apm) TryProbe(result, "APM 2048", delegate { Apm(raw, result, size, 2048); });
                    bool gpt = false;
                    TryProbe(result, "GPT 512", delegate { gpt = Gpt(raw, result, size, 512); });
                    if (!gpt) TryProbe(result, "GPT 2048", delegate { gpt = Gpt(raw, result, size, 2048); });
                    if (!gpt && boot[510] == 0x55 && boot[511] == 0xAA)
                        for (int i = 0; i < 4; i++)
                        {
                            int p = 446 + 16 * i;
                            if (boot[p + 4] != 0xAF) continue;
                            long start = LE32(boot, p + 8) * 512L, length = LE32(boot, p + 12) * 512L;
                            result.HasPartitionMap = true;
                            if (Range(start, length, size)) ProbeApplePartition(raw, result, start, length, "MBR " + (i + 1));
                            else MissingApple(result, "MBR " + (i + 1) + " 分区边界无效。");
                        }
                    if (result.HasIso && result.HasUdf) Note(result, "ISO/UDF bridge。");
                    bool mac = result.Volumes.Exists(delegate(DiscVolume v) { return v.Format == "HFS" || v.Format == "HFSX" || v.Format == "ClassicHFS"; });
                    if (mac && (result.HasIso || result.HasUdf)) Note(result, "含 Mac 文件系统的 hybrid 光盘。");
                    if (result.Volumes.Count == 0) Note(result, "未确认受支持的文件系统；分区表或标记本身不代表文件可读。");
                    return result;
                }
                finally { raw.Position = saved; }
            }
        }

        private static bool Apm(Stream raw, DiscLayout result, long size, int unit)
        {
            if (!Range(unit, 512, size)) return false;
            byte[] first = Read(raw, unit, 512);
            if (BE16(first, 0) != 0x504D) return false;
            result.HasPartitionMap = true;
            uint count = BE32(first, 4);
            if (count == 0 || count > 4096 || (count + 1L) * unit > size)
            { Note(result, "APM 分区条目数量或边界无效；未继续解析。"); return true; }
            for (int i = 0; i < count; i++)
            {
                byte[] part;
                try { part = i == 0 ? first : Read(raw, (i + 1L) * unit, 512); }
                catch (IOException ex) { Note(result, "探测读取失败：APM 条目。" + ex.Message); continue; }
                if (BE16(part, 0) != 0x504D || BE32(part, 4) != count)
                { Note(result, "APM 分区条目签名或数量不一致。"); continue; }
                string type = Text(part, 48, 32);
                if (type != "Apple_HFS" && type != "Apple_HFSX" && type != "Apple_APFS") continue;
                long start = BE32(part, 8) * (long)unit, length = BE32(part, 12) * (long)unit;
                if (!Range(start, length, size)) { MissingApple(result, "APM Apple 分区边界无效：" + (i + 1)); continue; }
                ProbeApplePartition(raw, result, start, length, Text(part, 16, 32));
            }
            return true;
        }

        private static bool Gpt(Stream raw, DiscLayout result, long size, int unit)
        {
            if (!Range(unit, unit, size)) return false;
            byte[] h = Read(raw, unit, unit);
            if (Text(h, 0, 8) != "EFI PART") return false;
            result.HasPartitionMap = true;
            uint headerSize = LE32(h, 12), saved = LE32(h, 16);
            if (LE32(h, 8) != 0x10000 || headerSize < 92 || headerSize > unit || LE64(h, 24) != 1)
            { Note(result, "GPT 头部版本或边界无效。"); return true; }
            Array.Clear(h, 16, 4);
            if (Crc32(h, (int)headerSize) != saved) { Note(result, "GPT 头部校验失败。"); return true; }
            ulong first = LE64(h, 40), last = LE64(h, 48), table = LE64(h, 72);
            uint count = LE32(h, 80), entry = LE32(h, 84);
            if (first > last || last >= (ulong)(size / unit) || count == 0 || count > 4096 || entry < 128 || entry > 1024 || entry % 128 != 0 || table > (ulong)(size / unit))
            { Note(result, "GPT 分区表边界无效或超过安全解析上限。"); return true; }
            long bytes = (long)count * entry, offset = (long)table * unit;
            if (!Range(offset, bytes, size)) { Note(result, "GPT 分区表超出介质范围。"); return true; }
            byte[] entries = Read(raw, offset, (int)bytes);
            if (Crc32(entries, entries.Length) != LE32(h, 88)) { Note(result, "GPT 分区表校验失败。"); return true; }
            Guid hfs = new Guid("48465300-0000-11AA-AA11-00306543ECAC"), apfs = new Guid("7C3457EF-0000-11AA-AA11-00306543ECAC");
            for (int i = 0; i < count; i++)
            {
                int at = i * (int)entry;
                byte[] guid = new byte[16]; Buffer.BlockCopy(entries, at, guid, 0, 16);
                Guid type = new Guid(guid);
                if (type != hfs && type != apfs) continue;
                ulong start = LE64(entries, at + 32), end = LE64(entries, at + 40);
                if (start < first || end > last || start > end) { MissingApple(result, "GPT Apple 分区范围无效：" + (i + 1)); continue; }
                string name = Encoding.Unicode.GetString(entries, at + 56, 72).TrimEnd('\0');
                ProbeApplePartition(raw, result, (long)start * unit, ((long)(end - start) + 1) * unit, name);
            }
            return true;
        }

        private static void ProbeVolume(Stream raw, DiscLayout result, long start, long length, string name, int depth = 0)
        {
            if (!Range(start, length, raw.Length) || length < 1536) return;
            byte[] h = Read(raw, start + 1024, 512);
            ushort sig = BE16(h, 0), version = BE16(h, 2);
            if (sig == 0x482B || sig == 0x4858)
            {
                uint block = BE32(h, 40), count = BE32(h, 44);
                long actual = block * (long)count;
                if (version != (sig == 0x482B ? 4 : 5) || !Power(block, 512, 1u << 30) || count < 4 ||
                    actual > length || actual < 2048 || BE32(h, 48) > count)
                { Note(result, "HFS 卷头版本或容量边界无效。"); return; }
                // Validate all initial fork extents without reading file data.
                for (int f = 112; f <= 432; f += 80)
                {
                    if (BE64(h, f) > (ulong)actual || BE32(h, f + 12) > count)
                    { Note(result, "HFS 元数据文件边界无效。"); return; }
                    for (int e = f + 16; e < f + 80; e += 8)
                        if ((ulong)BE32(h, e) + BE32(h, e + 4) > count)
                        { Note(result, "HFS 元数据区段越界。"); return; }
                }
                Add(result, sig == 0x482B ? "HFS" : "HFSX", name, start, actual);
                uint attributes=BE32(h,4);
                if((attributes&0x100u)==0 || (attributes&0x800u)!=0)
                {
                    var volume=result.Volumes.Find(v=>v.Offset==start && v.Length==actual && (v.Format=="HFS" || v.Format=="HFSX"));
                    if(volume!=null) volume.ConsistencyNote="HFS 卷头未确认正常卸载或标记为不一致。本次只读提取，不重放日志，目录完整性待复查。";
                }
                return;
            }
            if (sig == 0x4244)
            {
                uint block = BE32(h, 20); ushort count = BE16(h, 18), baseBlock = BE16(h, 28);
                long allocation = baseBlock * 512L, allocLength = count * (long)block;
                if (!Power(block, 512, 1u << 30) || count == 0 || !Range(allocation, allocLength, length))
                { Note(result, "Classic HFS 卷头边界无效。"); return; }
                ushort embedded = BE16(h, 124);
                if (embedded == 0x482B || embedded == 0x4858)
                {
                    // TN1150: allocation base is ALWAYS in 512-byte sectors;
                    // embedded start/count use the wrapper allocation size.
                    long relative = allocation + BE16(h, 126) * (long)block;
                    long span = BE16(h, 128) * (long)block;
                    if (depth != 0 || relative < 1536 || !Range(relative, span, length) || span < 1536 ||
                        BE16(h, 126) + (uint)BE16(h, 128) > count)
                    { Note(result, "HFS wrapper 嵌入卷越界。"); return; }
                    ProbeVolume(raw, result, start + relative, span, name, depth + 1);
                    Note(result, "HFS wrapper 已按 TN1150 定位嵌入卷。");
                }
                else
                {
                    int n = Math.Min((int)h[36], 27);
                    string classicName = Encoding.GetEncoding(10000).GetString(h, 37, n);
                    Add(result, "ClassicHFS", String.IsNullOrEmpty(name) ? classicName : name, start, length);
                    Note(result, "Classic HFS 仅识别；当前读取器不承诺支持文件提取。");
                }
                return;
            }
            ProbeApfs(raw, result, start, length, name);
        }

        private static void ProbeApfs(Stream raw, DiscLayout result, long start, long length, string name)
        {
            byte[] head = Read(raw, start, 512);
            if (Text(head, 32, 4) != "NXSB") return;
            uint block = LE32(head, 36); ulong count = LE64(head, 40);
            if (!Power(block, 4096, 65536) || count == 0 || count > (ulong)(length / block))
            { Note(result, "APFS 容器边界无效。"); return; }
            byte[] nx = Read(raw, start, (int)block);
            if (!ApfsChecksum(nx) || (LE32(nx, 24) & 0xFFFF) != 1 || LE32(nx, 180) > 100)
            { Note(result, "APFS 容器校验或类型无效。"); return; }
            long span = (long)count * block;
            ulong omap = LE64(nx, 160), xid = LE64(nx, 16);
            for (int i = 0; i < 100; i++)
            {
                ulong oid = LE64(nx, 184 + i * 8);
                if (oid == 0) continue;
                ulong physical = ApfsLookup(raw, start, span, block, omap, oid, xid);
                if (physical == 0 || physical >= count) continue;
                byte[] volume = Read(raw, start + (long)physical * block, (int)block);
                if (Text(volume, 32, 4) == "APSB" && ApfsChecksum(volume) &&
                    (LE32(volume, 24) & 0xFFFF) == 13 && LE64(volume, 8) == oid && LE64(volume, 136) != 0)
                {
                    Add(result, "APFS", name, start, span);
                    Note(result, "已确认 APFS 卷超级块；加密和实际文件可读性仍须由读取器验证。");
                    return;
                }
            }
            Note(result, "发现 APFS 容器，但未确认有效卷；未作为可读取文件系统。");
        }

        // Bounded physical OMAP traversal; fixed-size keys only. Unsupported or
        // malformed trees are not guessed and never trigger a disk-wide scan.
        private static ulong ApfsLookup(Stream raw, long start, long span, uint block, ulong omap, ulong oid, ulong xid)
        {
            if (omap == 0 || omap >= (ulong)(span / block)) return 0;
            byte[] map = Read(raw, start + (long)omap * block, (int)block);
            if (!ApfsChecksum(map) || (LE32(map, 24) & 0xFFFF) != 11) return 0;
            ulong node = LE64(map, 48);
            HashSet<ulong> seen = new HashSet<ulong>();
            for (int depth = 0; depth < 16; depth++)
            {
                if (node == 0 || node >= (ulong)(span / block) || !seen.Add(node)) return 0;
                byte[] b = Read(raw, start + (long)node * block, (int)block);
                uint type = LE32(b, 24) & 0xFFFF;
                if (!ApfsChecksum(b) || (type != 2 && type != 3) || (LE16(b, 32) & 4) == 0) return 0;
                bool leaf = (LE16(b, 32) & 2) != 0, root = (LE16(b, 32) & 1) != 0;
                uint keys = LE32(b, 36);
                int table = 56 + LE16(b, 40), tableLength = LE16(b, 42), valueEnd = b.Length - (root ? 40 : 0);
                int keyBase = table + tableLength;
                if (keys == 0 || keys > block / 4 || tableLength < keys * 4 || table < 56 || keyBase > valueEnd) return 0;
                int selected = -1;
                for (int i = 0; i < keys; i++)
                {
                    int key = keyBase + LE16(b, table + i * 4);
                    if (key < keyBase || key > valueEnd - 16) return 0;
                    ulong koid = LE64(b, key), kxid = LE64(b, key + 8);
                    if (koid < oid || (koid == oid && kxid <= xid)) selected = i;
                    else break;
                }
                if (selected < 0) return 0;
                int keyAt = keyBase + LE16(b, table + selected * 4);
                int value = valueEnd - LE16(b, table + selected * 4 + 2);
                if (value < keyBase || value > valueEnd - (leaf ? 16 : 8)) return 0;
                if (!leaf) { node = LE64(b, value); continue; }
                if (LE64(b, keyAt) != oid || (LE32(b, value) & 1) != 0 || LE32(b, value + 4) != block) return 0;
                return LE64(b, value + 8);
            }
            return 0;
        }

        private static void ProbeOptical(Stream raw, DiscLayout result, long size)
        {
            bool bea = false, nsr = false, tea = false, terminated = false;
            DiscVolume iso = null;
            for (int sector = 16; sector < 80 && Range(sector * 2048L, 2048, size); sector++)
            {
                byte[] b;
                try { b = Read(raw, sector * 2048L, 2048); }
                catch (IOException ex) { Note(result, "探测读取失败：光盘描述符区。" + ex.Message); continue; }
                string id = Text(b, 1, 5);
                if (id == "BEA01" && b[0] == 0 && b[6] == 1) bea = true;
                if ((id == "NSR02" || id == "NSR03") && b[0] == 0 && b[6] == 1 && bea) nsr = true;
                if (id == "TEA01" && b[0] == 0 && b[6] == 1 && nsr) tea = true;
                if (id != "CD001" || b[6] != 1) continue;
                if (b[0] == 255) { terminated = true; continue; }
                if (b[0] != 1 && b[0] != 2) continue;
                uint blocks = LE32(b, 80); ushort unit = LE16(b, 128);
                uint root = LE32(b, 158), bytes = LE32(b, 166);
                if (blocks != BE32(b, 84) || unit != BE16(b, 130) || unit != 2048 || blocks < 18 ||
                    blocks > size / 2048 || b[156] < 34 || (b[181] & 2) == 0 || root != BE32(b, 162) || bytes != BE32(b, 170) ||
                    bytes < 34 || !Range(root * 2048L, bytes, blocks * 2048L))
                { Note(result, "ISO 卷描述符字段或边界无效。"); continue; }
                byte[] directory;
                try { directory = Read(raw, root * 2048L, 34); }
                catch (IOException ex) { Note(result, "探测读取失败：ISO 根目录。" + ex.Message); continue; }
                if (directory[0] < 34 || directory[32] != 1 || directory[33] != 0 || (directory[25] & 2) == 0)
                { Note(result, "ISO 根目录记录无效。"); continue; }
                bool joliet = b[0] == 2 && b[88] == 0x25 && b[89] == 0x2F && (b[90] == 0x40 || b[90] == 0x43 || b[90] == 0x45);
                if (b[0] == 1 || (iso != null && joliet))
                    iso = new DiscVolume { Format = "ISO", Offset = 0, Length = blocks * 2048L,
                        Name = joliet ? Encoding.BigEndianUnicode.GetString(b, 40, 32).TrimEnd('\0', ' ') : Text(b, 40, 32) };
                if (joliet) Note(result, "含 Joliet 文件名扩展。");
            }
            if (iso != null && terminated)
            { result.HasIso = true; Add(result, "ISO", iso.Name, 0, iso.Length); }
            if (bea && nsr && tea)
            {
                TryProbe(result, "UDF", delegate
                {
                    string label;
                    if (Udf(raw, size, out label)) { result.HasUdf = true; Add(result, "UDF", label, 0, size); }
                    else Note(result, "存在 UDF 识别序列，但未确认有效文件系统结构。");
                });
            }
        }

        private static bool Udf(Stream raw, long size, out string label)
        {
            label = "";
            if (!Range(256 * 2048L, 2048, size)) return false;
            byte[] anchor = Read(raw, 256 * 2048L, 2048);
            if (!Tag(anchor, 2, 256)) return false;
            uint span = LE32(anchor, 16), first = LE32(anchor, 20);
            if (span < 2048 || span > 1024 * 1024 || !Range(first * 2048L, span, size)) return false;
            Dictionary<ushort, long[]> partitions = new Dictionary<ushort, long[]>();
            byte[] logical = null;
            bool ended = false;
            for (uint i = 0; i < span / 2048; i++)
            {
                byte[] d = Read(raw, (first + i) * 2048L, 2048);
                ushort type = LE16(d, 0);
                if (!Tag(d, type, first + i)) return false;
                if (type == 8) { ended = true; break; }
                if (type == 5)
                {
                    long start = LE32(d, 188) * 2048L, length = LE32(d, 192) * 2048L;
                    if (!Range(start, length, size)) return false;
                    partitions[LE16(d, 22)] = new long[] { start, length };
                }
                if (type == 6 && LE32(d, 212) == 2048) logical = d;
            }
            if (!ended || logical == null || LE32(logical, 264) != 6 || LE32(logical, 268) != 1 || logical[440] != 1 || logical[441] != 6) return false;
            long[] partition;
            if (!partitions.TryGetValue(LE16(logical, 444), out partition) || LE16(logical, 256) != 0) return false;
            long fsd = LE32(logical, 252) * 2048L;
            if ((LE32(logical, 248) & 0xC0000000u) != 0 || !Range(fsd, 2048, partition[1])) return false;
            byte[] fileSet = Read(raw, partition[0] + fsd, 2048);
            if (!Tag(fileSet, 256, LE32(logical, 252)) || LE16(fileSet, 408) != 0) return false;
            long root = LE32(fileSet, 404) * 2048L;
            if (!Range(root, 2048, partition[1]) || (LE32(fileSet, 400) & 0xC0000000u) != 0) return false;
            byte[] entry = Read(raw, partition[0] + root, 2048);
            ushort kind = LE16(entry, 0);
            if ((kind != 261 && kind != 266) || !Tag(entry, kind, LE32(fileSet, 404)) || entry[27] != 4) return false;
            // LVD offsets follow ECMA-167 3/10.6: descriptor character set at
            // 20 and logical volume identifier D-string at 84 (128 bytes).
            label = UdfLabel(logical);
            return true;
        }

        private static string UdfLabel(byte[] logical)
        {
            if (logical[20] != 0 || Text(logical, 21, 63) != "OSTA Compressed Unicode") return "";
            int used = logical[211];
            if (used == 0) return "";
            if (used < 2 || used > 127) return "";
            for (int i = 84 + used; i < 211; i++) if (logical[i] != 0) return "";
            int compression = logical[84];
            StringBuilder value = new StringBuilder();
            if (compression == 8)
                for (int i = 85; i < 84 + used; i++) value.Append((char)logical[i]);
            else if (compression == 16 && (used - 1) % 2 == 0)
                for (int i = 85; i < 84 + used; i += 2) value.Append((char)BE16(logical, i));
            else return "";
            string name = value.ToString();
            foreach (char c in name) if (Char.IsControl(c) || Char.IsSurrogate(c)) return "";
            return String.IsNullOrWhiteSpace(name) ? "" : name;
        }

        private static bool Tag(byte[] b, ushort type, uint location)
        {
            if (LE16(b, 0) != type || (LE16(b, 2) != 2 && LE16(b, 2) != 3) || LE32(b, 12) != location) return false;
            int sum = 0; for (int i = 0; i < 16; i++) if (i != 4) sum += b[i];
            int length = LE16(b, 10);
            int minimum = type == 2 || type == 5 || type == 8 || type == 256 ? 496 : type == 6 ? 430 : type == 261 || type == 266 ? 160 : 1;
            if ((byte)sum != b[4] || length < minimum || length > b.Length - 16) return false;
            ushort crc = 0;
            for (int i = 16; i < 16 + length; i++)
            { crc ^= (ushort)(b[i] << 8); for (int j = 0; j < 8; j++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0)); }
            return crc == LE16(b, 8);
        }

        private static bool ApfsChecksum(byte[] b)
        {
            ulong lo = 0, hi = 0, mod = 0xFFFFFFFFUL;
            for (int i = 8; i < b.Length; i += 4) { lo = (lo + LE32(b, i)) % mod; hi = (hi + lo) % mod; }
            ulong low = mod - (lo + hi) % mod, high = mod - (lo + low) % mod;
            return LE64(b, 0) == (high << 32 | low);
        }
        private static bool Range(long start, long length, long total) { return start >= 0 && length > 0 && start <= total && length <= total - start; }
        private static bool Power(uint n, uint minimum, uint maximum) { return n >= minimum && n <= maximum && (n & (n - 1)) == 0; }
        private static void Add(DiscLayout d, string format, string name, long start, long length)
        {
            if (!d.Volumes.Exists(delegate(DiscVolume v) { return v.Format == format && v.Offset == start && v.Length == length; }))
                d.Volumes.Add(new DiscVolume { Format = format, Name = String.IsNullOrWhiteSpace(name) ? (format == "UDF" ? "" : format) : name, Offset = start, Length = length });
        }
        private static void Note(DiscLayout d, string text) { if (!d.Note.Contains(text)) d.Note += (d.Note.Length == 0 ? "" : " ") + text; }
        private static void TryProbe(DiscLayout d, string region, Action probe)
        {
            try { probe(); }
            catch (IOException ex) { Note(d, "探测读取失败：" + region + "。" + ex.Message); }
            catch (UnauthorizedAccessException ex) { Note(d, "探测访问失败：" + region + "。" + ex.Message); }
        }
        // Called only after a trustworthy map entry explicitly names an Apple
        // data partition. Failed speculative probes on ordinary discs do not
        // establish that an Apple volume has been lost.
        private static void ProbeApplePartition(Stream raw, DiscLayout d, long start, long length, string name)
        {
            ProbeCandidate(raw, d, start, length, name);
            if (!d.Volumes.Exists(delegate(DiscVolume v) {
                return v.Offset >= start && v.Offset < start + length &&
                    (v.Format == "HFS" || v.Format == "HFSX" || v.Format == "ClassicHFS" || v.Format == "APFS");
            })) MissingApple(d, "已声明的 Apple 分区未能确认文件系统：" + name + "，偏移 " + start + "。");
        }
        private static void MissingApple(DiscLayout d, string reason)
        {
            if (!d.IncompleteApple.Contains(reason)) d.IncompleteApple += (d.IncompleteApple.Length == 0 ? "" : " ") + reason;
            Note(d, reason);
        }
        private static void ProbeCandidate(Stream raw, DiscLayout d, long start, long length, string name)
        { TryProbe(d, "分区 " + start, delegate { ProbeVolume(raw, d, start, length, name); }); }
        private static byte[] Read(Stream s, long offset, int count)
        {
            if (!Range(offset, count, s.Length)) throw new EndOfStreamException("布局元数据超出数据源范围。");
            byte[] b = new byte[count]; s.Position = offset;
            for (int at = 0; at < count; ) { int n = s.Read(b, at, count - at); if (n == 0) throw new EndOfStreamException("布局元数据读取不完整。"); at += n; }
            return b;
        }
        private static string Text(byte[] b, int at, int count) { return Encoding.ASCII.GetString(b, at, count).TrimEnd('\0', ' '); }
        private static ushort BE16(byte[] b, int p) { return (ushort)((b[p] << 8) | b[p + 1]); }
        private static uint BE32(byte[] b, int p) { return (uint)b[p] << 24 | (uint)b[p + 1] << 16 | (uint)b[p + 2] << 8 | b[p + 3]; }
        private static ulong BE64(byte[] b, int p) { return (ulong)BE32(b, p) << 32 | BE32(b, p + 4); }
        private static ushort LE16(byte[] b, int p) { return (ushort)(b[p] | b[p + 1] << 8); }
        private static uint LE32(byte[] b, int p) { return b[p] | (uint)b[p + 1] << 8 | (uint)b[p + 2] << 16 | (uint)b[p + 3] << 24; }
        private static ulong LE64(byte[] b, int p) { return LE32(b, p) | (ulong)LE32(b, p + 4) << 32; }
        internal static uint Crc32(byte[] data, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < count; i++) { crc ^= data[i]; for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
            return ~crc;
        }
    }

    internal sealed class DiscSlice : Stream
    {
        private static readonly ConditionalWeakTable<Stream, object> Locks = new ConditionalWeakTable<Stream, object>();
        private readonly Stream source;
        private readonly object sync;
        private readonly long offset, length;
        private long position;
        private bool disposed;
        internal static object SourceLock(Stream s) { return Locks.GetValue(s, delegate(Stream ignored) { return new object(); }); }
        internal DiscSlice(Stream source, long offset, long length)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!source.CanRead || !source.CanSeek) throw new ArgumentException("需要可读、可定位的数据源。");
            sync = SourceLock(source);
            lock (sync)
                if (offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset) throw new ArgumentOutOfRangeException();
            this.source = source; this.offset = offset; this.length = length;
        }
        public override bool CanRead { get { return !disposed; } }
        public override bool CanSeek { get { return !disposed; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { lock (sync) { Open(); return length; } } }
        public override long Position { get { lock (sync) { Open(); return position; } } set { Seek(value, SeekOrigin.Begin); } }
        public override int Read(byte[] buffer, int at, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (at < 0 || count < 0 || at > buffer.Length - count) throw new ArgumentOutOfRangeException();
            lock (sync)
            {
                Open();
                int take = (int)Math.Min(count, Math.Max(0, length - position));
                if (take == 0) return 0;
                source.Position = checked(offset + position);
                int read = source.Read(buffer, at, take);
                if (read == 0) throw new EndOfStreamException("分区数据提前结束。");
                position += read; return read;
            }
        }
        public override long Seek(long n, SeekOrigin origin)
        {
            lock (sync)
            {
                Open(); long basis;
                switch (origin) { case SeekOrigin.Begin: basis = 0; break; case SeekOrigin.Current: basis = position; break; case SeekOrigin.End: basis = length; break; default: throw new ArgumentException("定位方式无效。"); }
                long next; try { next = checked(basis + n); } catch (OverflowException) { throw new IOException("定位超出范围。"); }
                if (next < 0) throw new IOException("不能定位到分区起点前。");
                return position = next;
            }
        }
        private void Open() { if (disposed) throw new ObjectDisposedException("DiscSlice"); }
        public override void Flush() { lock (sync) { Open(); } }
        public override void SetLength(long n) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int at, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { lock (sync) { disposed = true; } base.Dispose(disposing); }
    }
}
