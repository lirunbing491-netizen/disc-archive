using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DiscArchive;

internal sealed class ArchiveItem
{
    internal int Index;
    internal string Path = "", Name = "", LinkTarget = "", Method = "", Error = "", Issue = "";
    internal long Size;
    internal bool IsDirectory, IsAltStream, IsLink, IsHardLink, IsEncrypted, IsAux, IsDeleted;
    internal uint PosixAttributes;
    internal int ParentIndex = -1;
    internal bool ParentIsAltStream;
    internal DateTime? Modified;
}

// Serializes every native operation, including callbacks and outstanding item streams.
// The caller owns the bounded partition stream and must keep it open.
internal sealed class SevenZipReader : IDisposable
{
    private readonly object gate = new();
    private readonly Stream source;
    private readonly Native.ReadFn read;
    private readonly Native.SeekFn seek;
    private readonly List<NativeItemStream> streams = new();
    private IntPtr context;
    private Exception? callbackError;
    private bool disposed;
    internal IList<ArchiveItem> Items { get; private set; } = Array.Empty<ArchiveItem>();
    internal string Label { get; private set; } = "";
    internal uint ErrorFlags { get; private set; }
    internal uint WarningFlags { get; private set; }
    internal string Warning { get; private set; } = "";
    internal string HandlerName { get; private set; } = "";
    internal bool HardLinkSupportKnown => false;

