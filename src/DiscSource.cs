using System;
using System.Collections.Generic;
using System.IO;

namespace DiscArchive
{
    internal static class ArchivePath
    {
        internal static bool IsSharingViolation(IOException error)
        {
#if MAC_PORT
            if(!OperatingSystem.IsWindows())
            {
                // FileStream's Unix sharing lock exposes raw EAGAIN, unlike
                // Win32 HRESULTs. Do not retry EACCES or unrelated raw errno.
                return error.HResult==unchecked((int)0x80070020) || error.HResult==unchecked((int)0x80070021) ||
                    (OperatingSystem.IsLinux() && error.HResult==11) ||
                    (OperatingSystem.IsMacOS() && error.HResult==35);
            }
#endif
            int code=error.HResult&65535;
            return code==32 || code==33;
        }

#if MAC_PORT
        internal static readonly StringComparison Comparison = StringComparison.Ordinal;
        internal static readonly StringComparer Comparer = StringComparer.Ordinal;
#else
        internal static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;
        internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
#endif
    }

    // Only the source side of an import uses this interface. Destination paths
    // always go through the normal filesystem and its existing ownership checks.
    internal abstract class DiscSource
    {
        internal static readonly DiscSource Windows=new WindowsFiles();
        internal abstract IEnumerable<string> Entries(string directory);
        internal abstract FileAttributes Attributes(string path);
        internal abstract long Length(string path);
        internal abstract DateTime Modified(string path);
        internal abstract Stream OpenRead(string path);
        internal virtual bool Native { get { return false; } }
        internal virtual void WriteDescription(string attemptDirectory) { }

        private sealed class WindowsFiles : DiscSource
        {
            internal override IEnumerable<string> Entries(string dir)
            {
                bool ambiguous=false;
                foreach(string path in Directory.EnumerateFileSystemEntries(dir))
                {
#if MAC_PORT
                    // Backslash is legal on POSIX but ambiguous in shared Windows receipts.
                    // Fail visibly rather than collapse it into a hierarchy or silently skip it.
                    if(Path.GetFileName(path).IndexOf('\\')>=0) {ambiguous=true;continue;}
#endif
                    yield return path;
                }
                if(ambiguous)throw new IOException("原盘名称含反斜杠，无法安全写入跨平台清单；已保留可读同级项，该目录未确认完整。");
            }
            internal override FileAttributes Attributes(string path) { return File.GetAttributes(path); }
            internal override long Length(string path) { return new FileInfo(path).Length; }
            internal override DateTime Modified(string path) { return File.GetLastWriteTime(path); }
            internal override Stream OpenRead(string path) { return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); }
            internal override bool Native { get { return true; } }
        }
    }
}
