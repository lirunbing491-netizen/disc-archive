using System;
using System.IO;
using System.Text;
using System.Linq;
using DiscArchive;
internal static class BackendTests {
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
    private static void BE(byte[] b, int p, ulong v, int n) { for (int i = n - 1; i >= 0; i--) { b[p + i] = (byte)v; v >>= 8; } }
    private static void LE(byte[] b, int p, uint v, int n) { for (int i = 0; i < n; i++) { b[p + i] = (byte)v; v >>= 8; } }
    private static void Both(byte[] b, int p, uint value, int n) { LE(b, p, value, n); BE(b, p + n, value, n); }
    private static int IsoRecord(byte[] b, int p, uint block, uint length, byte flags, byte[] name)
    {
        int size = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        b[p] = (byte)size; Both(b, p + 2, block, 4); Both(b, p + 10, length, 4);
        b[p + 18] = 126; b[p + 19] = 11; b[p + 20] = 20; b[p + 25] = flags; Both(b, p + 28, 1, 2);
        b[p + 32] = (byte)name.Length; Buffer.BlockCopy(name, 0, b, p + 33, name.Length); return size;
    }
    private static byte[] Iso()
    {
        byte[] b = new byte[24 * 2048]; int p = 16 * 2048;
        b[p] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(b, p + 1); b[p + 6] = 1;
        Encoding.ASCII.GetBytes("SYNTHETIC".PadRight(32)).CopyTo(b, p + 40);
        Both(b, p + 80, 24, 4); Both(b, p + 120, 1, 2); Both(b, p + 124, 1, 2); Both(b, p + 128, 2048, 2);
        IsoRecord(b, p + 156, 20, 2048, 2, new byte[] { 0 });
        p = 17 * 2048; b[p] = 255; Encoding.ASCII.GetBytes("CD001").CopyTo(b, p + 1); b[p + 6] = 1;
        p = 20 * 2048; p += IsoRecord(b, p, 20, 2048, 2, new byte[] { 0 }); p += IsoRecord(b, p, 20, 2048, 2, new byte[] { 1 });
        p += IsoRecord(b, p, 21, 5, 0, Encoding.ASCII.GetBytes("HELLO.TXT;1"));
        IsoRecord(b, p, 22, 0, 0, Encoding.ASCII.GetBytes("EMPTY;1"));
        Encoding.ASCII.GetBytes("hello").CopyTo(b, 21 * 2048); return b;
    }
    private static void Fork(byte[] b, int p, ulong size, uint block, uint blocks)
    { BE(b, p, size, 8); BE(b, p + 12, blocks, 4); BE(b, p + 16, block, 4); BE(b, p + 20, blocks, 4); }
    private static int HfsRecord(byte[] b, int p, uint parent, uint id, string name, bool dir, uint mode, uint block, bool resource)
    {
        int key = 8 + name.Length * 2;
        BE(b, p, (ulong)(key - 2), 2); BE(b, p + 2, parent, 4); BE(b, p + 6, (ulong)name.Length, 2);
        Encoding.BigEndianUnicode.GetBytes(name).CopyTo(b, p + 8);
        int r = p + key; BE(b, r, dir ? 1u : 2u, 2); BE(b, r + 8, id, 4); BE(b, r + 0x2A, mode, 2);
        if (!dir) { Fork(b, r + 0x58, 5, block, 1); if (resource) Fork(b, r + 0xA8, 4, block + 1, 1); }
        return key + 0x58 + (dir ? 0 : 160);
    }
    private static byte[] Hfs(bool caseSensitive, string fileName = "café-文.txt")
    {
        byte[] b = new byte[32 * 512]; int h = 1024;
        b[h] = (byte)'H'; b[h + 1] = (byte)(caseSensitive ? 'X' : '+'); BE(b, h + 2, caseSensitive ? 5u : 4u, 2);
        BE(b, h + 0x20, 2, 4); BE(b, h + 0x28, 512, 4); BE(b, h + 0x2C, 32, 4);
        Fork(b, h + 0x110, 8192, 4, 16);
        int tree = 4 * 512, leaf = tree + 4096;
        BE(b, tree + 14 + 0xA, 1, 4); BE(b, tree + 14 + 0x12, 4096, 2); BE(b, tree + 14 + 0x16, 2, 4);
        b[leaf + 8] = 255; b[leaf + 9] = 1; BE(b, leaf + 10, 3, 2);
        int p = leaf + 14;
        BE(b, leaf + 4094, (ulong)(p - leaf), 2); p += HfsRecord(b, p, 1, 2, "测试卷", true, 0x41ED, 0, false);
        BE(b, leaf + 4092, (ulong)(p - leaf), 2); p += HfsRecord(b, p, 2, 16, fileName, false, 0x81A4, 22, true);
        BE(b, leaf + 4090, (ulong)(p - leaf), 2); p += HfsRecord(b, p, 2, 17, "link", false, 0xA1FF, 24, false);
        BE(b, leaf + 4088, (ulong)(p - leaf), 2);
        Encoding.ASCII.GetBytes("hello").CopyTo(b, 22 * 512); Encoding.ASCII.GetBytes("RSRC").CopyTo(b, 23 * 512); Encoding.ASCII.GetBytes("other").CopyTo(b, 24 * 512);
        return b;
    }
    private static byte[] CompressedHfs(bool unsupported, bool corrupt)
    {
        byte[] original = Hfs(false), b = new byte[64 * 512]; original.CopyTo(b, 0);
        BE(b, 1024 + 0x2C, 64, 4);
        int fileRecord = 4 * 512 + 4096 + 14 + (8 + "测试卷".Length * 2 + 0x58) + 8 + "café-文.txt".Length * 2;
        Array.Clear(b, fileRecord + 0x58, 160);
        Fork(b, 1024 + 0x160, 1024, 32, 2);
        int tree = 32 * 512, leaf = tree + 512;
        BE(b, tree + 14 + 0xA, 1, 4); BE(b, tree + 14 + 0x12, 512, 2); BE(b, tree + 14 + 0x16, 2, 4);
        b[leaf + 8] = 255; b[leaf + 9] = 1; BE(b, leaf + 10, 1, 2);
        string name = "com.apple.decmpfs"; int p = leaf + 14;
        BE(b, p, (ulong)(12 + name.Length * 2), 2); BE(b, p + 4, 16, 4); BE(b, p + 12, (ulong)name.Length, 2);
        Encoding.BigEndianUnicode.GetBytes(name).CopyTo(b, p + 14);
        int r = p + 14 + name.Length * 2;
        byte[] zlib = new byte[] { 0x78, 0x9c, 0xcb, 0x48, 0xcd, 0xc9, 0xc9, 0x07, 0x00, 0x06, 0x2c, 0x02, 0x15 };
        if (corrupt) zlib[4] ^= 0x80;
        BE(b, r, 0x10, 4); BE(b, r + 12, (ulong)(16 + zlib.Length), 4);
        r += 16; LE(b, r, 0x636d7066, 4); LE(b, r + 4, unsupported ? 11u : 3u, 4); LE(b, r + 8, 5, 4); zlib.CopyTo(b, r + 16);
        BE(b, leaf + 510, 14, 2); BE(b, leaf + 508, (ulong)(r + 16 + zlib.Length - leaf), 2);
        return b;
    }
    private static string Read(Stream s) { using(s) using(var r = new StreamReader(s,Encoding.UTF8)) return r.ReadToEnd(); }
    private static byte[] DuplicateHfs(string secondName) {
        byte[] b=Hfs(true); int leaf=4*512+4096;
        int p=(b[leaf+4090]<<8)|b[leaf+4091];
        p+=HfsRecord(b,leaf+p,2,17,secondName,false,0x81A4,24,false);
        BE(b,leaf+4088,(ulong)p,2); return b;
    }
    private static void ExpectFailure(Action action, string name) { try { action(); } catch(Exception error) when(error is IOException || error is InvalidDataException) { Check(true,name); return; } throw new Exception("Expected failure: " + name); }
    private static void Verify(byte[] image, string format, string name) {
        using var source = new MemoryStream(image,false); using var reader = SevenZipReader.Open(source,format);
        var data = reader.Items.Single(i=>!i.IsDirectory && !i.IsAltStream && !i.IsLink && i.Path.EndsWith(name,StringComparison.Ordinal));
        Check(Read(reader.OpenItem(data.Index)) == "hello", format + " contents");
        using var a = reader.OpenItem(data.Index); using var b = reader.OpenItem(data.Index);
        a.Seek(3,SeekOrigin.Begin); Check(a.ReadByte()=='l', format + " seek forward");
        Check(b.ReadByte()=='h',format + " independent source cursors");
        a.Seek(0,SeekOrigin.Begin); Check(a.ReadByte()=='h',format + " seek backward after interleave");
        Check(!a.CanWrite && a.CanSeek,format + " seekable and read-only");
        if (format.StartsWith("HFS",StringComparison.Ordinal)) {
            var resource = reader.Items.Single(i=>i.IsAltStream);
            Check(Read(reader.OpenItem(resource.Index))=="RSRC",format + " resource fork bytes");
            Check(resource.ParentIndex==data.Index && resource.ParentIsAltStream,format + " raw fork parent");
            var link=reader.Items.Single(i=>i.IsLink); ExpectFailure(()=>reader.OpenItem(link.Index),format + " no link following");
        }
    }
    private sealed class FaultStream(byte[] image) : MemoryStream(image,false) {
        internal bool Fail;
        public override int Read(byte[] b,int o,int n) { if(Fail) throw new IOException("injected source fault"); return base.Read(b,o,n); }
    }
    private sealed class CountingStream(byte[] image) : MemoryStream(image,false) {
        internal long Bytes;
        public override int Read(byte[] b,int o,int n) { int done=base.Read(b,o,n); Bytes+=done; return done; }
    }
    public static int Main(string[] args) {
        try {
            Verify(Iso(),"ISO","HELLO.TXT");
            byte[] large=new byte[2*1024*1024]; Iso().CopyTo(large,0);
            Both(large,16*2048+80,1024,4); Both(large,20*2048+68+10,1024*1024,4);
            using(var source=new CountingStream(large)) using(var reader=SevenZipReader.Open(source,"ISO")) {
                var data=reader.Items.Single(i=>i.Size==1024*1024); source.Bytes=0;
                using var stream=reader.OpenItem(data.Index); Check(source.Bytes<131072,"opening ordinary item does not decode its whole body");
                Check(stream.ReadByte()=='h' && source.Bytes<131072,"head sampling reads only needed prefix");
                stream.Seek(1024*1024-1,SeekOrigin.Begin); Check(stream.ReadByte()==0 && source.Bytes<262144,"tail seeking does not read intervening bytes");
            }
            Verify(Hfs(false),"HFS+","café-文.txt"); Verify(Hfs(true),"HFSX","café-文.txt");
            Verify(Hfs(true,"literal\\slash:colon.txt"),"HFSX","literal\\slash:colon.txt");
            foreach(string secondName in new[]{"café-文.txt","CAFÉ-文.txt"}) {
                using var source=new MemoryStream(DuplicateHfs(secondName),false); using var reader=SevenZipReader.Open(source,"HFSX");
                var data=reader.Items.Where(i=>!i.IsDirectory && !i.IsAltStream && !i.IsLink).ToArray();
                Check(data.Length==2 && data[0].Index!=data[1].Index,"duplicate/case-distinct names retain independent indices");
                Check(Read(reader.OpenItem(data[0].Index))=="hello" && Read(reader.OpenItem(data[1].Index))=="other","duplicate/case-distinct names read by index");
            }
            using(var source = new MemoryStream(CompressedHfs(false,false),false)) using(var reader=SevenZipReader.Open(source,"HFS")) {
                var data=reader.Items.Single(i=>i.Method.Length>0 && !i.IsDirectory && !i.IsAltStream);
                Check(Read(reader.OpenItem(data.Index))=="hello","decoded compressed HFS bytes");
            }
            foreach(bool unsupported in new[]{false,true}) {
                using var source=new MemoryStream(CompressedHfs(unsupported,!unsupported),false); using var reader=SevenZipReader.Open(source,"HFS");
                var data=reader.Items.Single(i=>!i.IsDirectory && !i.IsAltStream && !i.IsLink && i.Name=="café-文.txt");
                ExpectFailure(()=>Read(reader.OpenItem(data.Index)),"corrupt/unsupported compressed file is explicit failure");
            }
            using(var source = new FaultStream(Iso())) using(var reader=SevenZipReader.Open(source,"ISO")) {
                var data=reader.Items.Single(i=>i.Size==5); using var stream=reader.OpenItem(data.Index); source.Fail=true;
                ExpectFailure(()=>stream.ReadByte(),"source fault reaches caller");
            }
            ExpectFailure(()=>SevenZipReader.Open(new MemoryStream(new byte[8192]),"HFS"),"invalid filesystem rejected");
            // External fixtures are authored from test strings by run-tests.sh using hdiutil.
            for(int i=0;i<args.Length;i+=2) {
                using var source=File.OpenRead(args[i+1]); using var reader=SevenZipReader.Open(source,args[i]);
                var data=reader.Items.Single(i=>!i.IsDirectory && !i.IsAltStream && i.Path.EndsWith("café-文.txt",StringComparison.Ordinal));
                Check(Read(reader.OpenItem(data.Index))=="hello",args[i] + " generated Unicode contents");
            }
            Console.WriteLine($"PASS: {checks} checks"); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
