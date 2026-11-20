using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using DiscArchive;

internal static class ArchiveReaderTests
{
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
    [DllImport("shlwapi.dll")]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IStream SHCreateMemStream(byte[] bytes, uint size);
    private static object Member(object instance, string name, BindingFlags flags, params object[] args)
    { return instance.GetType().InvokeMember(name, flags, null, instance, args); }
    private static byte[] Udf()
    {
        // Windows' built-in IMAPI authors an isolated in-memory UDF fixture.
        // No optical device/recorder object is created or opened.
        object image = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage", true));
        object root = null, result = null; IStream content = null, native = null;
        try
        {
            Member(image, "ChooseImageDefaultsForMediaType", BindingFlags.InvokeMethod, 12);
            Member(image, "FileSystemsToCreate", BindingFlags.SetProperty, 4);
            Member(image, "UDFRevision", BindingFlags.SetProperty, 0x102);
            Member(image, "VolumeName", BindingFlags.SetProperty, "SYNTHETIC_UDF");
            root = Member(image, "Root", BindingFlags.GetProperty);
            content = SHCreateMemStream(Encoding.ASCII.GetBytes("hello"), 5);
            Member(root, "AddFile", BindingFlags.InvokeMethod, "HELLO.TXT", content);
            result = Member(image, "CreateResultImage", BindingFlags.InvokeMethod);
            native = (IStream)Member(result, "ImageStream", BindingFlags.GetProperty);
            System.Runtime.InteropServices.ComTypes.STATSTG stat; native.Stat(out stat, 1);
            if (stat.cbSize <= 0 || stat.cbSize > 32 * 1024 * 1024) throw new Exception("Unexpected synthetic UDF size.");
            byte[] bytes = new byte[(int)stat.cbSize]; IntPtr read = Marshal.AllocHGlobal(4);
            try
            {
                native.Seek(0, 0, IntPtr.Zero); int done = 0;
                while (done < bytes.Length)
                {
                    byte[] chunk = new byte[Math.Min(128 * 1024, bytes.Length - done)];
                    Marshal.WriteInt32(read, 0); native.Read(chunk, chunk.Length, read); int n = Marshal.ReadInt32(read);
                    if (n <= 0) throw new Exception("Synthetic UDF fixture truncated.");
                    Buffer.BlockCopy(chunk, 0, bytes, done, n); done += n;
                }
            }
            finally { Marshal.FreeHGlobal(read); }
            return bytes;
        }
        finally
        {
            if (native != null) Marshal.ReleaseComObject(native);
            if (result != null) Marshal.ReleaseComObject(result);
            if (content != null) Marshal.ReleaseComObject(content);
            if (root != null) Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(image);
        }
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
    private static string Read(Stream stream) { using (stream) using (StreamReader r = new StreamReader(stream, Encoding.UTF8)) return r.ReadToEnd(); }
    private static ArchiveItem Find(SevenZipReader r, string suffix) { foreach (ArchiveItem item in r.Items) if (item.Path.EndsWith(suffix, StringComparison.Ordinal)) return item; throw new Exception("Missing " + suffix); }
    private static void SeekChecks(SevenZipReader reader, ArchiveItem item, string format)
    {
        using (Stream a = reader.OpenItem(item.Index)) using (Stream b = reader.OpenItem(item.Index))
        {
            Check(a.CanSeek && b.CanSeek && !a.CanWrite && a.Position == 0, format + " seekable read only streams");
            FieldInfo nativeSeek = a.GetType().GetField("seekable", BindingFlags.NonPublic | BindingFlags.Instance);
            if (nativeSeek != null) Check(nativeSeek.GetValue(a) != null, format + " official IInStream available without cache");
            Check(a.Seek(0, SeekOrigin.Begin) == 0 && a.Position == 0, format + " seek zero");
            a.Position = 2; Check(a.ReadByte() == 'l', format + " seek middle");
            b.Position = 1; Check(b.ReadByte() == 'e', format + " interleaved seek second stream");
            Check(a.Seek(-1, SeekOrigin.End) == 4 && a.ReadByte() == 'o' && a.ReadByte() == -1, format + " seek tail and EOF");
            Check(b.Seek(1, SeekOrigin.Current) == 3 && b.ReadByte() == 'l', format + " seek current after other stream");
            a.Position = 0; Check(a.ReadByte() == 'h' && b.ReadByte() == 'o', format + " rewind with interleaved read");
            a.Position = a.Length + 10; Check(a.ReadByte() == -1, format + " seek beyond end");
            a.Position = 0; Check(a.ReadByte() == 'h', format + " rewind after beyond end");
            bool rejected = false; try { a.Seek(-1, SeekOrigin.Begin); } catch (IOException) { rejected = true; }
            Check(rejected, format + " negative seek is IOException");
        }
        Task<bool> one = Task.Factory.StartNew(() => SeekWorker(reader, item.Index));
        Task<bool> two = Task.Factory.StartNew(() => SeekWorker(reader, item.Index));
        Task.WaitAll(one, two); Check(one.Result && two.Result, format + " concurrent random seek and read");
    }
    private static bool SeekWorker(SevenZipReader reader, int index)
    {
        using (Stream stream = reader.OpenItem(index))
        {
            for (int i = 0; i < 20; i++) { int target = i % 5; stream.Position = target; if (stream.ReadByte() != "hello"[target]) return false; }
            return true;
        }
    }
    private sealed class SequentialOnly : SevenZipInterop.ISequentialIn
    {
        internal int Reads, Position;
        internal bool Fail;
        public int Read(IntPtr data, uint size, IntPtr processed)
        {
            Reads++;
            if (Fail) { Marshal.WriteInt32(processed, 0); return SevenZipInterop.Fail; }
            byte[] bytes = Encoding.ASCII.GetBytes("hello");
            int count = Math.Min(2, Math.Min((int)size, bytes.Length - Position));
            Marshal.Copy(bytes, Position, data, count); Position += count; Marshal.WriteInt32(processed, count); return 0;
        }
    }
    private static Stream SequentialWrapper(SevenZipReader reader, SequentialOnly source)
    {
        Type type = typeof(SevenZipReader).GetNestedType("ItemStream", BindingFlags.NonPublic);
        return (Stream)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { reader, source, 5L }, null);
    }
    private static void SequentialChecks(SevenZipReader reader)
    {
        SequentialOnly source = new SequentialOnly(); string cachePath;
        using (Stream stream = SequentialWrapper(reader, source))
        {
            stream.Position = 0; stream.Position = 4; stream.Position = 0;
            Check(source.Reads == 0, "sequential fallback seek alone does not consume source");
            FieldInfo cache = stream.GetType().GetField("cache", BindingFlags.NonPublic | BindingFlags.Instance);
            Check(cache.GetValue(stream) == null, "seek zero and forward alone create no cache");
            stream.Position = 4; Check(stream.ReadByte() == 'o', "sequential fallback reads only required prefix");
            Check(source.Position == 5, "sequential fallback stays within declared item size");
            stream.Position = 1; Check(stream.ReadByte() == 'e', "sequential fallback replays cached prefix");
            int before = source.Reads; stream.Position = 0; Check(stream.ReadByte() == 'h' && source.Reads == before, "cached rewind does not reread source");
            cachePath = ((FileStream)cache.GetValue(stream)).Name;
            Check(File.Exists(cachePath), "lazy sequential cache exists during item lifetime");
        }
        Check(!File.Exists(cachePath), "sequential cache deleted when item closes");
        SequentialOnly failing = new SequentialOnly { Fail = true };
        using (Stream stream = SequentialWrapper(reader, failing))
        {
            bool rejected = false; try { stream.Position = 4; stream.ReadByte(); } catch (IOException) { rejected = true; }
            Check(rejected, "sequential fallback source failure remains IOException");
        }
    }
    private sealed class FailingStream : MemoryStream
    {
        internal long FailAt = long.MaxValue;
        internal FailingStream(byte[] bytes) : base(bytes, false) { }
        public override int Read(byte[] b, int o, int n) { if (Position >= FailAt) throw new IOException("Injected source failure"); return base.Read(b, o, n); }
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Supply an absolute official 7z.dll path.");
            string dll = args[0];
            using (MemoryStream source = new MemoryStream(Iso(), false))
            {
                using (SevenZipReader reader = SevenZipReader.Open(source, "iso", dll))
                {
                    ArchiveItem hello = Find(reader, "HELLO.TXT");
                    SeekChecks(reader, hello, "ISO");
                    SequentialChecks(reader);
                    Check(Read(reader.OpenItem(hello.Index)) == "hello", "synthetic ISO file bytes");
                    Check(Read(reader.OpenItem(Find(reader, "EMPTY").Index)) == "", "real empty file");
                    Check(reader.Label == "SYNTHETIC", "ISO label");
                    using (Stream a = reader.OpenItem(hello.Index)) using (Stream b = reader.OpenItem(hello.Index))
                    {
                        Check(a.ReadByte() == 'h' && b.ReadByte() == 'h' && a.ReadByte() == 'e' && b.ReadByte() == 'e', "independent interleaved native streams");
                    }
                    Task<string> one = Task.Factory.StartNew(() => Read(reader.OpenItem(hello.Index)));
                    Task<string> two = Task.Factory.StartNew(() => Read(reader.OpenItem(hello.Index)));
                    Task.WaitAll(one, two); Check(one.Result == "hello" && two.Result == "hello", "serialized concurrent source access");
                }
                Check(source.CanRead, "source ownership stays with caller");
            }
            foreach (bool x in new bool[] { false, true }) using (MemoryStream source = new MemoryStream(Hfs(x), false)) using (SevenZipReader reader = SevenZipReader.Open(source, x ? "hfsx" : "hfs+", dll))
            {
                ArchiveItem data = Find(reader, "café-文.txt"), resource = Find(reader, ":rsrc"), link = Find(reader, "link");
                Check(reader.ErrorFlags == 0, "clean synthetic " + (x ? "HFSX" : "HFS+"));
                Check(data.Name == "café-文.txt" && data.Path.IndexOf("测试卷", StringComparison.Ordinal) >= 0, "Unicode HFS name and path");
                Check(Read(reader.OpenItem(data.Index)) == "hello", "HFS data fork");
                SeekChecks(reader, data, x ? "HFSX" : "HFS+");
                Check(resource.IsAltStream && resource.ParentIsAltStream && resource.ParentIndex == data.Index && resource.Name == "rsrc", "HFS resource fork metadata");
                Check(Read(reader.OpenItem(resource.Index)) == "RSRC", "HFS raw resource bytes");
                Check(link.IsLink, "POSIX symlink identified");
                bool rejected = false; try { reader.OpenItem(link.Index); } catch (NotSupportedException) { rejected = true; } Check(rejected, "symlink not followed");
                Check(!reader.HardLinkSupportKnown, "HFS hardlink limitation explicit");
            }
            using (MemoryStream source = new MemoryStream(Hfs(false, "literal\\slash:colon.txt"), false)) using (SevenZipReader reader = SevenZipReader.Open(source, "HFS", dll))
            {
                ArchiveItem file = null;
                foreach (ArchiveItem entry in reader.Items) if (entry.Name == "literal\\slash:colon.txt") file = entry;
                if (file == null) throw new Exception("Literal HFS filename not preserved.");
                Check(file.Name == "literal\\slash:colon.txt" && reader.Items[file.ParentIndex].Name == "测试卷", "literal backslash and colon preserved by Name and parent chain");
            }
            using (FailingStream source = new FailingStream(Iso())) using (SevenZipReader reader = SevenZipReader.Open(source, "ISO", dll))
            {
                source.FailAt = 21 * 2048;
                bool failed = false; try { Read(reader.OpenItem(Find(reader, "HELLO.TXT").Index)); } catch (IOException) { failed = true; }
                Check(failed, "source read failure never becomes empty success");
            }
            using (MemoryStream source = new MemoryStream(CompressedHfs(false, false), false)) using (SevenZipReader reader = SevenZipReader.Open(source, "HFS", dll))
            {
                ArchiveItem compressed = Find(reader, "café-文.txt");
                SeekChecks(reader, compressed, "HFS decmpfs");
                Check(reader.ErrorFlags == 0 && compressed.Method.Length != 0, "synthetic HFS decmpfs zlib recognized");
                using (Stream decoded = reader.OpenItem(compressed.Index))
                {
                    Check(!decoded.CanWrite, "decoded temporary item is read only");
                    Check(Read(decoded) == "hello", "Extract callback decodes zlib file with exact size");
                }
            }
            foreach (bool corrupt in new bool[] { false, true }) using (MemoryStream source = new MemoryStream(CompressedHfs(!corrupt, corrupt), false)) using (SevenZipReader reader = SevenZipReader.Open(source, "HFS", dll))
            {
                bool failed = false; try { Read(reader.OpenItem(Find(reader, "café-文.txt").Index)); } catch (IOException) { failed = true; } catch (InvalidDataException) { failed = true; }
                Check(failed, corrupt ? "corrupt compressed item rejected" : "unsupported compression rejected");
            }
            using (MemoryStream source = new MemoryStream(Udf(), false)) using (SevenZipReader reader = SevenZipReader.Open(source, "UDF", dll))
            {
                ArchiveItem hello = Find(reader, "HELLO.TXT");
                Check(Read(reader.OpenItem(hello.Index)) == "hello", "synthetic UDF data file");
                SeekChecks(reader, hello, "UDF");
            }
            bool invalid = false; try { using (SevenZipReader.Open(new MemoryStream(new byte[8192]), "HFS", dll)) { } } catch (IOException) { invalid = true; }
            Check(invalid, "invalid volume rejected");
            Console.WriteLine("PASS: " + checks + " checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
