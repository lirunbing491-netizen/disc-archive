using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading;

namespace DiscArchive
{
    internal sealed class RescueResult
    {
        public long Files, Bytes, Failed, ReusedFiles, ReusedBytes;
        public string Report;
    }

    internal sealed class RescueStoppedException : IOException
    {
        public readonly RescueResult Result;
        public RescueStoppedException(string message, Exception cause, RescueResult result) : base(message, cause) { Result = result; }
    }

    internal sealed class RescueCanceledException : OperationCanceledException
    {
        public readonly RescueResult Result;
        public RescueCanceledException(RescueResult result, CancellationToken token) : base("抢救已取消；已完成文件保留。", token) { Result = result; }
    }

    // A file is successful only after EOF, the original length and a durable write agree.
    // Source exceptions are caught only around source operations; output/verify errors escape.
    internal static class RescueCopy
    {
        internal static RescueResult Run(string sourceRoot, string targetRoot, string defaultFolder,
            IDictionary<string, string> topFolders, string discId, string attemptDir,
            Action<string> progress, Action verify, CancellationToken cancel, string continuationDir = null, bool verifySourceForReuse = false, DiscSource media = null)
        {
            return new Worker(sourceRoot, targetRoot, defaultFolder, topFolders, discId, attemptDir, progress, verify, cancel, continuationDir, verifySourceForReuse, media ?? DiscSource.Windows).Run();
        }

        private sealed class Worker
        {
            private const string Coordination = ".discarchive-coordination-v1";
            private readonly string source, target, fallback, attempt, disc;
            private readonly Dictionary<string, string> mapping = new Dictionary<string, string>(ArchivePath.Comparer);
            private readonly Dictionary<string, string> origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Action<string> progress;
            private readonly Action verify;
            private readonly CancellationToken cancel;
            private readonly RescueResult result = new RescueResult();
            private StreamWriter paths, failures, status;
            private string fragments;
            private readonly string continuationDir;
            private readonly bool verifySourceForReuse;
            private readonly DiscSource media;
            private ContinuationJournal journal;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long verifiedAt = -1000, reportedAt = -1000;

            internal Worker(string s, string t, string f, IDictionary<string, string> m, string d, string a, Action<string> p, Action v, CancellationToken c, string continuation, bool verifySource, DiscSource sourceMedia)
            {
                media = sourceMedia; source = Full(s); target = Full(t); attempt = Full(a); Name(f); ProjectName(f); fallback = f;
                continuationDir = continuation; verifySourceForReuse = verifySource;
                disc = SafeDisc(d); progress = p; verify = v; cancel = c;
                if (Within(source, target) || Within(target, source) || Within(source, attempt) || Within(attempt, source))
                    throw new IOException("来源不能与目标或抢救记录目录重叠。");
                SourceParents(source); Parents(target); Parents(attempt);
                if ((media.Attributes(source) & FileAttributes.Directory) == 0 || !Directory.Exists(target)) throw new IOException("来源或目标目录不存在。");
                if (m != null) foreach (KeyValuePair<string, string> item in m) { Name(item.Key); Name(item.Value); ProjectName(item.Value); mapping.Add(item.Key, item.Value); }
            }

            internal RescueResult Run()
            {
                using (journal = continuationDir == null ? null : new ContinuationJournal(continuationDir, source, target))
                { return RunCore(); }
            }

