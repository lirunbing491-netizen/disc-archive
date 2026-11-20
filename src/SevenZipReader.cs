using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DiscArchive
{
    internal sealed class ArchiveItem
    {
        internal int Index;
        internal string Path, Name, LinkTarget, Method, Error, Issue;
        internal long Size;
        internal bool IsDirectory, IsAltStream, IsLink, IsHardLink, IsEncrypted, IsAux, IsDeleted;
        internal uint PosixAttributes;
        internal int ParentIndex = -1;
        internal bool ParentIsAltStream;
        internal DateTime? Modified;
    }

    // Caller owns the source; it must remain open and must not be used outside
    // this reader while the reader is alive. Pass a bounded partition slice.
    internal sealed class SevenZipReader : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<ItemStream> streams = new List<ItemStream>();
        private SevenZipLibrary library;
        private SevenZipInterop.IArchive archive;
        private SevenZipInterop.Input input;
        private bool disposed;
        private bool nativeReleaseFailed;
        internal IList<ArchiveItem> Items { get; private set; }
        internal string Label { get; private set; }
        internal uint ErrorFlags { get; private set; }
        internal uint WarningFlags { get; private set; }
        internal string Warning { get; private set; }
        internal string HandlerName { get; private set; }
        // No handler's hardlink round trip is validated here; HFS+ Finder
        // hardlink stubs are not even exposed as links by the official handler.
        internal bool HardLinkSupportKnown { get { return false; } }

        private SevenZipReader() { }
        internal static SevenZipReader Open(Stream partition, string format)
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native", IntPtr.Size == 8 ? "x64" : "x86", "7z.dll");
            return Open(partition, format, path);
        }
        internal static SevenZipReader Open(Stream partition, string format, string libraryPath)
        {
            if (partition == null) throw new ArgumentNullException("partition");
            if (!partition.CanRead || !partition.CanSeek) throw new ArgumentException("A readable, seekable partition stream is required.", "partition");
            string handler = Normalize(format);
            SevenZipReader reader = new SevenZipReader();
            try
            {
                reader.HandlerName = handler;
                reader.library = new SevenZipLibrary(libraryPath);
                reader.archive = reader.library.Create(handler);
                reader.input = new SevenZipInterop.Input(partition);
                ulong maxStart = 0;
                int hr = reader.archive.Open(reader.input, ref maxStart, IntPtr.Zero);
                reader.input.CheckError();
                SevenZipInterop.Check(hr, "Opening " + handler);
                reader.ReadItems();
                return reader;
            }
            catch(Exception error) { ResourceCleanup.Preserve(error,reader.Dispose); throw; }
        }
        private static string Normalize(string format)
        {
            string value = (format ?? "").Trim().ToUpperInvariant();
            if (value == "HFS" || value == "HFS+" || value == "HFSX") return "HFS";
            if (value == "ISO" || value == "ISO9660" || value == "ISO 9660" || value == "JOLIET") return "Iso";
            if (value == "UDF") return "Udf";
            if (value == "APFS") return "APFS";
            throw new NotSupportedException("Unsupported filesystem handler: " + format);
        }
        private object Property(uint index, uint property, bool volume)
        {
            SevenZipInterop.PropVariant value = new SevenZipInterop.PropVariant();
            try
            {
                SevenZipInterop.Check(volume ? archive.GetArchiveProperty(property, ref value) : archive.GetProperty(index, property, ref value), "Reading archive property " + property);
                input.CheckError();
                return value.Value;
            }
            finally { value.Clear(); }
        }
        private static uint UInt(object value) { return value == null ? 0 : Convert.ToUInt32(value); }
        private static string Text(object value) { return value == null ? "" : Convert.ToString(value); }
        private static bool Flag(object value) { return value != null && Convert.ToBoolean(value); }
        private void ReadItems()
        {
            uint count;
            SevenZipInterop.Check(archive.GetNumberOfItems(out count), "Listing archive");
            if (count > int.MaxValue) throw new InvalidDataException("Archive has too many entries.");
            List<ArchiveItem> result = new List<ArchiveItem>();
            for (uint i = 0; i < count; i++)
            {
                ArchiveItem item = new ArchiveItem();
                item.Index = (int)i;
                item.Path = Text(Property(i, 3, false));
                item.Name = Text(Property(i, 4, false));
                SevenZipInterop.IRawProps raw = archive as SevenZipInterop.IRawProps;
                if (raw != null)
                {
                    uint parent, parentType;
                    SevenZipInterop.Check(raw.GetParent(i, out parent, out parentType), "Reading item parent");
                    item.ParentIndex = parent == uint.MaxValue ? -1 : checked((int)parent);
                    item.ParentIsAltStream = parentType == 1;
                }
                item.IsDirectory = Flag(Property(i, 6, false));
                item.IsAltStream = Flag(Property(i, 63, false));
                item.IsAux = Flag(Property(i, 64, false));
                item.IsDeleted = Flag(Property(i, 65, false));
                object size = Property(i, 7, false);
                item.Size = size == null ? (item.IsDirectory ? 0 : -1) : Convert.ToInt64(size);
                item.Modified = Property(i, 12, false) as DateTime?;
                item.PosixAttributes = UInt(Property(i, 53, false));
                string sym = Text(Property(i, 54, false));
                string hard = Text(Property(i, 90, false));
                item.IsHardLink = hard.Length != 0;
                item.IsLink = item.IsHardLink || sym.Length != 0 || (item.PosixAttributes & 0xF000) == 0xA000;
                item.LinkTarget = sym.Length != 0 ? sym : hard;
                item.Method = Text(Property(i, 22, false));
                item.Error = Text(Property(i, 55, false));
                item.IsEncrypted = Flag(Property(i, 15, false));
                item.Issue = item.Error;
                if (item.IsLink) item.Issue = "Link must be preserved as metadata; it is not followed.";
                if (item.IsEncrypted) item.Issue = "Encrypted item is unsupported.";
                if (!item.IsDirectory && item.Size < 0) item.Issue = "Item has no declared size.";
                result.Add(item);
            }
            Items = result.AsReadOnly();
            Label = Text(Property(0, 59, true));
            if (Label.Length == 0) Label = Text(Property(0, 4, true));
            if (Label.Length == 0 && HandlerName == "HFS")
                foreach (ArchiveItem item in result)
                    if (item.IsDirectory && item.ParentIndex == -1) { Label = item.Name; break; }
            if (Label.Length == 0 && HandlerName == "Iso")
                foreach (string line in Text(Property(0, 28, true)).Split('\n'))
                    if (line.StartsWith("Volume: ", StringComparison.Ordinal)) { Label = line.Substring(8).TrimEnd('\r'); break; }
            ErrorFlags = UInt(Property(0, 71, true));
            WarningFlags = UInt(Property(0, 72, true));
            Warning = Text(Property(0, 73, true));
        }
        internal Stream OpenItem(int index)
        {
            lock (gate)
            {
                EnsureOpen();
                if (index < 0 || index >= Items.Count) throw new ArgumentOutOfRangeException("index");
                ArchiveItem item = Items[index];
                if (item.IsDirectory) throw new IOException("Cannot open a directory as a file.");
                if (item.IsLink) throw new NotSupportedException("Links must be preserved as metadata and must not be followed: " + item.Path);
                if (item.IsEncrypted) throw new NotSupportedException("Encrypted file is not supported: " + item.Path);
                if (item.Size < 0 || !String.IsNullOrEmpty(item.Error)) throw new InvalidDataException("Entry has no valid size or reports an error: " + item.Path + " " + item.Error);
                // APFS GetStream can return its stored data rather than decoded
                // decmpfs bytes. Extract verifies decoded size and operation result.
                bool decode = HandlerName == "APFS" || !String.IsNullOrEmpty(item.Method);
                SevenZipInterop.IGetStream get = archive as SevenZipInterop.IGetStream;
                if (!decode && get != null)
                {
                    SevenZipInterop.ISequentialIn native = null;
                    try
                    {
                        int hr = get.GetStream((uint)index, out native);
                        input.CheckError();
                        if (hr == 0 && native != null)
                        {
                            ItemStream stream = new ItemStream(this, native, item.Size);
                            streams.Add(stream); native = null; return stream;
                        }
                        if (hr != 0 && hr != 1) SevenZipInterop.Check(hr, "Opening archive file");
                    }
                    catch(Exception error)
                    {
                        if(native != null)
                        {
                            SevenZipInterop.ISequentialIn failed = native; native = null;
                            ResourceCleanup.Preserve(error,delegate {
                                var cleanup = new ResourceCleanup();
                                if(!cleanup.Run("7zip.item.release",delegate { if(Marshal.IsComObject(failed)) Marshal.ReleaseComObject(failed); })) nativeReleaseFailed = true;
                                cleanup.ThrowIfAny();
                            });
                        }
                        throw;
                    }
                    finally
                    {
                        if(native != null)
                        {
                            var cleanup = new ResourceCleanup();
                            if(!cleanup.Run("7zip.item.release",delegate { if(Marshal.IsComObject(native)) Marshal.ReleaseComObject(native); })) nativeReleaseFailed = true;
                            cleanup.ThrowIfAny();
                        }
                    }
                }
                return ExtractItem(item);
            }
        }
        private Stream ExtractItem(ArchiveItem item)
        {
            // Only a single requested decoded file is staged, never a disc image.
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiscArchive-item-" + Guid.NewGuid().ToString("N") + ".tmp");
            FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            try
            {
                SevenZipInterop.Extraction callback = new SevenZipInterop.Extraction((uint)item.Index, output, item.Size);
                int hr = archive.Extract(new uint[] { (uint)item.Index }, 1, 0, callback);
                input.CheckError();
                if (callback.Error != null) throw new IOException("Writing decoded item failed.", callback.Error);
                SevenZipInterop.Check(hr, "Decoding " + item.Path);
                if (!callback.Requested || callback.Result != 0 || output.Length != item.Size)
                    throw new InvalidDataException("7-Zip could not decode the complete file: " + item.Path + " (result " + callback.Result + ", bytes " + output.Length + "/" + item.Size + ").");
                output.Position = 0;
                return new ReadOnlyOwnedStream(output);
            }
            catch(Exception error) { ResourceCleanup.Preserve(error,output.Dispose); throw; }
        }
        private void EnsureOpen() { if (disposed) throw new ObjectDisposedException("SevenZipReader"); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                var cleanup = new ResourceCleanup();
                foreach (ItemStream stream in streams.ToArray()) cleanup.Run("7zip.item.dispose",stream.Dispose);
                streams.Clear();
                SevenZipInterop.IArchive closing = archive;
                archive = null;
                if (closing != null)
                {
                    cleanup.Run("7zip.archive.close",delegate {
                        int hr = closing.Close();
                        if(hr != 0) throw new COMException("Archive Close failed.",hr);
                    });
                    if(!cleanup.Run("7zip.archive.release",delegate {
                        // CreateObject hands this reader one privately owned RCW.
                        // A remaining runtime reference means unloading is unsafe.
                        if(Marshal.IsComObject(closing) && Marshal.ReleaseComObject(closing) != 0)
                            throw new InvalidOperationException("Archive RCW still has runtime references.");
                    })) nativeReleaseFailed = true;
                }
                SevenZipLibrary closingLibrary = library;
                library = null;
                if (closingLibrary != null)
                {
                    // An unreleased RCW can still execute DLL code during later
                    // CLR cleanup. Keep that module loaded rather than unload it
                    // underneath a native object; its release failure is reported.
                    if (!nativeReleaseFailed) cleanup.Run("7zip.library.unload",closingLibrary.Dispose);
                    else cleanup.Run("7zip.library.retained.after-release-failure",delegate { throw new IOException("Native objects may still reference the module."); });
                }
                input = null;
                cleanup.ThrowIfAny();
            }
        }

        private sealed class ItemStream : Stream
        {
            private SevenZipReader owner;
            private SevenZipInterop.ISequentialIn native;
            private SevenZipInterop.IIn seekable;
            private FileStream cache;
            private readonly long length;
            private long position;
            private long sourcePosition;
            private long nativePosition;
            private IOException fault;
            internal ItemStream(SevenZipReader owner, SevenZipInterop.ISequentialIn native, long length)
            {
                this.owner = owner; this.native = native; this.length = length;
                // QueryInterface for the official IInStream ABI. No assumption
                // about the concrete extent/limited stream implementation.
                seekable = native as SevenZipInterop.IIn;
                sourcePosition = owner.input.Position;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                SevenZipReader current = owner;
                if (current == null) throw new ObjectDisposedException("Archive item stream");
                lock (current.gate)
                {
                    current.EnsureOpen();
                    if (fault != null) throw new IOException("Archive item stream previously failed.", fault);
                    if (count == 0 || position >= length) return 0;
                    int wanted = (int)Math.Min(Math.Min(count, 128 * 1024), length - position);
                    if (seekable != null)
                    {
                        int n = ReadNative(current, buffer, offset, wanted);
                        position += n; return n;
                    }
                    // Truly sequential streams get a lazy prefix cache, bounded
                    // by this one item's declared length. Seek alone does not
                    // read data or allocate a file; read extends only as needed.
                    if (cache == null)
                    {
                        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiscArchive-seek-" + Guid.NewGuid().ToString("N") + ".tmp");
                        cache = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
                    }
                    long needed = checked(position + wanted);
                    byte[] chunk = null;
                    while (nativePosition < needed)
                    {
                        if (chunk == null) chunk = new byte[128 * 1024];
                        int size = (int)Math.Min(chunk.Length, needed - nativePosition);
                        int n = ReadNative(current, chunk, 0, size);
                        try { cache.Position = nativePosition - n; cache.Write(chunk, 0, n); }
                        catch (IOException ex) { fault = ex; throw; }
                    }
                    cache.Position = position;
                    int copied = cache.Read(buffer, offset, wanted);
                    if (copied <= 0) throw new IOException("Archive seek cache ended unexpectedly.");
                    position += copied; return copied;
                }
            }
            private int ReadNative(SevenZipReader current, byte[] buffer, int offset, int wanted)
            {
                IntPtr data = Marshal.AllocHGlobal(wanted), processed = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(processed, 0);
                    // Some official streams seek only at extent boundaries.
                    // Serialized calls also need the stream's own source cursor.
                    current.input.Position = sourcePosition;
                    int hr;
                    try { hr = native.Read(data, (uint)wanted, processed); }
                    finally { sourcePosition = current.input.Position; }
                    current.input.CheckError();
                    SevenZipInterop.Check(hr, "Reading archive file");
                    int n = Marshal.ReadInt32(processed);
                    if (n <= 0 || n > wanted || nativePosition > length - n) throw new EndOfStreamException("Archive file ended before its declared length or exceeded it.");
                    Marshal.Copy(data, buffer, offset, n); nativePosition += n; return n;
                }
                catch (IOException ex) { fault = ex; throw; }
                catch (COMException ex) { fault = new IOException("Reading native archive stream failed.", ex); throw fault; }
                finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(processed); }
            }
            protected override void Dispose(bool disposing)
            {
                SevenZipReader current = owner;
                if (current != null) lock (current.gate)
                {
                    var cleanup = new ResourceCleanup();
                    SevenZipInterop.ISequentialIn closing = native;
                    native = null;
                    seekable = null;
                    if (closing != null && !cleanup.Run("7zip.item.release",delegate { if (Marshal.IsComObject(closing)) Marshal.ReleaseComObject(closing); })) current.nativeReleaseFailed = true;
                    FileStream closingCache = cache;
                    cache = null;
                    if (closingCache != null) cleanup.Run("7zip.item.cache.close",closingCache.Dispose);
                    current.streams.Remove(this); owner = null;
                    cleanup.ThrowIfAny();
                }
                base.Dispose(disposing);
            }
            public override bool CanRead { get { return owner != null; } }
            public override bool CanSeek { get { return owner != null; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin)
            {
                SevenZipReader current = owner;
                if (current == null) throw new ObjectDisposedException("Archive item stream");
                lock (current.gate)
                {
                    current.EnsureOpen();
                    long target;
                    try
                    {
                        if (origin != SeekOrigin.Begin && origin != SeekOrigin.Current && origin != SeekOrigin.End) throw new IOException("Invalid archive seek origin.");
                        target = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : length) + offset);
                    }
                    catch (OverflowException ex) { throw new IOException("Archive seek position overflow.", ex); }
                    if (target < 0) throw new IOException("Cannot seek before the archive file begins.");
                    if (target == position) return position;
                    if (seekable != null)
                    {
                        IntPtr actual = Marshal.AllocHGlobal(8);
                        try
                        {
                            current.input.Position = sourcePosition;
                            int hr;
                            try { hr = seekable.Seek(target, 0, actual); }
                            finally { sourcePosition = current.input.Position; }
                            current.input.CheckError(); SevenZipInterop.Check(hr, "Seeking archive file");
                            if (Marshal.ReadInt64(actual) != target) throw new IOException("Archive stream did not reach the requested position.");
                            nativePosition = target;
                        }
                        finally { Marshal.FreeHGlobal(actual); }
                    }
                    position = target; return position;
                }
            }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int n) { throw new NotSupportedException(); }
        }
        private sealed class ReadOnlyOwnedStream : Stream
        {
            private readonly Stream inner;
            internal ReadOnlyOwnedStream(Stream inner) { this.inner = inner; }
            public override bool CanRead { get { return inner.CanRead; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position { get { return inner.Position; } set { inner.Position = value; } }
            public override int Read(byte[] b, int o, int n) { return inner.Read(b, o, n); }
            public override long Seek(long o, SeekOrigin origin) { return inner.Seek(o, origin); }
            public override void Flush() { }
            public override void Write(byte[] b, int o, int n) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
    }

    internal sealed class SevenZipLibrary : IDisposable
    {
        private IntPtr module;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCount(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetProperty(uint index, uint property, ref SevenZipInterop.PropVariant value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateObject(ref Guid classId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out SevenZipInterop.IArchive value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
        internal SevenZipLibrary(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path)) throw new ArgumentException("7z.dll requires an absolute path.", "path");
            // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32.
            module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new IOException("Cannot load the matching architecture 7z.dll: " + path, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        private T Export<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new NotSupportedException("7z.dll has no " + name + " export.");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        internal SevenZipInterop.IArchive Create(string name)
        {
            uint count;
            SevenZipInterop.Check(Export<GetCount>("GetNumberOfFormats")(out count), "Listing 7-Zip handlers");
            GetProperty property = Export<GetProperty>("GetHandlerProperty2");
            for (uint i = 0; i < count; i++)
            {
                SevenZipInterop.PropVariant value = new SevenZipInterop.PropVariant();
                bool match;
                try { SevenZipInterop.Check(property(i, 0, ref value), "Reading handler name"); match = String.Equals(value.Value as string, name, StringComparison.OrdinalIgnoreCase); }
                finally { value.Clear(); }
                if (!match) continue;
                try
                {
                    SevenZipInterop.Check(property(i, 1, ref value), "Reading handler class ID");
                    if (value.Type != 8 || value.Pointer == IntPtr.Zero || Marshal.ReadInt32(value.Pointer, -4) != 16) throw new InvalidDataException("Invalid handler GUID.");
                    byte[] bytes = new byte[16]; Marshal.Copy(value.Pointer, bytes, 0, 16);
                    Guid classId = new Guid(bytes), iid = typeof(SevenZipInterop.IArchive).GUID;
                    SevenZipInterop.IArchive result;
                    SevenZipInterop.Check(Export<CreateObject>("CreateObject")(ref classId, ref iid, out result), "Creating " + name + " handler");
                    if (result == null) throw new IOException("7-Zip returned no archive handler.");
                    return result;
                }
                finally { value.Clear(); }
            }
            throw new NotSupportedException("The bundled 7z.dll does not provide the " + name + " handler.");
        }
        public void Dispose()
        {
            IntPtr closing = module; module = IntPtr.Zero;
            if (closing != IntPtr.Zero && !FreeLibrary(closing))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
