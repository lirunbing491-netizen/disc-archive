using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiscArchive
{
    internal sealed class HfsNativeLink
    {
        public uint CNID, ParentCNID, LinkReference;
        public bool Directory;
        public string Name, Path;
        // Original on-disk UTF-16 components. Do not split Path to recover these.
        public IList<string> Components;
    }
    internal sealed class HfsGuardResult
    {
        public bool Complete, Audited;
        public int Links;
        public string Note;
        public IList<HfsNativeLink> Entries;
    }
    // A bounded metadata audit, not a filesystem reader or hardlink resolver.
    // Layout: Apple TN1150. Directory-link distinction: Apple's hfs_link.c
    // createindirectlink sets fdrp/MACS AND kHFSHasLinkChainMask. fdrp/MACS
    // alone is also used by ordinary Finder folder aliases and is not proof.
    internal static class HfsCatalogGuard
    {
        private const long ReadLimit = 256L * 1024 * 1024;
        private const int NodeLimit = 32768, EntryLimit = 250000, NameLimit = 16 * 1024 * 1024;
        public static HfsGuardResult Inspect(Stream hfsVolume)
        {
            if (hfsVolume == null) throw new ArgumentNullException("hfsVolume");
            if (!hfsVolume.CanRead || !hfsVolume.CanSeek) throw new ArgumentException("A readable, seekable HFS volume slice is required.");
            long saved = hfsVolume.Position;
            Audit audit = new Audit(hfsVolume);
            try { audit.Run(); }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is InvalidDataException) && !(ex is OverflowException) && !(ex is ArgumentException)) throw;
                audit.Failure = ex.Message;
            }
            finally { hfsVolume.Position = saved; }
            return audit.Result();
        }
        private sealed class CatalogEntry
        {
            internal uint Id, Parent;
            internal string Name;
        }
        private sealed class Extent
        {
            internal long Logical, Physical, Length;
        }
        private sealed class Audit
        {
            private readonly Stream stream;
            private readonly List<Extent> extents = new List<Extent>();
            private readonly Dictionary<uint, CatalogEntry> entries = new Dictionary<uint, CatalogEntry>();
            private readonly List<HfsNativeLink> links = new List<HfsNativeLink>();
            private long bytesRead, catalogSize;
            private int nameChars;
            internal string Failure;
            private bool audited;
            internal Audit(Stream stream) { this.stream = stream; }
            private static void Require(bool condition, string note) { if (!condition) throw new InvalidDataException(note); }
            private byte[] Physical(long position, int length)
            {
                Require(length >= 0 && position >= 0 && position <= stream.Length - length, "HFS元数据位置超出卷切片。");
                Require(bytesRead <= ReadLimit - length, "HFS目录审计达到总读取上限，尚未确认完整。");
                bytesRead += length;
                byte[] result = new byte[length]; stream.Position = position;
                int done = 0;
                while (done < length)
                {
                    int n = stream.Read(result, done, length - done);
                    if (n <= 0) throw new EndOfStreamException("HFS目录元数据读取提前结束。");
                    done += n;
                }
                return result;
            }
            private byte[] Catalog(long position, int length)
            {
                Require(position >= 0 && position <= catalogSize - length, "HFS目录节点超出catalog逻辑大小。");
                byte[] result = new byte[length]; int copied = 0;
                foreach (Extent extent in extents)
                {
                    if (position < extent.Logical || position >= extent.Logical + extent.Length) continue;
                    int part = (int)Math.Min(length - copied, extent.Logical + extent.Length - position);
                    byte[] data = Physical(checked(extent.Physical + position - extent.Logical), part);
                    Buffer.BlockCopy(data, 0, result, copied, part);
                    copied += part; position += part;
                    if (copied == length) return result;
                }
                throw new InvalidDataException("catalog头部extent不能覆盖所需目录节点；未实现overflow审计。");
            }
            internal void Run()
            {
                byte[] volume = Physical(1024, 512);
                uint signature = U16(volume, 0), version = U16(volume, 2);
                Require((signature == 0x482B && version == 4) || (signature == 0x4858 && version == 5), "不是直接HFS+/HFSX卷切片，未完成原生硬链接审计。");
                uint blockSize = U32(volume, 40), blocks = U32(volume, 44);
                Require(blockSize >= 512 && blockSize <= 0x40000000 && (blockSize & (blockSize - 1)) == 0 && blocks != 0, "HFS分配块大小或数量无效。");
                ulong size = U64(volume, 272);
                Require(size > 0 && size <= long.MaxValue, "HFS catalog大小无效。");
                catalogSize = (long)size;
                uint catalogBlocks = U32(volume, 284);
                long logical = 0; ulong coveredBlocks = 0;
                for (int i = 0; i < 8; i++)
                {
                    uint start = U32(volume, 288 + i * 8), count = U32(volume, 292 + i * 8);
                    if (count == 0) continue;
                    Require((ulong)start + count <= blocks, "HFS catalog extent超出卷分配块。");
                    long physical = checked((long)start * blockSize), length = checked((long)count * blockSize);
                    Require(physical <= stream.Length - length, "HFS catalog extent超出卷切片。");
                    foreach (Extent previous in extents)
                        Require(physical >= previous.Physical + previous.Length || previous.Physical >= physical + length, "HFS catalog extents相互重叠。");
                    extents.Add(new Extent { Logical = logical, Physical = physical, Length = length });
                    logical = checked(logical + length); coveredBlocks += count;
                }
                Require(coveredBlocks == catalogBlocks && logical >= catalogSize, "HFS catalog使用overflow extents或分配信息不全；数据叉可读不能证明原生硬链接完整，审计未完成。");
                byte[] header = Catalog(0, 120);
                Require(header[8] == 1, "HFS catalog头节点类型无效。");
                int nodeSize = U16(header, 32);
                Require(nodeSize >= 512 && nodeSize <= 65536 && (nodeSize & (nodeSize - 1)) == 0, "HFS catalog节点大小无效。");
                uint totalNodes = U32(header, 36), firstLeaf = U32(header, 24), lastLeaf = U32(header, 28), leafRecords = U32(header, 20);
                Require(totalNodes > 0 && (ulong)totalNodes * (uint)nodeSize <= (ulong)catalogSize, "HFS catalog节点数量超出逻辑大小。");
                Require(firstLeaf != 0 && lastLeaf != 0, "HFS catalog没有可审计的根目录叶节点。");
                HashSet<uint> seen = new HashSet<uint>(); uint node = firstLeaf, previousNode = 0; ulong records = 0, files = 0, folders = 0;
                while (node != 0)
                {
                    Require(node < totalNodes && seen.Count < NodeLimit && seen.Add(node), "HFS catalog叶链循环、越界或超过审计节点上限。");
                    byte[] leaf = Catalog(checked((long)node * nodeSize), nodeSize);
                    Require(leaf[8] == 255 && leaf[9] == 1 && U32(leaf, 4) == previousNode, "HFS catalog叶节点类型、高度或反向链不一致。");
                    int count = U16(leaf, 10);
                    Require(count > 0 && 14 + (count + 1) * 2 <= nodeSize, "HFS catalog叶记录数量无效。");
                    int endLimit = nodeSize - (count + 1) * 2;
                    for (int i = 0; i < count; i++)
                    {
                        int offset = U16(leaf, nodeSize - 2 - i * 2), next = U16(leaf, nodeSize - 4 - i * 2);
                        Require(offset >= 14 && offset < next && next <= endLimit && next - offset >= 10, "HFS catalog记录边界无效。");
                        int keyLength = U16(leaf, offset), chars = U16(leaf, offset + 6);
                        Require(chars <= 255 && keyLength == 6 + chars * 2 && offset + 2 + keyLength + 2 <= next, "HFS catalog名称或key边界无效。");
                        int record = offset + 2 + keyLength, type = U16(leaf, record);
                        records++;
                        if (type == 3 || type == 4) continue;
                        Require(type == 1 || type == 2, "HFS catalog含未知记录类型。");
                        Require(next - record >= (type == 1 ? 88 : 248), "HFS catalog文件/文件夹记录被截断。");
                        Require(entries.Count < EntryLimit && nameChars <= NameLimit - chars, "HFS catalog超过条目或名称内存审计上限。");
                        nameChars += chars;
                        CatalogEntry entry = new CatalogEntry { Id = U32(leaf, record + 8), Parent = U32(leaf, offset + 2), Name = new UnicodeEncoding(true, false, true).GetString(leaf, offset + 8, chars * 2) };
                        Require(entry.Id >= 2 && !entries.ContainsKey(entry.Id), "HFS catalog含重复或无效CNID。");
                        entries.Add(entry.Id, entry);
                        if (type == 1) folders++; else files++;
                        if (type != 2) continue;
                        uint finderType = U32(leaf, record + 48), creator = U32(leaf, record + 52);
                        bool fileLink = finderType == 0x686C6E6B && creator == 0x6866732B;
                        bool dirLink = finderType == 0x66647270 && creator == 0x4D414353 && (U16(leaf, record + 2) & 0x20) != 0;
                        if (fileLink || dirLink)
                            links.Add(new HfsNativeLink { CNID = entry.Id, ParentCNID = entry.Parent, Name = entry.Name, LinkReference = U32(leaf, record + 44), Directory = dirLink });
                    }
                    uint forward = U32(leaf, 0);
                    Require(forward != 0 || node == lastLeaf, "HFS catalog最后叶节点与header不一致。");
                    previousNode = node; node = forward;
                }
                Require(records == leafRecords, "HFS catalog叶记录总数与header不一致，不能排除未扫描记录。");
                Require(files == U32(volume, 32) && folders == (ulong)U32(volume, 36) + 1 && entries.ContainsKey(2), "HFS catalog文件/文件夹数量或根CNID与卷头不一致。");
                audited = true;
            }
            internal HfsGuardResult Result()
            {
                foreach (HfsNativeLink link in links)
                {
                    List<string> components = new List<string>(); HashSet<uint> ancestors = new HashSet<uint>(); uint id = link.CNID;
                    while (id != 1)
                    {
                        CatalogEntry entry;
                        if (ancestors.Count >= 1024 || !ancestors.Add(id) || !entries.TryGetValue(id, out entry)) { components.Clear(); break; }
                        components.Add(entry.Name); id = entry.Parent;
                    }
                    components.Reverse(); link.Components = components.AsReadOnly();
                    link.Path = components.Count == 0 ? "[CNID=" + link.CNID + "]" : String.Join("/", components.ToArray());
                }
                string note = audited ? links.Count == 0 ? "已完成有界catalog叶链审计，未发现原生硬链接占位。" : "本卷存在 " + links.Count + " 个原生硬链接占位，未确认完整；未跟随或重建其目标。" : "未能完成HFS原生硬链接审计：" + (Failure ?? "扫描不完整。") + (links.Count == 0 ? "" : " 已发现 " + links.Count + " 个占位。") ;
                return new HfsGuardResult { Complete = audited && links.Count == 0, Audited = audited, Links = links.Count, Note = note, Entries = links.AsReadOnly() };
            }
        }
        private static ushort U16(byte[] b, int p) { return (ushort)((b[p] << 8) | b[p + 1]); }
        private static uint U32(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
        private static ulong U64(byte[] b, int p) { return ((ulong)U32(b, p) << 32) | U32(b, p + 4); }
    }
}
