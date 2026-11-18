using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive
{
    public static class Optical
    {
        public static string[] Drives()
        {
            List<string> result = new List<string>();
            uint mask = OpticalNative.GetLogicalDrives();
            for (int i = 0; i < 26; i++)
            {
                string root = ((char)('A' + i)).ToString() + ":\\";
                if ((mask & (1u << i)) != 0 && OpticalNative.GetDriveType(root) == 5)
                    result.Add(root);
            }
            return result.ToArray();
        }

        public static bool IsReady(string root)
        {
            try
            {
                root = OpticalNative.Root(root);
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5) return false;
                    OpticalNative.Identity(root);
                    return true;
                }
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }

        public static OpticalLease Acquire(string root)
        {
            return new OpticalLease(OpticalNative.Root(root));
        }

        // This is positive evidence of no media, not the inverse of IsReady.
        // ERROR_NOT_READY (21) also covers spinning up/bad media and is unknown.
        public static bool IsEmpty(string root)
        {
            try
            {
                root = OpticalNative.Root(root);
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5) return false;
                    using (SafeFileHandle device = OpticalNative.CreateFile("\\\\.\\" + root.Substring(0, 2),
                        0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (device.IsInvalid || !OpticalNative.Probe(device)) return false;
                        uint count;
                        int error;
                        bool counted = OpticalNative.Count(device, out count, out error);
                        if (!OpticalNative.ExplicitNoMedia(counted, error))
                        {
                            // Some USB optical drivers collapse MEDIUM NOT
                            // PRESENT into ERROR_NOT_READY. Read the explicit
                            // current MMC media status, never infer from 21.
                            return error == 21 && OpticalNative.ScsiEmpty(root) &&
                                OpticalNative.Probe(device) && OpticalNative.GetDriveType(root) == 5;
                        }
                        // Require another explicit observation and a working
                        // CDROM device, rather than a stale drive-letter entry.
                        if (!OpticalNative.Probe(device) || OpticalNative.GetDriveType(root) != 5) return false;
                        counted = OpticalNative.Count(device, out count, out error);
                        return OpticalNative.ExplicitNoMedia(counted, error);
                    }
                }
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }
    }

    // The caller must keep this lease alive for the whole scan/copy/verification,
    // and call Verify at file/chunk boundaries and immediately before publishing.
    // A volume serial alone is deliberately never treated as proof of continuity.
    public sealed class OpticalLease : IDisposable
    {
        private readonly object sync = new object();
        private readonly string root;
        private SafeFileHandle handle;
        private string identity;
        private uint changeCount;
        private bool hasCount, locked, disposed, failed;

        internal OpticalLease(string root)
        {
            this.root = root;
            try
            {
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5)
                        throw new IOException("来源必须是 Windows 识别的 CD/DVD 光驱。");
                    // Reject an empty drive before issuing any locking control.
                    string before = OpticalNative.Identity(root);
                    // FILE_READ_ATTRIBUTES needs no administrator privileges.
                    handle = OpticalNative.CreateFile("\\\\.\\" + root.Substring(0, 2),
                        0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) throw OpticalNative.Error("无法打开光驱");
                    locked = OpticalNative.Lock(handle, true);
                    uint count;
                    int error;
                    hasCount = OpticalNative.Count(handle, out count, out error);
                    changeCount = count;
                    // Unsupported controls may fall back to the caller-owned
                    // tray lock; real media/device errors must never fall back.
                    if (!hasCount && (!locked || !OpticalNative.Unsupported(error)))
                        throw new IOException("无法建立原盘保护；光驱未提供可用的换盘计数或锁盘保护。", new Win32Exception(error));
                    identity = OpticalNative.Identity(root);
                    if (!String.Equals(before, identity, StringComparison.Ordinal))
                        throw new IOException("建立保护时光盘发生变化，请重新开始。");
                    VerifyCore();
                }
            }
            catch { Dispose(); throw; }
        }

        public void Verify()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("OpticalLease");
                if (failed) throw new IOException("原盘保护已经失效，请重新开始本次任务。");
                try
                {
                    using (new OpticalNative.ErrorMode()) { VerifyCore(); }
                }
                catch { failed = true; throw; }
            }
        }

        private void VerifyCore()
        {
            if (OpticalNative.GetDriveType(root) != 5 ||
                !String.Equals(identity, OpticalNative.Identity(root), StringComparison.Ordinal))
                throw new IOException("光盘已移除、更换或光驱发生变化，任务已停止。");
            uint count;
            int error;
            bool counted = OpticalNative.Count(handle, out count, out error);
            if (hasCount)
            {
                if (!counted || count != changeCount)
                    throw new IOException("检测到换盘或无法确认原盘，任务已停止。", new Win32Exception(error));
            }
            else
            {
                if ((!counted && !OpticalNative.Unsupported(error)) || !OpticalNative.Probe(handle))
                    throw new IOException("光驱连接或锁盘保护无法确认，任务已停止。");
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (handle != null)
                {
                    try
                    {
                        using (new OpticalNative.ErrorMode())
                            if (locked && !handle.IsInvalid && !handle.IsClosed) OpticalNative.Lock(handle, false);
                    }
                    finally { handle.Dispose(); }
                }
            }
        }
    }

    internal static class OpticalNative
    {
        // Microsoft WinSDK winioctl.h: CTL_CODE(0x2d, function,
        // METHOD_BUFFERED=0, FILE_ANY_ACCESS=0). Count is a 4-byte ULONG.
        internal const uint CheckVerify2 = (0x2du << 16) | (0x0200u << 2);
        internal const uint EjectionControl = (0x2du << 16) | (0x0250u << 2);
        internal const uint GetDeviceNumber = (0x2du << 16) | (0x0420u << 2);

        internal static bool ExplicitNoMedia(bool success, int error)
        {
            return !success && error == 1112; // ERROR_NO_MEDIA_IN_DRIVE only.
        }

        internal static bool ScsiEmpty(string root)
        {
            // SPTI requires read/write HANDLE access, but GET EVENT STATUS
            // NOTIFICATION (4A) only reads; never writes/loads/ejects/locks.
            // Windows permits ordinary users with device access; no elevation.
            using (SafeFileHandle device = CreateFile("\\\\.\\" + root.Substring(0, 2),
                0xC0000000u, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                return !device.IsInvalid && Probe(device) && MediaStatusSaysEmpty(device) && MediaStatusSaysEmpty(device);
            }
        }

        private static bool MediaStatusSaysEmpty(SafeFileHandle device)
        {
            // Native SCSI_PASS_THROUGH layout, including ULONG_PTR alignment.
            // Sense + 8-byte MMC media response, all in this buffered request.
            int size = IntPtr.Size == 8 ? 56 : 44;
            int senseField = IntPtr.Size == 8 ? 32 : 24;
            int dataOffset = size + 32;
            int pointerField = IntPtr.Size == 8 ? 24 : 20;
            int cdb = IntPtr.Size == 8 ? 36 : 28;
            byte[] buffer = new byte[size + 40];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)size), 0, buffer, 0, 2);
            buffer[6] = 10;
            buffer[7] = 32; // SenseInfoLength
            buffer[8] = 1; // SCSI_IOCTL_DATA_IN: read eight status bytes.
            Buffer.BlockCopy(BitConverter.GetBytes(8u), 0, buffer, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(10u), 0, buffer, 16, 4); // timeout
            Buffer.BlockCopy(BitConverter.GetBytes((uint)size), 0, buffer, senseField, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)dataOffset), 0, buffer, pointerField, 4);
            buffer[cdb] = 0x4A;
            buffer[cdb + 1] = 1; // Immediate polling, never wait for an event.
            buffer[cdb + 4] = 0x10; // Media status class only.
            buffer[cdb + 8] = 8; // Allocation length (big endian: 00 08).
            uint returned;
            // CTL_CODE(FILE_DEVICE_CONTROLLER=4, 0x401, BUFFERED, READ|WRITE).
            if (!DeviceIoControl(device, 0x0004D004, buffer, (uint)buffer.Length,
                buffer, (uint)buffer.Length, out returned, IntPtr.Zero) ||
                buffer[2] != 0 || returned < dataOffset + 8 || BitConverter.ToUInt32(buffer, 12) != 8) return false;
            byte[] status = new byte[8];
            Buffer.BlockCopy(buffer, dataOffset, status, 0, 8);
            return MediaStatusNoMedia(status);
        }

        internal static bool MediaStatusNoMedia(byte[] status)
        {
            // Official scsi.h: NOTIFICATION_EVENT_STATUS_HEADER +
            // NOTIFICATION_MEDIA_STATUS. MediaPresent is bit 1 of byte 5.
            // Require a complete current media-class response (not NEA),
            // class support, and no contradictory new-media/change event.
            return status != null && status.Length == 8 && status[0] == 0 && status[1] == 6 &&
                status[2] == 4 && (status[3] & 0x10) != 0 &&
                (status[4] == 0 || status[4] == 3) && (status[5] & 0xFE) == 0;
        }

        internal static string Root(string value)
        {
            if (value == null) throw new ArgumentNullException("root");
            if ((value.Length != 2 && value.Length != 3) || value[1] != ':' ||
                (value.Length == 3 && value[2] != '\\' && value[2] != '/') ||
                !((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z')))
                throw new ArgumentException("请选择光驱根目录，例如 H:\\。", "root");
            return Char.ToUpperInvariant(value[0]).ToString() + ":\\";
        }

        internal static IOException Error(string message)
        {
            return new IOException(message + "。", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        internal static string Identity(string root)
        {
            StringBuilder label = new StringBuilder(261), fs = new StringBuilder(261), target = new StringBuilder(4096);
            uint serial, component, flags;
            if (!GetVolumeInformation(root, label, label.Capacity, out serial, out component, out flags, fs, fs.Capacity))
                throw Error("光驱未就绪或无法读取光盘卷信息");
            if (QueryDosDevice(root.Substring(0, 2), target, target.Capacity) == 0)
                throw Error("无法确认光驱设备身份");
            return target.ToString() + "\n" + serial.ToString("X8") + "\n" + fs + "\n" + label;
        }

        internal static bool Unsupported(int error)
        {
            return error == 0 || error == 1 || error == 50 || error == 87 || error == 120;
        }

        internal static bool Count(SafeFileHandle handle, out uint count, out int error)
        {
            byte[] data = new byte[4];
            uint returned;
            bool ok = DeviceIoControl(handle, CheckVerify2, null, 0, data, 4, out returned, IntPtr.Zero);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            count = ok && returned == 4 ? BitConverter.ToUInt32(data, 0) : 0;
            return ok && returned == 4;
        }

        internal static bool Lock(SafeFileHandle handle, bool prevent)
        {
            uint returned;
            // EJECTION_CONTROL is per-caller (unlike MEDIA_REMOVAL), and is
            // released by Windows on handle/process teardown. Never eject/load.
            return DeviceIoControl(handle, EjectionControl, new byte[] { prevent ? (byte)1 : (byte)0 },
                1, null, 0, out returned, IntPtr.Zero);
        }

        internal static bool Probe(SafeFileHandle handle)
        {
            uint returned;
            byte[] data = new byte[12];
            return DeviceIoControl(handle, GetDeviceNumber, null, 0, data, 12, out returned, IntPtr.Zero) &&
                returned >= 12 && BitConverter.ToUInt32(data, 0) == 2; // FILE_DEVICE_CD_ROM
        }

        // Thread-local suppression: no process-wide error mode changes and no
        // Windows "insert a disk" dialogs on readiness/IOCTL failures.
        internal sealed class ErrorMode : IDisposable
        {
            private uint previous;
            internal ErrorMode()
            {
                if (!SetThreadErrorMode(GetThreadErrorMode() | 0x8001u, out previous))
                    throw Error("无法设置光驱静默读取模式");
            }
            public void Dispose() { uint ignored; SetThreadErrorMode(previous, out ignored); }
        }

        [DllImport("kernel32.dll")] internal static extern uint GetLogicalDrives();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder name, int size, out uint serial, out uint component, out uint flags, StringBuilder fs, int fsSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadErrorMode(uint mode, out uint oldMode);
        [DllImport("kernel32.dll")] private static extern uint GetThreadErrorMode();
    }
}
