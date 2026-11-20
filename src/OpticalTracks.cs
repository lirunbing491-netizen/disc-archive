using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive
{
    internal sealed class TrackInfo
    {
        // Empty means unknown or no positive evidence of additional tracks;
        // it is never a claim that all sessions/tracks were inspected or copied.
        public string Note = "";
        public bool HasUncoveredTracks;
    }

    // Metadata-only optional check. No raw sectors, audio playback, tray locks,
    // media removal commands, writes, elevation, or application dialogs.
    // Official structures/constants: Microsoft ntddcdrm.h / CDROM_READ_TOC_EX,
    // CDROM_TOC_SESSION_DATA, CDROM_TOC, TRACK_DATA:
    // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddcdrm/ns-ntddcdrm-_cdrom_read_toc_ex
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/ntddcdrm.h
    internal static class OpticalTracks
    {
        private const uint ReadTocEx = 0x00024054;

        public static TrackInfo Inspect(string root)
        {
            try
            {
                root = OpticalNative.Root(root);
                using (new OpticalNative.ErrorMode())
                {
                    if (OpticalNative.GetDriveType(root) != 5) return new TrackInfo();
                    using (SafeFileHandle handle = OpticalNative.CreateFile("\\\\.\\" + root.Substring(0, 2),
                        0x80000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid) return new TrackInfo();
                        // Format 1 requires SessionTrack=0; Format 0 starts at
                        // the first available track. Msf=1 makes addresses MSF.
                        byte[] session = Query(handle, 0x81, 12);
                        byte[] toc = Query(handle, 0x80, 804);
                        byte[] confirmed = Query(handle, 0x81, 12);
                        // When session metadata is available, reject a change
                        // during the two queries rather than combine two discs.
                        if ((session != null || confirmed != null) && !Same(session, confirmed)) return new TrackInfo();
                        return Parse(session, toc);
                    }
                }
            }
            catch (ArgumentException) { return new TrackInfo(); }
            catch (IOException) { return new TrackInfo(); }
            catch (UnauthorizedAccessException) { return new TrackInfo(); }
        }

        private static byte[] Query(SafeFileHandle handle, byte format, int capacity)
        {
            byte[] data = new byte[capacity];
            uint returned;
            if (!DeviceIoControl(handle, ReadTocEx, new byte[] { format, 0, 0, 0 }, 4,
                data, (uint)data.Length, out returned, IntPtr.Zero) || returned < 4 || returned > data.Length) return null;
            int declared = (data[0] << 8 | data[1]) + 2;
            if (declared != returned || declared > capacity) return null;
            Array.Resize(ref data, declared);
            return data;
        }

        // Pure parser permits malformed/truncated-response tests without ever
        // opening a drive. Only positive, complete metadata produces a warning.
        internal static TrackInfo Parse(byte[] session, byte[] toc)
        {
            TrackInfo result = new TrackInfo();
            if (ValidLength(session, 12, 12) && session[2] >= 1 && session[3] <= 99 && session[2] <= session[3] &&
                ValidTrack(session, 4, session[6]) && session[6] >= 1 && session[6] <= 99 && session[3] > session[2])
                Append(result, "已确认多个已完成会话；只读当前数据视图，历史区段尚未提取。");

            if (!ValidLength(toc, 20, 804) || toc[2] < 1 || toc[3] > 99 || toc[2] > toc[3]) return result;
            int tracks = toc[3] - toc[2] + 1;
            if (toc.Length != 4 + (tracks + 1) * 8) return result;
            int audio = 0, data = 0, previous = -1;
            for (int i = 0; i <= tracks; i++)
            {
                int p = 4 + i * 8;
                if (!ValidTrack(toc, p, i == tracks ? 0xAA : toc[2] + i)) return result;
                int position = (toc[p + 5] * 60 + toc[p + 6]) * 75 + toc[p + 7];
                if (position <= previous) return result;
                previous = position;
                if (i == tracks) continue;
                if ((toc[p + 1] & 4) != 0) data++; else audio++;
            }
            if (audio > 0)
                Append(result, data > 0 ? "已确认含音频和数据的混合音轨；本工具未提取音轨，仅处理可读数据视图。" :
                    "TOC 已确认所列轨道均为音频；本工具未提取音轨，不能按普通数据文件归档。");
            return result;
        }

        private static bool ValidLength(byte[] bytes, int minimum, int maximum)
        {
            return bytes != null && bytes.Length >= minimum && bytes.Length <= maximum &&
                (bytes[0] << 8 | bytes[1]) + 2 == bytes.Length;
        }
        private static bool ValidTrack(byte[] b, int p, int number)
        {
            return b[p] == 0 && b[p + 3] == 0 && b[p + 2] == number &&
                (b[p + 1] >> 4) == 1 && b[p + 4] == 0 && b[p + 5] <= 99 && b[p + 6] < 60 && b[p + 7] < 75;
        }
        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        private static void Append(TrackInfo info, string text)
        {
            info.HasUncoveredTracks = true;
            info.Note += (info.Note.Length == 0 ? "" : " ") + text;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
            byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}
