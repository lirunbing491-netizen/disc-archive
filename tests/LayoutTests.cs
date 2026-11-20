using System;
using System.IO;
using System.Text;
using System.Threading;
using DiscArchive;

internal static class LayoutTests
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
    private static void Throws<T>(Action a, string message) where T : Exception
    { try { a(); } catch (T) { Check(true, message); return; } throw new Exception("FAIL: " + message); }
    private static void B16(byte[] b, int p, uint n) { b[p] = (byte)(n >> 8); b[p + 1] = (byte)n; }
    private static void B32(byte[] b, int p, uint n) { B16(b, p, n >> 16); B16(b, p + 2, n); }
    private static void L16(byte[] b, int p, uint n) { b[p] = (byte)n; b[p + 1] = (byte)(n >> 8); }
    private static void L32(byte[] b, int p, uint n) { L16(b, p, n); L16(b, p + 2, n >> 16); }
    private static void L64(byte[] b, int p, ulong n) { L32(b, p, (uint)n); L32(b, p + 4, (uint)(n >> 32)); }
    private static void Ascii(byte[] b, int p, string s) { byte[] x = Encoding.ASCII.GetBytes(s); Buffer.BlockCopy(x, 0, b, p, x.Length); }
    private static DiscLayout Inspect(byte[] b) { using (MemoryStream m = new MemoryStream(b, false)) return OpticalLayout.Inspect(m); }
    private static void Hfs(byte[] b, int start, int length, bool x)
    { int p = start + 1024; B16(b, p, x ? 0x4858u : 0x482Bu); B16(b, p + 2, x ? 5u : 4u); B32(b, p + 40, 512); B32(b, p + 44, (uint)(length / 512)); }
    private static byte[] Apm(int unit, bool second)
    {
        byte[] b = new byte[1024 * 1024];
        B16(b, 0, 0x4552); B16(b, 2, (uint)unit);
        for (int i = 0; i < (second ? 2 : 1); i++)
        {
            int p = unit * (i + 1), start = i == 0 ? 36 : 128;
            B16(b, p, 0x504D); B32(b, p + 4, second ? 2u : 1u); B32(b, p + 8, (uint)start); B32(b, p + 12, 64);
            Ascii(b, p + 16, "Mac partition " + i); Ascii(b, p + 48, "Apple_HFS");
            Hfs(b, start * unit, 64 * unit, i == 1);
        }
        return b;
    }
    private static void Iso(byte[] b, bool joliet)
    {
        int p = 16 * 2048;
        b[p] = 1; Ascii(b, p + 1, "CD001"); b[p + 6] = 1; Ascii(b, p + 40, "ISO SAMPLE");
        L32(b, p + 80, (uint)(b.Length / 2048)); B32(b, p + 84, (uint)(b.Length / 2048));
        L16(b, p + 128, 2048); B16(b, p + 130, 2048);
        b[p + 156] = 34; L32(b, p + 158, 30); B32(b, p + 162, 30); L32(b, p + 166, 2048); B32(b, p + 170, 2048); b[p + 181] = 2;
        int end = 17;
        if (joliet)
        {
            Buffer.BlockCopy(b, p, b, 17 * 2048, 2048); int j = 17 * 2048;
            b[j] = 2; b[j + 88] = 0x25; b[j + 89] = 0x2F; b[j + 90] = 0x45;
            Array.Clear(b, j + 40, 32); byte[] name = Encoding.BigEndianUnicode.GetBytes("JOLIET"); Buffer.BlockCopy(name, 0, b, j + 40, name.Length); end++;
        }
        b[end * 2048] = 255; Ascii(b, end * 2048 + 1, "CD001"); b[end * 2048 + 6] = 1;
        b[30 * 2048] = 34; b[30 * 2048 + 25] = 2; b[30 * 2048 + 32] = 1;
    }
    private static void Tag(byte[] b, int p, uint type, uint location, int body)
    {
        L16(b, p, type); L16(b, p + 2, 3); L16(b, p + 10, (uint)body); L32(b, p + 12, location);
        ushort crc = 0; for (int i = p + 16; i < p + 16 + body; i++) { crc ^= (ushort)(b[i] << 8); for (int j = 0; j < 8; j++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0)); }
        L16(b, p + 8, crc); int sum = 0; for (int i = 0; i < 16; i++) if (i != 4) sum += b[p + i]; b[p + 4] = (byte)sum;
    }
    private static void Udf(byte[] b)
    {
        string[] ids = { "BEA01", "NSR03", "TEA01" };
        for (int i = 0; i < ids.Length; i++) { int p = (20 + i) * 2048; Ascii(b, p + 1, ids[i]); b[p + 6] = 1; }
        int a = 256 * 2048; L32(b, a + 16, 3 * 2048); L32(b, a + 20, 257); Tag(b, a, 2, 256, 496);
        int pd = 257 * 2048; L16(b, pd + 22, 3); L32(b, pd + 188, 300); L32(b, pd + 192, 500); Tag(b, pd, 5, 257, 496);
        int lv = 258 * 2048; L32(b, lv + 212, 2048); L32(b, lv + 248, 2048); L32(b, lv + 264, 6); L32(b, lv + 268, 1); b[lv + 440] = 1; b[lv + 441] = 6; L16(b, lv + 444, 3); Tag(b, lv, 6, 258, 430);
        Tag(b, 259 * 2048, 8, 259, 496);
        int fs = 300 * 2048; L32(b, fs + 400, 2048); L32(b, fs + 404, 1); Tag(b, fs, 256, 0, 496);
        int root = 301 * 2048; b[root + 27] = 4; Tag(b, root, 261, 1, 160);
    }
    private static void UdfName(byte[] b, string name, bool wide)
    {
        int lv = 258 * 2048;
        Array.Clear(b, lv + 20, 64); Ascii(b, lv + 21, "OSTA Compressed Unicode");
        Array.Clear(b, lv + 84, 128); b[lv + 84] = wide ? (byte)16 : (byte)8;
        byte[] text = wide ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.GetEncoding(28591).GetBytes(name);
        Buffer.BlockCopy(text, 0, b, lv + 85, text.Length); b[lv + 211] = (byte)(text.Length + 1);
        Tag(b, lv, 6, 258, 430);
    }
    private static byte[] Gpt(bool apfs)
    {
        byte[] b = new byte[2 * 1024 * 1024]; int header = 512;
        Ascii(b, header, "EFI PART"); L32(b, header + 8, 0x10000); L32(b, header + 12, 92); L64(b, header + 24, 1);
        L64(b, header + 32, (ulong)(b.Length / 512 - 1)); L64(b, header + 40, 34); L64(b, header + 48, (ulong)(b.Length / 512 - 2)); L64(b, header + 72, 2); L32(b, header + 80, 4); L32(b, header + 84, 128);
        byte[] guid = new Guid(apfs ? "7C3457EF-0000-11AA-AA11-00306543ECAC" : "48465300-0000-11AA-AA11-00306543ECAC").ToByteArray(); Buffer.BlockCopy(guid, 0, b, 1024, 16);
        L64(b, 1024 + 32, 64); L64(b, 1024 + 40, apfs ? 1087UL : 319UL);
        if (apfs) Apfs(b, 64 * 512); else Hfs(b, 64 * 512, 256 * 512, false);
        byte[] entries = new byte[512]; Buffer.BlockCopy(b, 1024, entries, 0, entries.Length); L32(b, header + 88, OpticalLayout.Crc32(entries, entries.Length));
        byte[] h = new byte[92]; Buffer.BlockCopy(b, header, h, 0, h.Length); L32(b, header + 16, OpticalLayout.Crc32(h, h.Length));
        return b;
    }
    private static void ApfsChecksum(byte[] b, int p)
    {
        ulong lo = 0, hi = 0, mod = 0xFFFFFFFFUL;
        for (int i = 8; i < 4096; i += 4) { lo = (lo + BitConverter.ToUInt32(b, p + i)) % mod; hi = (hi + lo) % mod; }
        ulong x = mod - (lo + hi) % mod, y = mod - (lo + x) % mod; L64(b, p, y << 32 | x);
    }
    private static void Apfs(byte[] b, int p)
    {
        Ascii(b, p + 32, "NXSB"); L32(b, p + 24, 1); L64(b, p + 16, 10); L32(b, p + 36, 4096); L64(b, p + 40, 128); L64(b, p + 160, 1); L32(b, p + 180, 1); L64(b, p + 184, 100);
        int map = p + 4096; L32(b, map + 24, 11); L64(b, map + 48, 2);
        int tree = p + 8192; L32(b, tree + 24, 2); L16(b, tree + 32, 7); L32(b, tree + 36, 1); L16(b, tree + 42, 4); L16(b, tree + 58, 16); L64(b, tree + 60, 100); L64(b, tree + 68, 10);
        int value = tree + 4096 - 40 - 16; L32(b, value + 4, 4096); L64(b, value + 8, 3);
        int volume = p + 12288; Ascii(b, volume + 32, "APSB"); L32(b, volume + 24, 13); L64(b, volume + 8, 100); L64(b, volume + 136, 500);
        ApfsChecksum(b, p); ApfsChecksum(b, map); ApfsChecksum(b, tree); ApfsChecksum(b, volume);
    }
    private sealed class FaultStream : MemoryStream
    {
        private readonly long bad;
        internal FaultStream(byte[] b, long bad) : base(b, false) { this.bad = bad; }
        public override int Read(byte[] b, int p, int n) { if (Position <= bad && Position + n > bad) throw new IOException("injected sector failure"); return base.Read(b, p, n); }
    }
    private sealed class YieldStream : MemoryStream
    {
        internal YieldStream(byte[] b) : base(b, false) { }
        public override long Position { get { return base.Position; } set { base.Position = value; Thread.Sleep(0); } }
    }
    public static int Main()
    {
        try
        {
            DiscLayout d = Inspect(new byte[1024 * 1024]); Check(d.Volumes.Count == 0 && !d.Note.Contains("读取失败"), "ordinary unrecognized data distinguished");
            byte[] b = Apm(512, true); d = Inspect(b); Check(d.HasPartitionMap && d.Volumes.Count == 2 && d.Volumes[0].Offset == 36 * 512 && d.Volumes[1].Format == "HFSX", "APM 512 start36 and multiple volumes");
            b = Apm(2048, false); d = Inspect(b); Check(d.Volumes.Count == 1 && d.Volumes[0].Offset == 36 * 2048, "APM 2048 units");
            b = Apm(512, false); Array.Clear(b, 36 * 512 + 1024, 512); d = Inspect(b); Check(d.HasPartitionMap && d.Volumes.Count == 0 && !String.IsNullOrEmpty(d.IncompleteApple), "known Apple partition without filesystem records incomplete evidence");
            b = Apm(512, true); using (FaultStream f = new FaultStream(b, 36 * 512 + 1024)) { d = OpticalLayout.Inspect(f); Check(!String.IsNullOrEmpty(d.IncompleteApple) && d.Volumes.Exists(delegate(DiscVolume v) { return v.Format == "HFSX"; }), "known Apple partition read failure preserves healthy sibling and marks incomplete"); }
            b = Apm(512, false); B32(b, 512 + 12, UInt32.MaxValue); Check(!String.IsNullOrEmpty(Inspect(b).IncompleteApple), "declared Apple partition with invalid bounds records incomplete evidence");
            b = new byte[128 * 1024]; Hfs(b, 0, b.Length, false); Check(Inspect(b).Volumes[0].Format == "HFS", "bare HFS Plus");
            B16(b, 1026, 5); Check(Inspect(b).Volumes.Count == 0, "HFS signature version mismatch rejected");
            Hfs(b, 0, b.Length, true); B32(b, 1024 + 44, UInt32.MaxValue); Check(Inspect(b).Volumes.Count == 0, "HFS overflowing declared capacity rejected");
            b = new byte[256 * 1024]; int mdb = 1024; B16(b, mdb, 0x4244); B16(b, mdb + 18, 32); B32(b, mdb + 20, 4096); B16(b, mdb + 28, 4); B16(b, mdb + 124, 0x482B); B16(b, mdb + 126, 2); B16(b, mdb + 128, 16); int embedded = 4 * 512 + 2 * 4096; Hfs(b, embedded, 16 * 4096, false);
            d = Inspect(b); Check(d.Volumes.Count == 1 && d.Volumes[0].Offset == embedded && d.Volumes[0].Length == 65536, "TN1150 wrapper uses 512 base plus allocation extent");
            B16(b, mdb + 124, 0); d = Inspect(b); Check(d.Volumes.Count == 1 && d.Volumes[0].Format == "ClassicHFS" && d.Note.Contains("仅识别"), "classic HFS explicitly identification only");
            b = Gpt(false); d = Inspect(b); Check(d.HasPartitionMap && d.Volumes.Count == 1 && d.Volumes[0].Offset == 32768, "GPT HFS checked header and table CRC");
            b[512 + 16] ^= 1; d = Inspect(b); Check(d.Volumes.Count == 0 && d.Note.Contains("校验失败"), "GPT corrupt CRC rejected");
            b = Gpt(true); d = Inspect(b); Check(d.Volumes.Count == 1 && d.Volumes[0].Format == "APFS", "GPT APFS with OMAP resolved APSB");
            Array.Clear(b, 32768 + 184, 8); ApfsChecksum(b, 32768); d = Inspect(b); Check(d.Volumes.Count == 0 && d.Note.Contains("未确认有效卷"), "APFS container alone rejected");
            b = new byte[1024 * 1024]; b[510] = 0x55; b[511] = 0xAA; b[450] = 0xAF; L32(b, 454, 64); L32(b, 458, 128); Hfs(b, 32768, 65536, false); d = Inspect(b); Check(d.Volumes.Count == 1 && d.Volumes[0].Format == "HFS", "MBR AF with confirmed HFS");
            b = new byte[2 * 1024 * 1024]; Iso(b, true); d = Inspect(b); Check(d.HasIso && d.Volumes.Count == 1 && d.Volumes[0].Name == "JOLIET", "ISO Joliet with root directory");
            b[30 * 2048] = 0; Check(!Inspect(b).HasIso, "ISO magic without root rejected");
            b = new byte[2 * 1024 * 1024]; Udf(b); d = Inspect(b); Check(d.HasUdf && d.Volumes.Count == 1 && d.Volumes[0].Name == "", "UDF structure confirmed without inventing a volume label");
            UdfName(b, "Café archive", false); d = Inspect(b); Check(d.HasUdf && d.Volumes[0].Name == "Café archive", "UDF CS0 8-bit label decodes bytes as Unicode code points");
            UdfName(b, "摄影中文卷", true); d = Inspect(b); Check(d.HasUdf && d.Volumes[0].Name == "摄影中文卷", "UDF CS0 16-bit logical volume label");
            b[258 * 2048 + 211] = 4; Tag(b, 258 * 2048, 6, 258, 430); d = Inspect(b); Check(d.HasUdf && d.Volumes[0].Name == "", "malformed CS0 label length stays empty without hiding valid filesystem");
            UdfName(b, "valid", false); b[258 * 2048 + 21] = (byte)'X'; Tag(b, 258 * 2048, 6, 258, 430); d = Inspect(b); Check(d.HasUdf && d.Volumes[0].Name == "", "unrecognized character set does not produce a project label");
            UdfName(b, "bridge volume", false);
            Iso(b, false); d = Inspect(b); Check(d.HasIso && d.HasUdf && d.Note.Contains("bridge"), "ISO UDF bridge distinguished");
            b[256 * 2048 + 4] ^= 1; Check(!Inspect(b).HasUdf, "UDF invalid anchor checksum rejected");
            b = Apm(512, false); Iso(b, false); d = Inspect(b); Check(d.HasIso && d.Volumes.Count == 2 && d.Note.Contains("hybrid"), "ISO HFS hybrid retained");
            using (FaultStream f = new FaultStream(b, 16 * 2048)) { d = OpticalLayout.Inspect(f); Check(d.Volumes.Exists(delegate(DiscVolume v) { return v.Format == "HFS"; }) && d.Note.Contains("探测读取失败"), "bad optical probe preserves readable HFS and reports failure"); }
            using (FaultStream f = new FaultStream(new byte[1024 * 1024], 2048)) { d = OpticalLayout.Inspect(f); Check(String.IsNullOrEmpty(d.IncompleteApple), "failed alternative partition probe does not invent missing Apple data"); }
            using (MemoryStream s = new MemoryStream(b, false)) { s.Position = 123; OpticalLayout.Inspect(s); Check(s.Position == 123, "inspection restores source position"); }
            byte[] values = new byte[10000]; for (int i = 0; i < values.Length; i++) values[i] = (byte)(i % 251);
            using (YieldStream s = new YieldStream(values))
            {
                DiscSlice a = new DiscSlice(s, 111, 3000), c = new DiscSlice(s, 5000, 3000);
                Exception error = null;
                Thread left = new Thread(delegate() { try { for (int i = 0; i < 3000; i++) if (a.ReadByte() != values[111 + i]) throw new Exception("slice A race"); } catch (Exception ex) { error = ex; } });
                Thread right = new Thread(delegate() { try { for (int i = 0; i < 3000; i++) if (c.ReadByte() != values[5000 + i]) throw new Exception("slice B race"); } catch (Exception ex) { error = ex; } });
                left.Start(); right.Start(); left.Join(); right.Join(); if (error != null) throw error;
                Check(a.Position == 3000 && c.Position == 3000, "shared-source slices serialize concurrent position and reads");
                Check(a.ReadByte() == -1, "slice EOF"); a.Seek(-1, SeekOrigin.End); Check(a.ReadByte() == values[3110], "slice tail seek");
                Throws<IOException>(delegate { a.Seek(-1, SeekOrigin.Begin); }, "slice negative seek");
                Throws<IOException>(delegate { a.Seek(Int64.MaxValue, SeekOrigin.End); }, "slice seek overflow");
                Throws<NotSupportedException>(delegate { a.WriteByte(1); }, "slice writes rejected");
                Throws<ArgumentOutOfRangeException>(delegate { new DiscSlice(s, Int64.MaxValue, 10); }, "slice constructor boundary");
                a.Dispose(); c.Dispose(); Check(s.CanRead, "slices do not own source");
                Throws<ObjectDisposedException>(delegate { a.ReadByte(); }, "disposed slice rejected");
            }
            Console.WriteLine("Checks passed: " + checks); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
