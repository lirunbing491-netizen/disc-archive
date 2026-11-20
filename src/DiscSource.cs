using System;
using System.Collections.Generic;
using System.IO;

namespace DiscArchive
{
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
            internal override IEnumerable<string> Entries(string dir) { return Directory.EnumerateFileSystemEntries(dir); }
            internal override FileAttributes Attributes(string path) { return File.GetAttributes(path); }
            internal override long Length(string path) { return new FileInfo(path).Length; }
            internal override DateTime Modified(string path) { return File.GetLastWriteTime(path); }
            internal override Stream OpenRead(string path) { return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); }
            internal override bool Native { get { return true; } }
        }
    }
}
