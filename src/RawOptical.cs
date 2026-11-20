using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive
{
    // Cooked data sectors only: audio extraction and SCSI write commands are
    // deliberately not implemented. The handle requests GENERIC_READ only.
    internal sealed class RawOptical : IDisposable
    {
        public readonly Stream Data;
        private readonly OpticalLease lease;

        private RawOptical(Stream data, OpticalLease lease)
        {
            Data = data;
            this.lease = lease;
        }

        public static bool CanRead(string drive)
        {
            try
            {
                string root = OpticalNative.Root(drive);
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5) return false;
                    string identity = OpticalNative.DeviceIdentity(root);
                    // Readiness checks do not acquire a tray lock.
                    using (RawOpticalReader reader = new RawOpticalReader(root))
                    {
                        long offset;
                        reader.ProbeData(out offset);
                        return String.Equals(identity, OpticalNative.DeviceIdentity(root), StringComparison.Ordinal);
                    }
                }
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static RawOptical Open(string drive)
        {
            string root = OpticalNative.Root(drive);
            RawOpticalReader reader = null;
            OpticalLease guard = null;
            try
            {
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5)
                        throw new IOException("来源必须是 Windows 识别的 CD/DVD 光驱。");
                    string device = OpticalNative.DeviceIdentity(root);
                    reader = new RawOpticalReader(root);
                    if (!String.Equals(device, reader.Identity, StringComparison.Ordinal))
                        throw new IOException("建立读取时光驱设备句柄发生变化。");
                    // Empty, audio-only, disconnected and unreadable devices
                    // must fail before a tray lease is requested.
                    long probeOffset;
                    byte[] first = reader.ProbeData(out probeOffset);
                    guard = new OpticalLease(root, true);
                    // Detect any transition between initial probe and lease.
                    byte[] confirmed = new byte[first.Length];
                    reader.ReadSector(probeOffset, confirmed);
                    for (int i = 0; i < first.Length; i++)
                        if (first[i] != confirmed[i]) throw new IOException("建立保护时光盘发生变化，请重新开始。");
                    if (!String.Equals(device, OpticalNative.DeviceIdentity(root), StringComparison.Ordinal))
                        throw new IOException("建立保护时光驱设备映射发生变化。");
                    using (RawOpticalReader current = new RawOpticalReader(root))
                        if (reader.Length != current.Length || reader.SectorSize != current.SectorSize ||
                            !String.Equals(reader.Identity, current.Identity, StringComparison.Ordinal))
                            throw new IOException("建立保护时光盘数据边界发生变化。");
                    guard.Verify();
                    RawOpticalReader ownedReader = reader;
                    OpticalLease ownedGuard = guard;
                    Stream stream = new AlignedOpticalStream(reader.Length, reader.SectorSize,
                        reader.ReadSector, guard.Verify, delegate
                        {
                            var cleanup = new ResourceCleanup();
                            cleanup.Run("raw.reader.close",ownedReader.Dispose);
                            cleanup.Run("raw.lease.unlock",ownedGuard.Dispose);
                            cleanup.ThrowIfAny();
                        }, reader.ReadSectors);
                    return new RawOptical(stream, guard);
                }
            }
            catch(Exception error)
            {
                ResourceCleanup.Preserve(error,delegate {
                    var cleanup = new ResourceCleanup();
                    if (reader != null) cleanup.Run("raw.reader.close",reader.Dispose);
                    if (guard != null) cleanup.Run("raw.lease.unlock",guard.Dispose);
                    cleanup.ThrowIfAny();
                });
                throw;
            }
        }

        public void Verify() { lease.Verify(); }
        public void Dispose() { Data.Dispose(); }
    }

    // Partial sectors use one-sector cache; aligned interiors use bounded bulk
    // requests. No physical sector outside the requested range is prefetched.
    internal sealed class AlignedOpticalStream : Stream
    {
        private readonly object sync = new object();
        private readonly long length;
        private readonly byte[] sector;
        private readonly Action<long, byte[]> readSector;
        private readonly Action<long, byte[], int, int> readSectors;
        private readonly Action verify, close;
        private long position, cached = -1;
        private bool disposed;

        internal AlignedOpticalStream(long length, int sectorSize, Action<long, byte[]> readSector,
            Action verify, Action close, Action<long, byte[], int, int> readSectors = null)
        {
            if (length <= 0 || sectorSize <= 0 || length % sectorSize != 0)
                throw new ArgumentException("无效的光盘扇区边界。");
            if (readSector == null || verify == null || close == null) throw new ArgumentNullException();
            this.length = length;
            sector = new byte[sectorSize];
            this.readSector = readSector;
            this.readSectors = readSectors;
            this.verify = verify;
            this.close = close;
        }

        public override bool CanRead { get { return !disposed; } }
        public override bool CanSeek { get { return !disposed; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { lock (sync) { EnsureOpen(); return length; } } }
        public override long Position
        {
            get { lock (sync) { EnsureOpen(); return position; } }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            lock (sync)
            {
                EnsureOpen();
                verify(); // Even cached reads must verify media continuity.
                int total = (int)Math.Min((long)count, Math.Max(0, length - position));
                int copied = 0;
                try
                {
                    while (copied < total)
                    {
                        long start = position - position % sector.Length;
                        if (readSectors != null && position == start && total - copied >= sector.Length)
                        {
                            int batch = Math.Min(total - copied, Math.Max(sector.Length, 256 * 1024));
                            batch -= batch % sector.Length;
                            cached = -1;
                            readSectors(start, buffer, offset + copied, batch);
                            Buffer.BlockCopy(buffer, offset + copied + batch - sector.Length, sector, 0, sector.Length);
                            cached = start + batch - sector.Length;
                            copied += batch;
                            position += batch;
                            continue;
                        }
                        if (cached != start)
                        {
                            cached = -1; // A failed read must never validate stale cache.
                            readSector(start, sector);
                            cached = start;
                        }
                        int within = (int)(position - start);
                        int take = Math.Min(total - copied, sector.Length - within);
                        Buffer.BlockCopy(sector, within, buffer, offset + copied, take);
                        copied += take;
                        position += take;
                    }
                    verify();
                    return copied;
                }
                catch { cached = -1; throw; }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (sync)
            {
                EnsureOpen();
                long basis;
                switch (origin)
                {
                    case SeekOrigin.Begin: basis = 0; break;
                    case SeekOrigin.Current: basis = position; break;
                    case SeekOrigin.End: basis = length; break;
                    default: throw new ArgumentException("无效的定位方式。", "origin");
                }
                long next;
                try { next = checked(basis + offset); }
                catch (OverflowException) { throw new IOException("光盘定位超出范围。"); }
                if (next < 0) throw new IOException("不能定位到光盘起点之前。");
                position = next;
                return next;
            }
        }

        public override void Flush() { lock (sync) { EnsureOpen(); } }
        public override void SetLength(long value) { throw new NotSupportedException("光盘只读。"); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException("光盘只读。"); }
        private void EnsureOpen() { if (disposed) throw new ObjectDisposedException("RawOptical"); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lock (sync)
                {
                    if (!disposed) { disposed = true; cached = -1; close(); }
                }
            base.Dispose(disposing);
        }
    }

    internal sealed class RawOpticalReader : IDisposable
    {
        private readonly SafeFileHandle handle;
        internal readonly int SectorSize;
        internal readonly long Length;
        internal readonly string Identity;

        internal RawOpticalReader(string root)
        {
            handle = OpticalNative.CreateFile("\\\\.\\" + root.Substring(0, 2),
                0x80000000u, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw OpticalNative.Error("无法只读打开光驱数据");
                if (!OpticalNative.Probe(handle)) throw new IOException("无法确认光驱连接。");
                Identity = OpticalNative.DeviceIdentity(root, handle);
                byte[] geometry = new byte[32];
                uint returned;
                // DISK_GEOMETRY_EX: BytesPerSector at 20, DiskSize at 24.
                if (DeviceIoControl(handle, 0x000700A0, null, 0, geometry, 32, out returned, IntPtr.Zero) && returned >= 32)
                {
                    SectorSize = BitConverter.ToInt32(geometry, 20);
                    Length = BitConverter.ToInt64(geometry, 24);
                }
                else
                {
                    if (!DeviceIoControl(handle, 0x00070000, null, 0, geometry, 24, out returned, IntPtr.Zero) || returned < 24)
                        throw OpticalNative.Error("无法读取光盘扇区大小");
                    SectorSize = BitConverter.ToInt32(geometry, 20);
                    byte[] size = new byte[8];
                    if (!DeviceIoControl(handle, 0x0007405C, null, 0, size, 8, out returned, IntPtr.Zero) || returned < 8)
                        throw OpticalNative.Error("无法读取光盘数据长度");
                    Length = BitConverter.ToInt64(size, 0);
                }
                if (SectorSize < 512 || SectorSize > 65536 || (SectorSize & (SectorSize - 1)) != 0 ||
                    Length <= 0 || Length % SectorSize != 0)
                    throw new IOException("光盘未提供可信的数据扇区边界。");
            }
            catch { handle.Dispose(); throw; }
        }

        internal void ReadSector(long offset, byte[] buffer)
        { ReadSectors(offset, buffer, 0, buffer.Length); }

        internal void ReadSectors(long offset, byte[] buffer, int at, int count)
        {
            using (new OpticalNative.ErrorMode())
            {
                if (buffer == null || at < 0 || count <= 0 || at > buffer.Length - count ||
                    offset < 0 || offset % SectorSize != 0 || count % SectorSize != 0 || offset > Length - count)
                    throw new ArgumentException("读取必须位于光盘物理扇区范围内。");
                long actual;
                if (!SetFilePointerEx(handle, offset, out actual, 0) || actual != offset)
                    throw OpticalNative.Error("无法定位光盘扇区");
                GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    uint read;
                    if (!ReadFile(handle, Marshal.UnsafeAddrOfPinnedArrayElement(buffer, at), (uint)count, out read, IntPtr.Zero))
                        throw OpticalNative.Error("光盘扇区读取失败");
                    if (read != count) throw new EndOfStreamException("光盘扇区数据不完整。");
                }
                finally { pinned.Free(); }
            }
        }

        internal byte[] ProbeData(out long offset)
        {
            byte[] data = new byte[SectorSize];
            // A bad first sector must not hide otherwise readable data media.
            // At most three cooked-sector reads; no scan or audio extraction.
            long[] candidates = new long[] { 0, Math.Min(16L * SectorSize, Length - SectorSize), Length - SectorSize };
            IOException last = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if ((i > 0 && candidates[i] == candidates[0]) || (i > 1 && candidates[i] == candidates[1])) continue;
                try { ReadSector(candidates[i], data); offset = candidates[i]; return data; }
                catch (IOException ex) { last = ex; }
            }
            throw new IOException("光盘没有可确认的可读数据扇区；可能为空盘、音频盘或介质不可读。", last);
        }

        public void Dispose() { handle.Dispose(); }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
            byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint origin);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
    }
}
