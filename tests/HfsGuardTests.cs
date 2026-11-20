using System;
using System.IO;
using System.Text;
using DiscArchive;

internal static class HfsGuardTests
{
    private static int checks;
    private static void Check(bool result, string note) { if (!result) throw new Exception("FAIL: " + note); checks++; Console.WriteLine("PASS: " + note); }
    private static void BE(byte[] b, int p, ulong value, int n) { for (int i = n - 1; i >= 0; i--) { b[p + i] = (byte)value; value >>= 8; } }
    private static int Record(byte[] b, int p, uint parent, uint cnid, string name, bool folder, uint finderType, uint creator, ushort flags)
    {
        int key = 8 + name.Length * 2; BE(b, p, (ulong)(key - 2), 2); BE(b, p + 2, parent, 4); BE(b, p + 6, (ulong)name.Length, 2);
        Encoding.BigEndianUnicode.GetBytes(name).CopyTo(b, p + 8); int r = p + key;
        BE(b, r, folder ? 1u : 2u, 2); BE(b, r + 2, flags, 2); BE(b, r + 8, cnid, 4);
        BE(b, r + 44, 1234, 4); BE(b, r + 48, finderType, 4); BE(b, r + 52, creator, 4);
        return key + (folder ? 88 : 248);
    }
    private static byte[] Volume(uint finderType, uint creator, ushort flags, bool hfsx)
    {
        byte[] b = new byte[32 * 512]; int h = 1024;
        BE(b, h, hfsx ? 0x4858u : 0x482Bu, 2); BE(b, h + 2, hfsx ? 5u : 4u, 2);
        BE(b, h + 32, 1, 4); BE(b, h + 40, 512, 4); BE(b, h + 44, 32, 4);
        BE(b, h + 272, 8192, 8); BE(b, h + 284, 16, 4); BE(b, h + 288, 4, 4); BE(b, h + 292, 16, 4);
        int tree = 2048, leaf = tree + 4096;
        b[tree + 8] = 1; BE(b, tree + 10, 3, 2); BE(b, tree + 20, 2, 4); BE(b, tree + 24, 1, 4); BE(b, tree + 28, 1, 4);
        BE(b, tree + 32, 4096, 2); BE(b, tree + 36, 2, 4);
        b[leaf + 8] = 255; b[leaf + 9] = 1; BE(b, leaf + 10, 2, 2);
        int p = leaf + 14; BE(b, leaf + 4094, 14, 2);
        p += Record(b, p, 1, 2, "测试卷", true, 0, 0, 0); BE(b, leaf + 4092, (ulong)(p - leaf), 2);
        p += Record(b, p, 2, 16, "文件\\含:字", false, finderType, creator, flags); BE(b, leaf + 4090, (ulong)(p - leaf), 2);
        return b;
    }
    private static HfsGuardResult Inspect(byte[] bytes)
    {
        using (MemoryStream stream = new MemoryStream(bytes, false))
        {
            stream.Position = 17; HfsGuardResult result = HfsCatalogGuard.Inspect(stream);
            Check(stream.CanRead && stream.Position == 17, "read only audit preserves source ownership and cursor"); return result;
        }
    }
    public static int Main()
    {
        try
        {
            foreach (bool x in new bool[] { false, true })
            {
                HfsGuardResult ordinary = Inspect(Volume(0x54455854, 0, 0, x));
                Check(ordinary.Complete && ordinary.Audited && ordinary.Links == 0, "ordinary " + (x ? "HFSX" : "HFS+") + " not marked partial");
                HfsGuardResult file = Inspect(Volume(0x686C6E6B, 0x6866732B, 0, x));
                Check(file.Audited && !file.Complete && file.Links == 1, "pre-Leopard native file hardlink detected without chain flag");
                Check(file.Entries[0].CNID == 16 && file.Entries[0].ParentCNID == 2 && file.Entries[0].LinkReference == 1234 && !file.Entries[0].Directory, "hardlink original CNID and reference captured");
                Check(file.Entries[0].Components.Count == 2 && file.Entries[0].Components[0] == "测试卷" && file.Entries[0].Components[1] == "文件\\含:字", "raw name components and parent chain preserved");
                HfsGuardResult directory = Inspect(Volume(0x66647270, 0x4D414353, 0x20, x));
                Check(directory.Audited && !directory.Complete && directory.Links == 1 && directory.Entries[0].Directory, "directory hardlink signature and chain flag detected");
                HfsGuardResult alias = Inspect(Volume(0x66647270, 0x4D414353, 0, x));
                Check(alias.Complete && alias.Links == 0, "ordinary Finder folder alias is not guessed to be hardlink");
                HfsGuardResult otherCreator = Inspect(Volume(0x686C6E6B, 0x4D414353, 0, x));
                Check(otherCreator.Complete && otherCreator.Links == 0, "type without matching creator is not guessed to be hardlink");
            }
            byte[] overflow = Volume(0, 0, 0, false); BE(overflow, 1024 + 284, 17, 4);
            HfsGuardResult unknown = Inspect(overflow); Check(!unknown.Audited && !unknown.Complete && unknown.Note.IndexOf("overflow", StringComparison.Ordinal) >= 0, "overflow catalog explicitly unaudited");
            byte[] fragmented = Volume(0x686C6E6B, 0x6866732B, 0, false);
            Buffer.BlockCopy(fragmented, 6144, fragmented, 12288, 4096); Array.Clear(fragmented, 6144, 4096);
            BE(fragmented, 1024 + 292, 8, 4); BE(fragmented, 1024 + 296, 24, 4); BE(fragmented, 1024 + 300, 8, 4);
            HfsGuardResult fragmentedResult = Inspect(fragmented);
            Check(fragmentedResult.Audited && fragmentedResult.Links == 1, "catalog leaf mapped correctly across separate inline extents");
            byte[] cycle = Volume(0, 0, 0, false); BE(cycle, 6144, 1, 4);
            Check(!Inspect(cycle).Audited, "cyclic leaf chain rejected within bounds");
            byte[] badCount = Volume(0, 0, 0, false); BE(badCount, 2048 + 20, 3, 4);
            Check(!Inspect(badCount).Audited, "incomplete leaf record count is not a clean audit");
            byte[] badOffset = Volume(0, 0, 0, false); BE(badOffset, 6144 + 4094, 4095, 2);
            Check(!Inspect(badOffset).Audited, "out of range record offsets rejected");
            byte[] badSize = Volume(0, 0, 0, false); BE(badSize, 2048 + 32, 65535, 2);
            Check(!Inspect(badSize).Audited, "invalid node size rejected before node allocation");
            byte[] truncated = new byte[8192]; Buffer.BlockCopy(Volume(0, 0, 0, false), 0, truncated, 0, truncated.Length);
            Check(!Inspect(truncated).Audited, "truncated catalog slice not considered clean");
            Check(!Inspect(new byte[8192]).Complete, "non HFS input explicitly unaudited");
            Console.WriteLine("PASS: " + checks + " checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
