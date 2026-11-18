using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DiscArchive
{
    public sealed class CopyResult
    {
        public long Files;
        public long Bytes;
        public bool DvdVideoFiles;
    }

    public static class DiscCopy
    {
        private sealed class Entry
        {
            public string Path;
            public bool Directory;
            public long Length;
            public long Ticks;
            public string Hash;
        }

        private sealed class TransferMonitor
        {
            private readonly Action verify;
            private readonly Action<string> progress;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long verifiedAt = -1000;
            private long reportedAt = -1000;

            public TransferMonitor(Action verifyMedia, Action<string> report) { verify = verifyMedia; progress = report; }
            public void Verify(bool force)
            {
                if (verify != null && (force || clock.ElapsedMilliseconds - verifiedAt >= 1000))
                {
                    verify();
                    verifiedAt = clock.ElapsedMilliseconds;
                }
            }
            public void Report(string phase, Entry entry, long bytes, bool force)
            {
                if (progress != null && (force || clock.ElapsedMilliseconds - reportedAt >= 1000))
                {
                    progress(phase + "：" + entry.Path + "  " +
                        (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                        (entry.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
                    reportedAt = clock.ElapsedMilliseconds;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder name,
            int nameSize, out uint serial, out uint maxComponent, out uint flags,
            StringBuilder fs, int fsSize);

        public static CopyResult Run(string sourceRoot, string jobDir,
            Action<string> progress, CancellationToken cancel, Action verifyMedia = null)
        {
            string source = Full(sourceRoot), job = Full(jobDir);
            TransferMonitor monitor = new TransferMonitor(verifyMedia, progress);
            monitor.Verify(true);
            if (Contains(source, job) || Contains(job, source))
                throw new IOException("来源和导出目录不能相同或互相包含。");
            CheckParents(source);
            CheckParents(job);
            if (!System.IO.Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            if (!System.IO.Directory.Exists(job)) throw new DirectoryNotFoundException("请先创建本次任务目录：" + job);
            string stage = System.IO.Path.Combine(job, "未完成-原盘文件");
            string final = System.IO.Path.Combine(job, "原盘文件");
            string partial = System.IO.Path.Combine(job, "未完成-校验清单.tsv");
            string manifest = System.IO.Path.Combine(job, "校验清单.tsv");
            string log = System.IO.Path.Combine(job, "复制日志.txt");
            foreach (string path in new string[] { stage, final, partial, manifest, log })
                if (File.Exists(path) || System.IO.Directory.Exists(path))
                    throw new IOException("本次任务已存在复制结果，请使用全新任务目录：" + path);

            using (StreamWriter journal = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
            {
                journal.AutoFlush = true;
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    uint volume = Volume(source);
                    bool dvdVideo = ValidateDvdVideo(source);
                    List<Entry> entries = Scan(source, cancel, true, monitor);
                    monitor.Verify(true);
                    long count = 0, totalBytes = 0;
                    foreach (Entry e in entries) if (!e.Directory) { count++; totalBytes = checked(totalBytes + e.Length); }
                    if (count == 0) throw new IOException("未找到数据文件：空盘或不支持的音频盘。");
                    CheckFreeSpace(job, totalBytes);
                    System.IO.Directory.CreateDirectory(stage);
                    CopyResult result = new CopyResult { DvdVideoFiles = dvdVideo };
                    using (StreamWriter list = new StreamWriter(new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)))
                    {
                        list.WriteLine("# DiscArchive SHA256 v1; 路径相对原盘文件；转义：\\\\=反斜杠，\\t=制表，\\r=回车，\\n=换行；目录哈希为 -；文件时间为 UTC ticks。");
                        list.WriteLine("类型\t相对路径\t字节数\t最后修改UTCticks\tSHA256");
                        if (dvdVideo) list.WriteLine("# DVD-Video 可见文件归档：影碟文件已校验后仍待试播；不是 ISO，未验证可播放性。无转码，包含整盘可见目录。");
                        foreach (Entry e in entries)
                        {
                            cancel.ThrowIfCancellationRequested();
                            CheckVolume(source, volume);
                            string dst = e.Path.Length == 0 ? stage : System.IO.Path.Combine(stage, e.Path);
                            if (e.Directory)
                            {
                                System.IO.Directory.CreateDirectory(dst);
                                list.WriteLine("D\t" + Escape(e.Path.Length == 0 ? "." : e.Path) + "\t0\t0\t-");
                                continue;
                            }
                            monitor.Report("复制", e, 0, true);
                            monitor.Verify(true);
                            string src = System.IO.Path.Combine(source, e.Path);
                            CheckParents(src);
                            CheckParents(dst);
                            MatchFile(src, e);
                            using (SHA256 sha = SHA256.Create())
                            using (FileStream input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (FileStream output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[1024 * 1024];
                                int n;
                                long bytes = 0;
                                while (true)
                                {
                                    cancel.ThrowIfCancellationRequested();
                                    monitor.Verify(false);
                                    n = input.Read(buffer, 0, buffer.Length);
                                    if (n == 0) break;
                                    output.Write(buffer, 0, n);
                                    sha.TransformBlock(buffer, 0, n, buffer, 0);
                                    bytes += n;
                                    monitor.Report("复制", e, bytes, false);
                                }
                                sha.TransformFinalBlock(new byte[0], 0, 0);
                                output.Flush(true);
                                if (bytes != e.Length) throw new IOException("来源文件长度发生变化：" + e.Path);
                                e.Hash = Hex(sha.Hash);
                            }
                            monitor.Report("复制", e, e.Length, true);
                            MatchFile(src, e);
                            result.Files++;
                            result.Bytes += e.Length;
                            list.WriteLine("F\t" + Escape(e.Path) + "\t" + e.Length + "\t" + e.Ticks + "\t" + e.Hash);
                            list.Flush();
                        }
                        cancel.ThrowIfCancellationRequested();
                        monitor.Verify(true);
                        CheckVolume(source, volume);
                        Compare(entries, Scan(source, cancel, true, monitor), true);
                        // All destination streams were Flush(true)'d and closed above. Confirm the
                        // complete inventory before independently reopening every destination file.
                        Compare(entries, Scan(stage, cancel, false, monitor), false);
                        foreach (Entry e in entries)
                        {
                            if (e.Directory) continue;
                            cancel.ThrowIfCancellationRequested();
                            monitor.Report("校验", e, 0, true);
                            monitor.Verify(true);
                            string dst = System.IO.Path.Combine(stage, e.Path);
                            CheckParents(dst);
                            if (HashFile(dst, e, cancel, monitor) != e.Hash)
                                throw new IOException("目标 SHA256 校验失败：" + e.Path);
                            monitor.Report("校验", e, e.Length, true);
                        }
                        Compare(entries, Scan(source, cancel, true, monitor), true);
                        Compare(entries, Scan(stage, cancel, false, monitor), false);
                        list.WriteLine("# 完成：" + result.Files + " 个文件，" + result.Bytes + " 字节；所有文件目标独立读取 SHA256 一致。");
                    }
                    cancel.ThrowIfCancellationRequested();
                    CheckVolume(source, volume);
                    journal.WriteLine(DateTime.UtcNow.ToString("o") + " 校验通过，正在发布。");
                    monitor.Verify(true);
                    cancel.ThrowIfCancellationRequested();
                    // Both moves refuse to overwrite. If the second move fails, no successful manifest is published.
                    System.IO.Directory.Move(stage, final);
                    File.Move(partial, manifest);
                    return result;
                }
                catch (Exception ex)
                {
                    try { journal.WriteLine(DateTime.UtcNow.ToString("o") + " 未完成：" + ex.ToString()); }
                    catch { /* Preserve the original failure, including disk-full errors. */ }
                    throw;
                }
            }
        }

        private static string Full(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("目录不能为空。");
            string full = System.IO.Path.GetFullPath(path);
            string root = System.IO.Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd('\\', '/');
        }

        private static bool Contains(string parent, string child)
        {
            return String.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckParents(string path)
        {
            string current = path;
            while (!String.IsNullOrEmpty(current))
            {
                // GetAttributes also detects dangling reparse points where Exists may return false.
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("不支持重解析点或目录链接：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = System.IO.Path.GetDirectoryName(current);
            }
        }

        private static List<Entry> Scan(string root, CancellationToken cancel, bool rejectSpecial, TransferMonitor monitor)
        {
            CheckParents(root);
            List<Entry> result = new List<Entry>();
            result.Add(new Entry { Path = "", Directory = true });
            bool dvdVideo = rejectSpecial && ValidateDvdVideo(root);
            ScanDirectory(root, "", result, cancel, rejectSpecial, dvdVideo, monitor);
            result.Sort(delegate(Entry a, Entry b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path); });
            return result;
        }

        private static void ScanDirectory(string root, string relative, List<Entry> entries, CancellationToken cancel, bool rejectSpecial, bool dvdVideo, TransferMonitor monitor)
        {
            cancel.ThrowIfCancellationRequested();
            monitor.Verify(false);
            string folder = relative.Length == 0 ? root : System.IO.Path.Combine(root, relative);
            CheckParents(folder);
            foreach (string path in System.IO.Directory.GetFileSystemEntries(folder))
            {
                cancel.ThrowIfCancellationRequested();
                monitor.Verify(false);
                string name = System.IO.Path.GetFileName(path);
                string rel = relative.Length == 0 ? name : System.IO.Path.Combine(relative, name);
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持重解析点：" + rel);
                if (rejectSpecial && relative.Length == 0 && Special(name, path, dvdVideo))
                    throw new IOException("此工具不支持此蓝光/VCD/音频盘或待复查的影碟结构：" + name);
                bool directory = (attrs & FileAttributes.Directory) != 0;
                Entry entry = new Entry { Path = rel, Directory = directory };
                if (!directory)
                {
                    FileInfo info = new FileInfo(path);
                    entry.Length = info.Length;
                    entry.Ticks = info.LastWriteTimeUtc.Ticks;
                }
                entries.Add(entry);
                if (directory) ScanDirectory(root, rel, entries, cancel, rejectSpecial, dvdVideo, monitor);
            }
        }

        private static bool Special(string name, string path, bool dvdVideo)
        {
            if (dvdVideo && String.Equals(name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase)) return false;
            if (dvdVideo && String.Equals(name, "AUDIO_TS", StringComparison.OrdinalIgnoreCase) &&
                System.IO.Directory.Exists(path) && System.IO.Directory.GetFileSystemEntries(path).Length == 0) return false;
            string[] names = { "VIDEO_TS", "AUDIO_TS", "BDMV", "CERTIFICATE", "VCD", "SVCD", "MPEGAV", "MPEG2", "CDDA", "CDPLUS" };
            foreach (string special in names) if (String.Equals(name, special, StringComparison.OrdinalIgnoreCase)) return true;
            return String.Equals(System.IO.Path.GetExtension(name), ".cda", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidateDvdVideo(string root)
        {
            foreach (string path in System.IO.Directory.GetFileSystemEntries(root))
            {
                if (!String.Equals(System.IO.Path.GetFileName(path), "VIDEO_TS", StringComparison.OrdinalIgnoreCase)) continue;
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0)
                    throw new IOException("VIDEO_TS 不是普通目录，影碟结构待复查。");
                bool ifo = false, vob = false;
                foreach (string file in System.IO.Directory.GetFiles(path))
                {
                    if (String.Equals(System.IO.Path.GetFileName(file), "VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)) ifo = true;
                    if (String.Equals(System.IO.Path.GetExtension(file), ".VOB", StringComparison.OrdinalIgnoreCase)) vob = true;
                }
                if (!ifo || !vob) throw new IOException("VIDEO_TS 缺少 VIDEO_TS.IFO 或 VOB 文件，影碟结构待复查。");
                return true;
            }
            return false;
        }

        private static void CheckFreeSpace(string job, long totalBytes)
        {
            long free;
            try
            {
                DriveInfo drive = new DriveInfo(System.IO.Path.GetPathRoot(job));
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) return;
                free = drive.AvailableFreeSpace;
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (ArgumentException) { return; }
            catch (System.Security.SecurityException) { return; }
            if (free < totalBytes)
                throw new IOException("目标硬盘可用空间不足：需要至少 " + totalBytes + " 字节，可用 " + free + " 字节。");
        }

        private static void MatchFile(string path, Entry expected)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.Length || info.LastWriteTimeUtc.Ticks != expected.Ticks)
                throw new IOException("来源文件在复制期间发生变化：" + expected.Path);
        }

        private static string HashFile(string path, Entry entry, CancellationToken cancel, TransferMonitor monitor)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[1024 * 1024];
                int n;
                long bytes = 0;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    monitor.Verify(false);
                    n = stream.Read(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    sha.TransformBlock(buffer, 0, n, buffer, 0);
                    bytes += n;
                    monitor.Report("校验", entry, bytes, false);
                }
                if (bytes != entry.Length) throw new IOException("目标文件长度发生变化：" + entry.Path);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static void Compare(List<Entry> original, List<Entry> actual, bool source)
        {
            if (original.Count != actual.Count) throw new IOException("复制期间目录/文件清单发生变化。");
            for (int i = 0; i < original.Count; i++)
            {
                Entry a = original[i], b = actual[i];
                if (!String.Equals(a.Path, b.Path, StringComparison.Ordinal) || a.Directory != b.Directory ||
                    (!a.Directory && (a.Length != b.Length || (source && a.Ticks != b.Ticks))))
                    throw new IOException("复制尾端清单核对失败：" + a.Path);
            }
        }

        private static uint Volume(string path)
        {
            uint serial, max, flags;
            if (!GetVolumeInformation(System.IO.Path.GetPathRoot(path), null, 0, out serial, out max, out flags, null, 0))
                throw new IOException("无法读取来源卷信息。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return serial;
        }

        private static void CheckVolume(string path, uint expected)
        {
            if (Volume(path) != expected) throw new IOException("来源卷已改变，请勿在复制期间换盘。");
        }

        private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
        private static string Escape(string value) { return value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }
    }
}