            private RescueResult RunCore()
            {
                Directory.CreateDirectory(attempt);
                // Every run owns a unique report area, even when the caller reuses an attempt directory.
                string records = Path.Combine(attempt, "抢救记录-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(records);
                result.Report = Path.Combine(records, "进度.tsv");
                fragments = Path.Combine(records, "未完成残片"); Directory.CreateDirectory(fragments);
                using (paths = Writer(Path.Combine(records, "路径清单.tsv")))
                using (failures = Writer(Path.Combine(records, "失败清单.tsv")))
                using (status = Writer(result.Report))
                {
                    paths.WriteLine("类型\t来源\t目标\t说明"); failures.WriteLine("来源\t目标\t错误\t残片");
                    status.WriteLine("状态\t完整文件\t完整字节\t失败条目\t说明\t时间提示\t沿用文件\t沿用字节");
                    try
                    {
                        Check(); Save("进行中", "按可见文件抢救；不代表影碟可播放或音轨提取成功。");
                        Enumerate(source, delegate(string entry) {
                            string folder; bool explicitMapping = mapping.TryGetValue(Path.GetFileName(entry), out folder);
                            if (!explicitMapping) folder = fallback;
                            string project = Path.Combine(target, folder);
                            if (explicitMapping && String.Equals(Path.GetFileName(entry), folder, StringComparison.OrdinalIgnoreCase) && SourceDirectory(entry))
                            {
                                MapProjectDirectory(entry, project);
                                return;
                            }
                            string dst;
                            ContinuationJournal.Entry known = journal == null ? null : journal.Find(entry);
                            if (known != null) { Visit(entry, journal.Destination(known)); return; }
                            using (AllocationLock())
                            {
                                Parents(project); Directory.CreateDirectory(project);
                                dst = Path.Combine(project, Path.GetFileName(entry));
                                if (Conflict(dst))
                                {
                                    dst = Path.Combine(Origin(project), Path.GetFileName(entry));
                                    if (Claimed(dst) || Exists(dst)) dst = Path.Combine(NewOrigin(project), Path.GetFileName(entry));
                                }
                                Claim(dst);
                            }
                            Visit(entry, dst);
                        }, target);
                        if (journal != null) foreach (KeyValuePair<string, ContinuationJournal.Entry> missing in journal.MissingFiles())
                            Fail(missing.Key, journal.Destination(missing.Value), "旧回执条目本轮缺失，保留旧物并需复查；本轮未确认该文件。", "");
                        Check(); Save(result.Files == 0 ? "未抢救到文件" : "完成", result.Files == 0 ? "没有抢救到完整可见文件；音频轨道、不可见视频轨道及不可读取内容未提取，请检查失败清单。" : result.Failed == 0 ? "可见文件读取完成；未做试播。" : "部分条目失败，见失败清单。");
                        return result;
                    }
                    catch (OperationCanceledException e)
                    {
                        if (!cancel.IsCancellationRequested) { TrySave("已停止", e.Message); throw new RescueStoppedException("校验或回调中止：" + e.Message, e, result); }
                        TrySave("已取消", "已完成文件保留。"); throw new RescueCanceledException(result, cancel);
                    }
                    catch (Exception e) { TrySave("已停止", e.Message); throw new RescueStoppedException("抢救已停止：" + e.Message, e, result); }
                }
            }

            private bool Conflict(string dst)
            { return Claimed(dst) || Exists(dst) || Reserved(Path.GetFileName(dst)) || Within(dst, attempt) || Within(attempt, dst); }

            private string Origin(string project)
            {
                string origin;
                if (origins.TryGetValue(project, out origin)) return origin;
                origin = NewOrigin(project); origins.Add(project, origin); return origin;
            }

            private string NewOrigin(string project)
            {
                string stem = Path.Combine(project, "来源-" + disc); string origin = stem; int suffix = 2;
                while (Claimed(origin) || Exists(origin) || Within(origin, attempt) || Within(attempt, origin)) origin = stem + "-" + suffix++;
                Parents(origin); Claim(origin); Directory.CreateDirectory(origin);
                return origin;
            }

            private static void ProjectName(string value)
            {
                if (String.Equals(value, Coordination, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("项目名与并行抢救协调目录重名。");
            }

            // The share server enforces this handle, including across different computers.
            // Never delete the lock file: deleting/recreating it could produce two lock identities.
            // Keep this handle only during allocation, never during source reads or callbacks.
            private FileStream AllocationLock()
            {
                Check();
                string area = Path.Combine(target, Coordination); Parents(area); Directory.CreateDirectory(area);
                string path = Path.Combine(area, "allocation.lock"); Parents(path);
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException e)
                    {
                        if (!ArchivePath.IsSharingViolation(e)) throw;
                        if (cancel.WaitHandle.WaitOne(50)) cancel.ThrowIfCancellationRequested();
                        Check(false);
                    }
                }
            }

            private string ClaimPath(string path)
            {
                // Claims survive cancellation/crashes: an incomplete old tree must never acquire
                // another disc's files. A retry gets a fresh origin instead of reclaiming it.
                string key = Path.GetFullPath(path).Substring(target.Length).ToUpperInvariant();
#if MAC_PORT
                // Match the existing Windows claim filenames on the same backup disk.
                key=key.Replace('/', '\\');
#endif
                // Use a relative key so different drive mappings of the same share agree.
                byte[] digest;
                using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(Path.Combine(target, Coordination), BitConverter.ToString(digest).Replace("-", "") + ".claim");
            }
            private bool Claimed(string path) { string claim = ClaimPath(path); Parents(claim); return Exists(claim); }
            private void Claim(string path)
            {
                Parents(path); string claim = ClaimPath(path); Parents(claim);
                using (FileStream file = new FileStream(claim, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes("DiscArchive path claim v1\r\n" + disc + "\r\n" + path);
                    file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
            }

            private void SourceParents(string path)
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (full.EndsWith(":")) full += Path.DirectorySeparatorChar;
                if (!Within(source, full)) throw new IOException("来源路线越界：" + path);
                if (media.Native) { Parents(full); return; }
                string current = full;
                while (Within(source, current))
                {
                    if ((media.Attributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝来源重解析点：" + current);
                    if (String.Equals(Full(current), source, ArchivePath.Comparison)) break;
                    current = Path.GetDirectoryName(current);
                }
            }

            private bool SourceDirectory(string src)
            {
                try { SourceParents(src); return (media.Attributes(src) & FileAttributes.Directory) != 0; }
                catch (Exception e) { if (!SourceError(e)) throw; return false; }
            }

            private void MapProjectDirectory(string src, string project)
            {
                Check(); SourceParents(src); Parents(project);
                ContinuationJournal.Entry known = journal == null ? null : journal.Find(src);
                if (known != null && known.Directory)
                {
                    string routed = journal.Destination(known);
                    if (File.Exists(routed)) throw new IOException("续拷项目目录已被文件占用。");
                    Directory.CreateDirectory(routed);
                    Enumerate(src, delegate(string child) {
                        string childDestination = Path.Combine(routed, Path.GetFileName(child));
                        if (journal.Find(child) == null)
                        {
                            using (AllocationLock())
                            {
                                if (Conflict(childDestination)) childDestination = FreshDestinationLocked(childDestination);
                                else Claim(childDestination);
                            }
                        }
                        Visit(child, childDestination);
                    }, routed);
                    Check(); ValidateOutput(routed, true, 0); return;
                }
                List<string> children = new List<string>();
                Enumerate(src, delegate(string child) { children.Add(child); }, project);
                bool collision = false; string destination;
                using (AllocationLock())
                {
                    Parents(project); Directory.CreateDirectory(project);
                    foreach (string child in children) if (Conflict(Path.Combine(project, Path.GetFileName(child)))) { collision = true; break; }
                    destination = collision ? Origin(project) : project;
                    // An origin may already contain another top-level entry from this disc.
                    // Reserve the entire mapped group's children together before releasing the lock.
                    if (collision)
                    {
                        bool sourceCollision = false;
                        foreach (string child in children) if (Claimed(Path.Combine(destination, Path.GetFileName(child))) || Exists(Path.Combine(destination, Path.GetFileName(child)))) { sourceCollision = true; break; }
                        if (sourceCollision)
                        {
                            string stem = Path.Combine(destination, Path.GetFileName(src)); destination = stem; int suffix = 2;
                            while (Claimed(destination) || Exists(destination)) destination = stem + "-" + suffix++;
                            Parents(destination); Claim(destination); Directory.CreateDirectory(destination);
                        }
                    }
                    foreach (string child in children) Claim(Path.Combine(destination, Path.GetFileName(child)));
                }
                if (journal != null) journal.Route(src, destination, true);
                paths.WriteLine("项目目录\t" + E(src) + "\t" + E(destination) + "\t" + (collision ? "同组子项整体隔离" : "源顶层项目目录对应目标项目根"));
                foreach (string child in children) Visit(child, Path.Combine(destination, Path.GetFileName(child)));
                Check(); ValidateOutput(destination, true, 0);
            }

            private void Check(bool force = true)
            {
                cancel.ThrowIfCancellationRequested();
                if (!force && clock.ElapsedMilliseconds - verifiedAt < 1000) return;
                if (!Directory.Exists(target)) throw new IOException("目标硬盘已断开。");
                Parents(target); Parents(attempt);
                if (verify != null) verify();
                if (!Directory.Exists(target)) throw new IOException("目标硬盘已断开。");
                verifiedAt = clock.ElapsedMilliseconds;
                cancel.ThrowIfCancellationRequested();
            }

            private void Report(string src, long bytes, long length, bool force)
            {
                if (progress == null || (!force && clock.ElapsedMilliseconds - reportedAt < 1000)) return;
                reportedAt = clock.ElapsedMilliseconds;
                progress("抢救：" + src + "  已读 " + bytes + " / " + length + " 字节");
                cancel.ThrowIfCancellationRequested();
            }

            private static void ValidateOutput(string path, bool directory, long length)
            {
                Parents(path);
                FileAttributes attrs = File.GetAttributes(path);
                if (((attrs & FileAttributes.Directory) != 0) != directory)
                    throw new IOException("目标条目类型发生变化：" + path);
                if (!directory && new FileInfo(path).Length != length)
                    throw new IOException("目标文件长度发生变化：" + path);
            }

            private void Enumerate(string src, Action<string> action, string dst)
            {
                Check();
                IEnumerator<string> iterator = null;
                try { iterator = media.Entries(src).GetEnumerator(); }
                catch (Exception e) { if (!SourceError(e)) throw; Fail(src, dst, "目录枚举失败：" + e.Message, ""); return; }
                using (iterator)
                {
                    while (true)
                    {
                        Check(false); string entry;
                        try { if (!iterator.MoveNext()) break; entry = iterator.Current; }
                        catch (Exception e) { if (!SourceError(e)) throw; Fail(src, dst, "目录枚举中断：" + e.Message, ""); break; }
                        action(entry);
                    }
                }
            }

            private void Visit(string src, string dst)
            {
                Check(); Parents(dst);
                ContinuationJournal.Entry prior = journal == null ? null : journal.Find(src);
                if (prior != null) dst = journal.Destination(prior);
                paths.WriteLine("条目\t" + E(src) + "\t" + E(dst) + "\t计划");
                FileAttributes attrs;
                try { SourceParents(src); attrs = media.Attributes(src); }
                catch (Exception e) { if (!SourceError(e)) throw; Fail(src, dst, e.Message, ""); return; }
                if (journal != null) journal.Encounter(src, (attrs & FileAttributes.Directory) != 0);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (prior == null || !prior.Directory)
                    {
                        if (Exists(dst)) dst = FreshDestination(dst);
                        if (journal != null) journal.Route(src, dst, true);
                    }
                    if (File.Exists(dst)) throw new IOException("续拷目录被文件占用：" + dst);
                    Directory.CreateDirectory(dst);
                    paths.WriteLine("目录\t" + E(src) + "\t" + E(dst) + "\t已创建");
                    string leaf = Path.GetFileName(src);
                    if (leaf.Equals("VCD", StringComparison.OrdinalIgnoreCase) || leaf.Equals("MPEGAV", StringComparison.OrdinalIgnoreCase) || leaf.Equals("SVCD", StringComparison.OrdinalIgnoreCase))
                        Save("说明", "VCD/SVCD 仅抢救可见且可读取的文件；无法访问的轨道不在此清单内，未验证播放或完整音视频内容。");
                    Enumerate(src, delegate(string entry) { Visit(entry, Path.Combine(dst, Path.GetFileName(entry))); }, dst);
                    Check(); ValidateOutput(dst, true, 0);
                    return;
                }
                if (String.Equals(Path.GetExtension(src), ".cda", StringComparison.OrdinalIgnoreCase))
                { Fail(src, dst, "CDA 是音轨占位符，未提取音频，不能算抢救成功。", ""); return; }
                Check();
                if (prior != null && File.Exists(dst))
                {
                    long oldLength = new FileInfo(dst).Length;
                    string digest = ContinuationJournal.HashFile(dst, cancel, delegate { Check(false); });
                    bool reusable = prior.Complete && prior.Length == oldLength && prior.Hash == digest;
                    if ((!prior.Complete && prior.Hash == "legacy") || (reusable && verifySourceForReuse))
                    {
                        try { reusable = media.Length(src) == oldLength && ContinuationJournal.HashFile(src, cancel, delegate { Check(false); }, media) == digest; }
                        catch (Exception e) { if (!SourceError(e)) throw; Check(); Fail(src, dst, e.Message, ""); return; }
                    }
                    if (reusable)
                    {
                        journal.Complete(src, dst, oldLength, digest);
                        result.Files++; result.Bytes += oldLength; result.ReusedFiles++; result.ReusedBytes += oldLength;
                        paths.WriteLine("文件\t" + E(src) + "\t" + E(dst) + "\tSHA256核实沿用 " + digest); Save("进行中", src); return;
                    }
                }
                if (Exists(dst)) dst = FreshDestination(dst);
                if (journal != null) journal.Route(src, dst, false);
                Stream input = null; long length;
                try { SourceParents(src); length = media.Length(src); input = media.OpenRead(src); if (input.Length != length) throw new IOException("来源长度发生变化。"); }
                catch (Exception e) { if (input != null) input.Dispose(); if (!SourceError(e)) throw; Fail(src, dst, e.Message, ""); return; }
                try { Report(src, 0, length, true); }
                catch { input.Dispose(); throw; }
                string fragment = Path.Combine(fragments, Guid.NewGuid().ToString("N") + ".partial");
                paths.WriteLine("残片\t" + E(src) + "\t" + E(dst) + "\t" + E(fragment));
                long read = 0; Exception readError = null; string copiedHash;
                using (SHA256 hash = SHA256.Create())
                using (input)
                using (FileStream output = new FileStream(fragment, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        Check(false); int n;
                        try { n = input.Read(buffer, 0, buffer.Length); }
                        catch (Exception e) { if (!SourceError(e)) throw; readError = e; break; }
                        if (n == 0) break;
                        output.Write(buffer, 0, n); hash.TransformBlock(buffer, 0, n, buffer, 0); read += n;
                        Report(src, read, length, false);
                        if (read > length) { readError = new IOException("来源长度发生变化。"); break; }
                    }
                    output.Flush(true);
                    hash.TransformFinalBlock(new byte[0], 0, 0); copiedHash = BitConverter.ToString(hash.Hash).Replace("-", "");
                    if (read != length && readError == null) readError = new IOException("未读到源文件完整长度。");
                }
                if (readError != null) { Check(); Fail(src, dst, readError.Message, fragment); return; }
                Check(); Parents(dst);
                ValidateOutput(fragment, false, read);
                if (Exists(dst)) throw new IOException("目标已存在，停止以防覆盖：" + dst);
                // Move publishes without overwriting. Cross-volume records use a separate target temporary file.
                if (String.Equals(Path.GetPathRoot(fragment), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase)) File.Move(fragment, dst);
                else
                {
                    string staged = Path.Combine(Path.GetDirectoryName(dst), ".抢救暂存-" + Guid.NewGuid().ToString("N"));
                    using (FileStream from = new FileStream(fragment, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream to = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1024 * 1024]; int n;
                        while ((n = from.Read(buffer, 0, buffer.Length)) != 0) { Check(false); to.Write(buffer, 0, n); }
                        to.Flush(true);
                    }
                    Check(); ValidateOutput(staged, false, read); Parents(dst); File.Move(staged, dst);
                    // Keep the recovery copy: deleting user-visible files is never necessary here.
                }
                result.Files++; result.Bytes += read;
                if (journal != null) journal.Complete(src, dst, read, copiedHash);
                paths.WriteLine("文件\t" + E(src) + "\t" + E(dst) + "\t完整 " + read + " 字节"); Save("进行中", src);
                Report(src, read, length, true);
            }

            private string FreshDestination(string dst)
            {
                using (AllocationLock())
                {
                    return FreshDestinationLocked(dst);
                }
            }

            private string FreshDestinationLocked(string dst)
            {
                string dir=Path.GetDirectoryName(dst), name=Path.GetFileNameWithoutExtension(dst), extension=Path.GetExtension(dst);
                for(int next=1;;next++)
                {
                    string suffix=next==1 ? ".重新抢救" : ".重新抢救-"+next;
                    int room=255-extension.Length-suffix.Length;
                    if(room<1) throw new IOException("无法为超长扩展名生成安全的补拷副本："+dst);
                    string head=name.Length>room ? name.Substring(0,room) : name;
                    string fresh=Path.Combine(dir,head+suffix+extension);
                    if(Exists(fresh) || Claimed(fresh)) continue;
                    Claim(fresh); return fresh;
                }
            }

            private void Fail(string src, string dst, string message, string fragment)
            {
                // A source error must not hide a changed disc, cancellation, or a lost output volume.
                Check(); result.Failed++;
                if (journal != null) journal.Uncertain(src);
                failures.WriteLine(E(src) + "\t" + E(dst) + "\t" + E(message) + "\t" + E(fragment));
                paths.WriteLine("失败\t" + E(src) + "\t" + E(dst) + "\t" + E(message)); Save("进行中", message);
            }
            private void Save(string state, string note) { status.WriteLine(state + "\t" + result.Files + "\t" + result.Bytes + "\t" + result.Failed + "\t" + E(note)+"\t0\t"+result.ReusedFiles+"\t"+result.ReusedBytes); }
            private void TrySave(string state, string note) { try { Save(state, note); } catch { /* Result still travels in the exception if the report disk was lost. */ } }
        }

        private static StreamWriter Writer(string path) { StreamWriter w = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)); w.AutoFlush = true; return w; }
        private static bool SourceError(Exception e) { return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException; }
        private static string Full(string value) { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        private static bool Within(string parent, string child) { return Full(child).StartsWith(Full(parent), ArchivePath.Comparison); }
        private static bool Exists(string p) { return File.Exists(p) || Directory.Exists(p); }
        private static string E(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }
        private static void Name(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" ")) throw new ArgumentException("项目名必须是合法单段目录名。");
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9')) throw new ArgumentException("项目名不能使用系统设备名。");
        }
        private static string SafeDisc(string value) { if (String.IsNullOrEmpty(value)) return "未知盘"; StringBuilder s = new StringBuilder(); foreach (char c in value) s.Append(Char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'); return s.ToString(0, Math.Min(60, s.Length)); }
        private static bool Reserved(string name)
        {
            return name.Equals("DiscArchive", StringComparison.OrdinalIgnoreCase) || name.Equals("原盘文件", StringComparison.OrdinalIgnoreCase) || name.StartsWith("未完成", StringComparison.OrdinalIgnoreCase) || name.StartsWith("抢救", StringComparison.OrdinalIgnoreCase) || name.StartsWith("photo", StringComparison.OrdinalIgnoreCase) || name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".discarchive", StringComparison.OrdinalIgnoreCase) || name.IndexOf("清单", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("日志", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("照片", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("record.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("登记信息.txt", StringComparison.OrdinalIgnoreCase);
        }
        private static void Parents(string path)
        {
            string current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (current.EndsWith(":")) current += Path.DirectorySeparatorChar;
            while (!String.IsNullOrEmpty(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝重解析点：" + current); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