    private SevenZipReader(Stream stream) { source = stream; read = ReadSource; seek = SeekSource; }
    internal static SevenZipReader Open(Stream partition, string format)
    {
        if (partition == null || !partition.CanRead || !partition.CanSeek)
            throw new ArgumentException("A readable, seekable bounded partition stream is required.", nameof(partition));
        var r = new SevenZipReader(partition);
        r.HandlerName = (format ?? "").Trim().ToUpperInvariant() switch {
            "HFS" or "HFS+" or "HFSX" => "HFS", "ISO" or "ISO9660" or "ISO 9660" or "JOLIET" => "Iso",
            "UDF" => "Udf", "APFS" => "APFS", _ => throw new NotSupportedException("Unsupported filesystem: " + format)
        };
        try {
            Check(r, Native.da_open(Native.LibraryPath, r.HandlerName, IntPtr.Zero, r.read, r.seek, out r.context), "Opening " + r.HandlerName);
            r.ReadItems(); return r;
        } catch (Exception original) {
            try { r.Dispose(); } catch (Exception cleanup) { original.Data["ArchiveCleanupError"] = cleanup; }
            throw;
        }
    }
    private int ReadSource(IntPtr user, IntPtr data, uint size, out uint processed)
    {
        processed = 0;
        try {
            byte[] b = new byte[Math.Min(size, 131072u)]; int n = source.Read(b, 0, b.Length);
            if (n == 0 && b.Length != 0 && source.Position < source.Length) throw new EndOfStreamException("Partition ended before declared length.");
            Marshal.Copy(b, 0, data, n); processed = (uint)n; return 0;
        } catch (Exception ex) { callbackError = ex; return unchecked((int)0x80004005); }
    }
    private int SeekSource(IntPtr user, long offset, uint origin, out ulong position)
    {
        position = 0;
        try {
            if (origin > 2) throw new IOException("Invalid source seek origin.");
            long p = source.Seek(offset, (SeekOrigin)origin); if (p < 0) throw new IOException("Negative source position.");
            position = (ulong)p; return 0;
        } catch (Exception ex) { callbackError = ex; return unchecked((int)0x80004005); }
    }
    private static void Check(SevenZipReader owner, int hr, string action)
    {
        if (owner.callbackError is Exception ex) { owner.callbackError = null; throw new IOException("Reading source partition failed.", ex); }
        if (hr != 0) throw new IOException(action + " (7-Zip 0x" + hr.ToString("X8") + ").");
    }
    private object? Property(uint index, uint property, bool volume = false)
    {
        Check(this, Native.da_property(context, index, property, volume ? 1 : 0, out uint type, out ulong value, out IntPtr text), "Reading property " + property);
        return type switch { 0 => null, 8 => Marshal.PtrToStringUTF8(text) ?? "", 11 => value != 0,
            19 => (uint)value, 20 => unchecked((long)value), 21 => value, 64 => FileTime(value),
            _ => throw new InvalidDataException("Unsupported property type " + type) };
    }
    private static DateTime? FileTime(ulong value) { try { return DateTime.FromFileTimeUtc(checked((long)value)); } catch (ArgumentOutOfRangeException) { return null; } catch (OverflowException) { return null; } }
    private string Text(uint i, uint p, bool v = false) => Convert.ToString(Property(i, p, v)) ?? "";
    private bool Flag(uint i, uint p) => Convert.ToBoolean(Property(i, p) ?? false);
    private void ReadItems()
    {
        Check(this, Native.da_count(context, out uint count), "Listing filesystem");
        if (count > int.MaxValue) throw new InvalidDataException("Too many archive entries.");
        var items = new List<ArchiveItem>((int)count);
        for (uint i = 0; i < count; ++i) {
            var item = new ArchiveItem { Index = (int)i, Path = Text(i,3), Name = Text(i,4),
                IsDirectory = Flag(i,6), IsAltStream = Flag(i,63), IsAux = Flag(i,64), IsDeleted = Flag(i,65),
                IsEncrypted = Flag(i,15), PosixAttributes = Convert.ToUInt32(Property(i,53) ?? 0),
                Method = Text(i,22), Error = Text(i,55), Modified = Property(i,12) as DateTime? };
            Check(this, Native.da_parent(context, i, out uint parent, out uint kind), "Reading raw parent chain");
            item.ParentIndex = parent == uint.MaxValue ? -1 : checked((int)parent); item.ParentIsAltStream = kind == 1;
            object? size = Property(i,7); item.Size = size == null ? (item.IsDirectory ? 0 : -1) : checked(Convert.ToInt64(size));
            string sym = Text(i,54), hard = Text(i,90);
            item.IsHardLink = hard.Length != 0; item.IsLink = item.IsHardLink || sym.Length != 0 || (item.PosixAttributes & 0xF000) == 0xA000;
            item.LinkTarget = sym.Length != 0 ? sym : hard; item.Issue = item.Error;
            if (item.IsLink) item.Issue = "Link must be preserved as metadata; it is not followed.";
            if (item.IsEncrypted) item.Issue = "Encrypted item is unsupported.";
            if (!item.IsDirectory && item.Size < 0) item.Issue = "Item has no declared size.";
            items.Add(item);
        }
        Items = items.AsReadOnly(); Label = Text(0,59,true); if (Label.Length == 0) Label = Text(0,4,true);
        if (Label.Length == 0 && HandlerName == "HFS") foreach (var item in items) if (item.IsDirectory && item.ParentIndex == -1) { Label = item.Name; break; }
        if (Label.Length == 0 && HandlerName == "Iso") foreach (var line in Text(0,28,true).Split('\n')) if (line.StartsWith("Volume: ", StringComparison.Ordinal)) { Label = line.Substring(8).TrimEnd('\r'); break; }
        ErrorFlags = Convert.ToUInt32(Property(0,71,true) ?? 0); WarningFlags = Convert.ToUInt32(Property(0,72,true) ?? 0); Warning = Text(0,73,true);
    }
    internal Stream OpenItem(int index)
    {
        lock (gate) {
            EnsureOpen(); if ((uint)index >= (uint)Items.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var item = Items[index];
            if (item.IsDirectory || item.IsLink || item.IsEncrypted || item.Size < 0 || item.Error.Length != 0)
                throw new IOException("Entry cannot be read as a regular file: " + item.Path + " " + item.Issue);
            if (HandlerName != "APFS" && item.Method.Length == 0) {
                int hr = Native.da_item_open(context, (uint)index, out IntPtr native);
                if (hr == 0) { var stream = new NativeItemStream(this, native, item.Size); streams.Add(stream); return stream; }
                if (hr != 1) Check(this, hr, "Opening item " + index);
                else if (callbackError != null) Check(this, 0, "Opening item " + index);
            }
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiscArchive-item-" + Guid.NewGuid().ToString("N"));
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.DeleteOnClose);
            try {
                Exception? writeError = null; long written = 0;
                Native.WriteFn write = (IntPtr _, IntPtr data, uint n, out uint done) => {
                    done = 0; try {
                        if (n > int.MaxValue || written > item.Size - n) throw new InvalidDataException("Decoded file exceeds declared size.");
                        byte[] bytes = new byte[Math.Min(n, 131072u)];
                        while (done < n) { int part = (int)Math.Min((uint)bytes.Length, n - done); Marshal.Copy(IntPtr.Add(data, (int)done), bytes, 0, part); output.Write(bytes,0,part); done += (uint)part; }
                        written += n; return 0;
                    } catch (Exception ex) { writeError = ex; return unchecked((int)0x80004005); }
                };
                int hr = Native.da_extract(context, (uint)index, IntPtr.Zero, write, out int result); GC.KeepAlive(write);
                Check(this, hr, "Decoding item " + index);
                if (writeError != null) throw new IOException("Writing decoded item failed.", writeError);
                if (result != 0 || written != item.Size || output.Length != item.Size) throw new InvalidDataException($"Incomplete decoded item {index}: result {result}, bytes {written}/{item.Size}.");
                output.Position = 0; return new ReadOnlyStream(output);
            } catch { output.Dispose(); throw; }
        }
    }
    private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(SevenZipReader)); }
    public void Dispose()
    {
        lock (gate) {
            if (disposed) return; disposed = true;
            foreach (var s in streams.ToArray()) s.Dispose(); streams.Clear();
            IntPtr old = context; context = IntPtr.Zero; if (old != IntPtr.Zero) Check(this, Native.da_close(old), "Closing filesystem");
            GC.KeepAlive(read); GC.KeepAlive(seek);
        }
    }
    private sealed class NativeItemStream : Stream
    {
        private SevenZipReader? owner; private IntPtr native; private readonly long length; private long position; private Exception? fault;
        internal NativeItemStream(SevenZipReader o, IntPtr p, long size) { owner = o; native = p; length = size; }
        public override int Read(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer); if (offset < 0 || count < 0 || offset > buffer.Length-count) throw new ArgumentOutOfRangeException();
            var o = owner ?? throw new ObjectDisposedException(nameof(NativeItemStream)); lock (o.gate) {
                o.EnsureOpen(); if (fault != null) throw new IOException("Item stream previously failed.",fault);
                if (position >= length || count == 0) return 0;
                int wanted = (int)Math.Min(Math.Min(count,131072),length-position); IntPtr b = Marshal.AllocHGlobal(wanted);
                try { Check(o,Native.da_item_read(native,b,(uint)wanted,out uint n),"Reading item");
                    if (n == 0 || n > wanted) throw new EndOfStreamException("Item ended before declared length.");
                    Marshal.Copy(b,buffer,offset,(int)n); position += n; return (int)n;
                } catch (Exception error) { fault=error; throw; } finally { Marshal.FreeHGlobal(b); }
            }
        }
        public override long Seek(long offset, SeekOrigin origin) {
            var o = owner ?? throw new ObjectDisposedException(nameof(NativeItemStream)); lock(o.gate) {
                o.EnsureOpen(); if (fault != null) throw new IOException("Item stream previously failed.",fault);
                if (origin < SeekOrigin.Begin || origin > SeekOrigin.End) throw new ArgumentOutOfRangeException(nameof(origin));
                long target = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : length)+offset);
                if (target < 0) throw new IOException("Negative item seek position.");
                try {
                    Check(o, Native.da_item_seek(native,target,0,out ulong actual),"Seeking item");
                    if (actual != (ulong)target) throw new IOException("Item seek did not reach requested position.");
                    return position = target;
                } catch (Exception error) { fault=error; throw; }
            }
        }
        protected override void Dispose(bool disposing) { var o = owner; if (o != null) lock(o.gate) { Native.da_item_close(native); native = IntPtr.Zero; owner = null; o.streams.Remove(this); } base.Dispose(disposing); }
        public override bool CanRead => owner != null; public override bool CanSeek => owner != null; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => Seek(value,SeekOrigin.Begin); }
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b,int o,int n) => throw new NotSupportedException();
    }
    private sealed class ReadOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position=value; }
        public override int Read(byte[] b,int o,int n) => inner.Read(b,o,n); public override long Seek(long o,SeekOrigin s)=>inner.Seek(o,s);
        public override void Flush() { } public override void SetLength(long v)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private static class Native
    {
        internal static readonly string DirectoryPath = GetDirectory();
        internal static string LibraryPath => System.IO.Path.Combine(DirectoryPath,"7z.so");
        private static string GetDirectory() {
            string? configured = Environment.GetEnvironmentVariable("DISCARCHIVE_NATIVE_DIR");
            string result = configured ?? System.IO.Path.Combine(AppContext.BaseDirectory,"native");
            if (!System.IO.Path.IsPathRooted(result)) throw new IOException("DISCARCHIVE_NATIVE_DIR must be absolute.");
            return result;
        }
        static Native() {
            // Explicit absolute loading prevents searching user-controlled working directories.
            string name = OperatingSystem.IsMacOS() ? "libdiscarchive7z.dylib" : "libdiscarchive7z.so";
            IntPtr module = NativeLibrary.Load(System.IO.Path.Combine(DirectoryPath,name));
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,(library,assembly,path)=>library == "discarchive7z" ? module : IntPtr.Zero);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadFn(IntPtr user,IntPtr data,uint size,out uint processed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SeekFn(IntPtr user,long offset,uint origin,out ulong position);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteFn(IntPtr user,IntPtr data,uint size,out uint processed);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_open([MarshalAs(UnmanagedType.LPUTF8Str)] string library,[MarshalAs(UnmanagedType.LPUTF8Str)] string handler,IntPtr user,ReadFn read,SeekFn seek,out IntPtr context);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_close(IntPtr context);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_count(IntPtr context,out uint count);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_property(IntPtr context,uint index,uint property,int volume,out uint type,out ulong number,out IntPtr text);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_parent(IntPtr context,uint index,out uint parent,out uint type);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_item_open(IntPtr context,uint index,out IntPtr item);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_item_read(IntPtr item,IntPtr buffer,uint size,out uint read);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_item_seek(IntPtr item,long offset,uint origin,out ulong actual);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern void da_item_close(IntPtr item);
        [DllImport("discarchive7z",CallingConvention=CallingConvention.Cdecl)] internal static extern int da_extract(IntPtr context,uint index,IntPtr user,WriteFn write,out int result);
    }
}
