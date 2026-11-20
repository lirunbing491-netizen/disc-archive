using System;
using System.Collections.Generic;

#if !MAC_PORT
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
#endif
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace DiscArchive
{
    public class Entry
    {
        public string Id, Title, Project, Status, Note, Photo, JobDir, TargetRoot, TargetId, Drive, Created;
        public string TargetName;
        public string RegistrationId;
        public string TitleState;
        public string OriginalPhoto;
        public string ProjectHint, DiscLabel, PhotoText, VisionNote, NamingReason, Report;
        public string[] ArchiveFolders;
        public string CanonicalId, LatestRunId, IdentityEvidence, ExistingProject;
        public string SourceFormat, SourceCoverage, CloseWarning;
        public long ReusedFiles, ReusedBytes;
        public bool PendingLabelSync;
        public long Failed, Warnings;
        public long Files, Bytes;
        internal Entry ForAttempt() { return (Entry)MemberwiseClone(); }
    }

    public class Catalog
    {
        private readonly string stateDir;
        private readonly string database;
        private readonly string sequenceDir;
        private const string Marker = ".discarchive-target.id";
        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(List<Entry>));
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(true);

        public Catalog(string stateDir) : this(stateDir, stateDir) { }

        internal Catalog(string stateDir, string sequenceDir)
        {
            if (String.IsNullOrWhiteSpace(stateDir)) throw new ArgumentException("登记目录不能为空。");
            this.stateDir = Path.GetFullPath(stateDir);
            Directory.CreateDirectory(this.stateDir);
            database = Path.Combine(this.stateDir, "catalog.xml");
            this.sequenceDir = sequenceDir;
        }

        public List<Entry> List()
        {
            using (Acquire(Path.Combine(stateDir, "catalog.lock"))) return Read();
        }

        public Entry Create(string photoPath, string targetRoot, string drive, string project, string expectedTargetId = null)
        {
            if (!File.Exists(photoPath)) throw new FileNotFoundException("找不到登记照片。", photoPath);
            if (String.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
                throw new DirectoryNotFoundException("目标目录不存在或硬盘未连接。");
            string root = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (FileStream original = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
            // Decode and validate before allocating any record or creating archive directories.
            byte[] thumbnail = MakeThumbnail(original);
            original.Position = 0;
            using (Acquire(Path.Combine(stateDir, "catalog.lock")))
            {
                List<Entry> entries = Read();
                string markerPath = Path.Combine(root, Marker);
                string targetId;
                // The marker lock also coordinates independent catalogs selecting the same disk.
                using (Acquire(Path.Combine(root, ".discarchive-target.lock")))
                {
                    if (!String.IsNullOrEmpty(expectedTargetId) &&
                        (!File.Exists(markerPath) || ReadMarker(markerPath) != expectedTargetId))
                        throw new IOException("目标硬盘身份与已选择的归档位置不符。");
                    if (File.Exists(markerPath)) targetId = ReadMarker(markerPath);
                    else
                    {
                        targetId = Guid.NewGuid().ToString("N");
                        AtomicText(markerPath, targetId);
                    }
                // Keep the target lock until publication: cloned workstation sequences must
                // advance past published IDs instead of racing for the same directory.
                string id, localDir, jobDir;
                do {
                    id = AllocateId();
                    localDir = Path.Combine(stateDir, "entries", id);
                    jobDir = Path.Combine(root, "DiscArchive", id);
                } while (Directory.Exists(jobDir) || File.Exists(jobDir) || Directory.Exists(localDir) || File.Exists(localDir));
                string extension = Path.GetExtension(photoPath);
                string localPhoto = Path.Combine(localDir, "thumbnail.jpg");
                string archivedPhoto = Path.Combine(jobDir, "photo" + extension);
                Entry entry = new Entry {
                    Id = id, RegistrationId = Guid.NewGuid().ToString("N"), Title = id, Project = project ?? "", Status = "等待放盘", Note = "",
                    Photo = localPhoto, OriginalPhoto = archivedPhoto, JobDir = jobDir, TargetRoot = root, TargetId = targetId,
                    TargetName = TargetName(root), ProjectHint=project ?? "", TitleState = "待确认", Drive = drive ?? "",
                    Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                };
                string localPending = localDir + ".pending-" + Guid.NewGuid().ToString("N");
                string remotePending = jobDir + ".pending-" + Guid.NewGuid().ToString("N");
                bool localMoved = false, remoteMoved = false;
                try
                {
                    Directory.CreateDirectory(localPending);
                    Directory.CreateDirectory(remotePending);
                    File.WriteAllBytes(Path.Combine(localPending, "thumbnail.jpg"), thumbnail);
                    using (FileStream destination = new FileStream(Path.Combine(remotePending, "photo" + extension), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        original.CopyTo(destination);
                        destination.Flush(true);
                    }
                    original.Position = 0;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var archive = File.OpenRead(Path.Combine(remotePending, "photo" + extension)))
                    {
                        string sourceHash = Convert.ToBase64String(sha.ComputeHash(original));
                        if (sourceHash != Convert.ToBase64String(sha.ComputeHash(archive)))
                            throw new IOException("盘面原图读回校验失败。请重新登记。");
                    }
                    WriteReplicaTo(entry, remotePending);
                    if (!IsOriginalTarget(entry)) throw new IOException("登记期间目标硬盘发生了变化。");
                    Directory.Move(localPending, localDir);
                    localMoved = true;
                    Directory.Move(remotePending, jobDir);
                    remoteMoved = true;
                    entries.Add(entry);
                    Write(entries);
                    return entry;
                }
                catch
                {
                    // Remove only named files created by this attempt; leave unknown contents alone.
                    CleanAttempt(localMoved ? localDir : localPending, new string[] { "thumbnail.jpg" });
                    if (IsOriginalTarget(entry))
                        CleanAttempt(remoteMoved ? jobDir : remotePending, new string[] { "photo" + extension, "entry.xml", "登记信息.txt" });
                    throw;
                }
                }
            }
            }
        }

        public void Save(Entry e)
        {
            if (e == null) throw new ArgumentNullException("e");
            using (Acquire(Path.Combine(stateDir, "catalog.lock")))
            {
                List<Entry> entries = Read();
                int index = entries.FindIndex(delegate(Entry item) { return item.Id == e.Id; });
                if (index < 0) throw new InvalidOperationException("该盘尚未登记。");
                Entry old = entries[index];
                if (old.TargetId != e.TargetId || old.TargetRoot != e.TargetRoot || old.JobDir != e.JobDir || old.Photo != e.Photo || old.OriginalPhoto != e.OriginalPhoto || old.Created != e.Created || old.RegistrationId != e.RegistrationId)
                    throw new InvalidOperationException("不能修改盘号对应的目标位置、照片或创建时间。");
                entries[index] = e;
                Write(entries);
                // Missing media or a reused drive letter must never prevent a local edit.
                if (IsOriginalTarget(e)) WriteReplica(e);
            }
        }

        public void Edit(Entry e, string title, string project)
        {
            if (e == null) throw new ArgumentNullException("e");
            e.Title = (title ?? "").Trim();
            e.Project = project ?? "";
            e.TitleState = e.Title.Length == 0 ? "待确认" : "已确认";
            SaveLabels(e,true);
        }
        internal void SyncLabels(Entry e) { SaveLabels(e,false); }
        private void SaveLabels(Entry e,bool edited)
        {
            using(Acquire(Path.Combine(stateDir,"catalog.lock")))
            {
                var rows=Read(); var local=rows.Find(x=>x.Id==e.Id && x.RegistrationId==e.RegistrationId && x.TargetId==e.TargetId);
                if(local==null) throw new IOException("未找到本机的原盘登记。");
                if(edited) {
                    local.Title=e.Title; local.Project=e.Project; local.TitleState=e.TitleState;
                    e.PendingLabelSync=local.PendingLabelSync=true; Write(rows);
                }
                if(!local.PendingLabelSync || !IsOriginalTarget(local)) return;
                using(Acquire(Path.Combine(local.TargetRoot,".discarchive-target.lock"))) {
                    var remote=ReadOrigin(local,local.Id);
                    if(remote.RegistrationId!=local.RegistrationId || remote.Created!=local.Created ||
                        (String.IsNullOrEmpty(local.RegistrationId) && remote.Photo!=local.Photo))
                        throw new IOException("目标登记的身份已改变，不能同步标题。");
                    // Another workstation may have completed the original since this local snapshot.
                    // A title edit must not write an older status or report path back to its replica.
                    remote.Title=local.Title; remote.Project=local.Project; remote.TitleState=local.TitleState; remote.PendingLabelSync=false;
                    WriteReplicaTo(remote,local.JobDir,local);
                }
                e.PendingLabelSync=local.PendingLabelSync=false; Write(rows);
            }
        }

        public bool IsOriginalTarget(Entry e)
        {
            if (e == null || String.IsNullOrEmpty(e.TargetId) || String.IsNullOrEmpty(e.TargetRoot)) return false;
            try { return ReadMarker(Path.Combine(e.TargetRoot, Marker)) == e.TargetId; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
        }

        public void Export(string csvPath)
        {
            List<Entry> entries = List();
            StringBuilder csv = new StringBuilder(CsvHeader);
            foreach (Entry e in entries)
            {
                string[] values = { e.Id, e.Title, e.TitleState, e.Project, e.Status, e.Note, e.Photo, e.OriginalPhoto, e.JobDir, e.TargetName, e.TargetRoot, e.TargetId, e.Drive, e.Created, e.Files.ToString(CultureInfo.InvariantCulture), e.Bytes.ToString(CultureInfo.InvariantCulture),e.Failed.ToString(),String.Join(" / ",e.ArchiveFolders??new string[0]),e.DiscLabel,e.PhotoText,e.NamingReason,e.Report,e.Warnings.ToString(CultureInfo.InvariantCulture), e.CanonicalId??e.Id,e.ReusedFiles.ToString(),e.LatestRunId??e.Id };
                for (int i = 0; i < values.Length; i++) { if (i > 0) csv.Append(','); csv.Append(Csv(values[i])); }
                csv.Append("\r\n");
            }
            AtomicText(Path.GetFullPath(csvPath), csv.ToString());
        }

        // Each published per-disc replica is authoritative for target-wide discovery.
        // This deliberately creates no shared catalog and acquires no write lock.
        private const string CsvHeader="盘号,标题,标题确认,项目,状态,备注,照片,原图,登记目录,目标硬盘,目标路径,目标标识,光驱,登记时间,完整文件数,完整字节数,失败条目,项目目录,卷标,盘面候选,命名依据,抢救记录,时间提示,原盘号,已核对沿用文件,最近复核登记\r\n";

        public int ExportTarget(string targetRoot, string csvPath)
        {
            if (String.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
                throw new DirectoryNotFoundException("目标目录不存在或未连接。");
            string root = Path.GetFullPath(targetRoot);
            string archive = Path.Combine(root, "DiscArchive");
            StringBuilder csv = new StringBuilder(CsvHeader);
            int count = 0;
            bool archiveExists;
            try { archiveExists = (File.GetAttributes(archive) & FileAttributes.Directory) != 0;
                if (!archiveExists) throw new IOException("DiscArchive 位置不是目录。"); }
            catch (FileNotFoundException) { archiveExists = false; }
            catch (DirectoryNotFoundException) { archiveExists = false; }
            if (archiveExists)
            {
                CheckNoLinks(archive);
                string[] directories = Directory.GetDirectories(archive);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    string name = Path.GetFileName(directory);
                    int pending = name.LastIndexOf(".pending-", StringComparison.Ordinal);
                    Guid ignored;
                    if (pending > 0 && Guid.TryParseExact(name.Substring(pending + 9), "N", out ignored)) continue;
                    try
                    {
                        CheckNoLinks(directory);
                        string replica = Path.Combine(directory, "entry.xml");
                        CheckNoLinks(replica);
                        List<Entry> records = ReadFile(replica);
                        if (records == null || records.Count != 1 || records[0] == null || records[0].Id != name)
                            throw new IOException("盘号与所在目录不符，或记录数量无效。");
                        Entry e = records[0];
                        List<string> projects = new List<string>();
                        foreach (string folder in e.ArchiveFolders ?? new string[0])
                            projects.Add(CurrentChild(root, folder));
                        string original = "";
                        foreach (string photo in Directory.GetFiles(directory, "photo.*"))
                        {
                            if (Path.GetExtension(photo).Equals(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                            CheckNoLinks(photo);
                            if (original.Length > 0) throw new IOException("盘面原图不唯一，无法确定当前原图路径。");
                            original = photo;
                        }
                        string report = RebaseReport(e, directory);
                        string[] values = { e.Id, e.Title, e.TitleState, e.Project, e.Status, e.Note, original, original, directory,
                            TargetName(root), root, e.TargetId, e.Drive, e.Created,
                            e.Files.ToString(CultureInfo.InvariantCulture), e.Bytes.ToString(CultureInfo.InvariantCulture),
                            e.Failed.ToString(CultureInfo.InvariantCulture), String.Join(" / ", projects.ToArray()),
                            e.DiscLabel, e.PhotoText, e.NamingReason, report, e.Warnings.ToString(CultureInfo.InvariantCulture),
                            e.CanonicalId??e.Id,e.ReusedFiles.ToString(),e.LatestRunId??e.Id };
                        for (int i = 0; i < values.Length; i++) { if (i > 0) csv.Append(','); csv.Append(Csv(values[i])); }
                        csv.Append("\r\n");
                        count++;
                    }
                    catch (Exception error)
                    {
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is InvalidOperationException) && !(error is ArgumentException)) throw;
                        throw new IOException("目标汇总未完成，未写出CSV。无法读取登记目录：" + directory + "。" + error.Message, error);
                    }
                }
            }
            AtomicText(Path.GetFullPath(csvPath), csv.ToString());
            return count;
        }

        private static string CurrentChild(string parent, string leaf)
        {
            if (String.IsNullOrWhiteSpace(leaf) || leaf == "." || leaf == ".." || leaf != Path.GetFileName(leaf) ||
                leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || leaf.EndsWith(".") || leaf.EndsWith(" "))
                throw new IOException("共享记录中的目录名称无效。");
            string path = Path.Combine(parent, leaf);
            CheckNoLinks(path);
            return path;
        }

        private static string RebaseReport(Entry e, string currentDirectory)
        {
            if (String.IsNullOrEmpty(e.Report) || String.IsNullOrEmpty(e.JobDir)) return "";
            string prefix = e.JobDir.TrimEnd('\\', '/') + "\\";
            if(!String.IsNullOrEmpty(e.LatestRunId) && e.LatestRunId!=e.Id)
            {
                currentDirectory=CurrentChild(Path.GetDirectoryName(currentDirectory),e.LatestRunId);
                prefix=Path.Combine(e.TargetRoot,"DiscArchive",e.LatestRunId).TrimEnd('\\','/')+"\\";
            }
            string stored = e.Report.Replace('/', '\\');
            if (!stored.StartsWith(prefix.Replace('/', '\\'), e.JobDir.Length>=2 && e.JobDir[1]==':' ? StringComparison.OrdinalIgnoreCase : ArchivePath.Comparison)) return "";
            string result = currentDirectory;
            foreach (string part in stored.Substring(prefix.Length).Split('\\')) result = CurrentChild(result, part);
            return result;
        }

        internal static void CheckNoLinks(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档路径包含目录链接：" + current); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            string first = value.TrimStart();
            if ((first.Length > 0 && "=+-@".IndexOf(first[0]) >= 0) || (value.Length > 0 && (value[0] == '\t' || value[0] == '\r' || value[0] == '\n')))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ReadMarker(string path)
        {
            string value = File.ReadAllText(path).Trim();
            Guid parsed;
            if (!Guid.TryParseExact(value, "N", out parsed)) throw new FormatException("目标硬盘标记损坏。");
            return value;
        }

        private static string TargetName(string root)
        {
            try { string name = new DriveInfo(Path.GetPathRoot(root)).VolumeLabel; if (!String.IsNullOrEmpty(name)) return name; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { } // DriveInfo does not accept ordinary UNC share roots.
            catch (NotSupportedException) { }
            return root;
        }

        private string AllocateId()
        {
            // The application uses one fixed local state directory. The registry identifier
            // distinguishes independently initialized catalogs; no archive is overwritten.
            string folder = sequenceDir;
            Directory.CreateDirectory(folder);
            using (Acquire(Path.Combine(folder, "sequence.lock")))
            {
                string sequence = Path.Combine(folder, "sequence.txt");
                string identityFile = Path.Combine(folder, "registry.id");
                long number = 0;
                if (!File.Exists(sequence) && (File.Exists(identityFile) || File.Exists(sequence + ".bak")))
                    throw new IOException("盘号序列缺失，已停止登记以防复用旧盘号。请保留登记目录并恢复序列文件。");
                if (File.Exists(sequence) && !Int64.TryParse(File.ReadAllText(sequence).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    throw new IOException("盘号序列损坏，已停止登记。");
                number = checked(number + 1);
                AtomicText(sequence, number.ToString(CultureInfo.InvariantCulture));
                string identity;
                if (File.Exists(identityFile)) identity = ReadMarker(identityFile);
                else { identity = Guid.NewGuid().ToString("N"); AtomicText(identityFile, identity); }
                return "DA-" + number.ToString("D6", CultureInfo.InvariantCulture) + "-" + identity.Substring(0, 8).ToUpperInvariant();
            }
        }

        private List<Entry> Read()
        {
            if (!File.Exists(database))
            {
                if (File.Exists(database + ".bak")) return ReadFile(database + ".bak");
                return new List<Entry>();
            }
            try { return ReadFile(database); }
            catch (InvalidOperationException)
            {
                // Fail closed: never silently replace a damaged current catalog with an older one.
                throw new IOException("本地登记文件损坏，请先保留原文件并检查 catalog.xml.bak。");
            }
        }

        private static List<Entry> ReadFile(string path)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                try { return ReadFileOnce(path); }
                catch (IOException error)
                {
                    // SMB ReplaceFile may briefly hold an exclusive server handle; file
                    // systems using the rename fallback also have a short publication gap.
                    // Persistent missing or unreadable records still fail explicitly.
                    int code = error.HResult & 0xffff;
                    if ((code != 2 && code != 3 && code != 32 && code != 33) || DateTime.UtcNow >= deadline) throw;
                    Thread.Sleep(20);
                }
            }
        }

        private static List<Entry> ReadFileOnce(string path)
        {
            System.Xml.XmlReaderSettings settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(stream, settings))
                return (List<Entry>)Serializer.Deserialize(reader);
        }

        private void Write(List<Entry> entries)
        {
            AtomicWrite(database, delegate(Stream stream) { Serializer.Serialize(stream, entries); });
        }

        private void WriteReplica(Entry e)
        {
            using (Acquire(Path.Combine(e.TargetRoot, ".discarchive-target.lock")))
            {
                string path = Path.Combine(e.JobDir, "entry.xml");
                if (!File.Exists(path)) throw new IOException("目标登记复本缺失，已停止写入以防覆盖其他工位。");
                List<Entry> existing = ReadFile(path);
                if (existing == null || existing.Count != 1 || existing[0] == null) throw new IOException("目标登记复本无效。");
                Entry owner = existing[0];
                if (owner.Id != e.Id || owner.TargetId != e.TargetId || owner.RegistrationId != e.RegistrationId ||
                    (String.IsNullOrEmpty(e.RegistrationId) && (owner.Created != e.Created || owner.Photo != e.Photo || owner.JobDir != e.JobDir || owner.TargetRoot != e.TargetRoot || owner.OriginalPhoto != e.OriginalPhoto)))
                    throw new IOException("目标登记复本属于其他登记，已停止写入。");
                WriteReplicaTo(e, e.JobDir);
            }
        }

        internal static string DiscFolder(string targetRoot,string id)
        {
            return CurrentChild(Path.Combine(Path.GetFullPath(targetRoot),"DiscArchive"),id);
        }

        internal Entry ReadOrigin(Entry current,string originalId)
        {
            if(!IsOriginalTarget(current)) throw new IOException("无法确认原备份硬盘。");
            string file=CurrentChild(DiscFolder(current.TargetRoot,originalId),"entry.xml");
            List<Entry> rows=ReadFile(file);
            if(rows==null || rows.Count!=1 || rows[0]==null || rows[0].Id!=originalId || rows[0].TargetId!=current.TargetId)
                throw new IOException("所选旧盘的登记与当前归档位置不一致。");
            return rows[0];
        }
        internal Entry LookupOriginal(string root,string id)
        {
            var access=new Entry { TargetRoot=root,TargetId=ReadMarker(Path.Combine(root,Marker)) };
            var entry=ReadOrigin(access,id);
            return String.IsNullOrEmpty(entry.CanonicalId) || entry.CanonicalId==entry.Id
                ? entry : ReadOrigin(access,entry.CanonicalId);
        }
        internal static string OriginalPhotoAt(string targetRoot,Entry owner)
        {
            string directory=DiscFolder(targetRoot,owner.Id);
            string stored=(owner.OriginalPhoto??owner.Photo??"").Replace('\\','/');
            string leaf=stored.Substring(stored.LastIndexOf('/')+1);
            string[] photos=Directory.GetFiles(directory,"photo.*");
            foreach(string p in photos) {
                if(String.Equals(leaf,Path.GetFileName(p),ArchivePath.Comparison)) {
                    CheckNoLinks(p); return p;
                }
            }
            throw new IOException("原盘面照片未能在所选备份盘的登记目录找到。可改为重新拍照登记。");
        }

        // Keep the new insertion/photo as history while recording the most recent
        // successful reconciliation against the original disc number.
        internal void CompleteOrigin(Entry run,Entry origin)
        {
            if(run.Id==origin.Id) return;
            using(Acquire(Path.Combine(stateDir,"catalog.lock")))
            using(Acquire(Path.Combine(run.TargetRoot,".discarchive-target.lock")))
            {
                Entry remote=ReadOrigin(run,origin.Id);
                if(remote.RegistrationId!=origin.RegistrationId || remote.Created!=origin.Created)
                    throw new IOException("原盘登记在续导期间改变，已保留本次回执。");
                CopyOutcome(remote,run);
                WriteReplicaTo(remote,DiscFolder(run.TargetRoot,origin.Id),run);
                var records=Read();
                var local=records.Find(x=>x.Id==origin.Id && x.TargetId==origin.TargetId &&
                    x.RegistrationId==origin.RegistrationId && x.Created==origin.Created);
                if(local!=null) { CopyOutcome(local,run); Write(records); }
            }
        }
        private static void CopyOutcome(Entry dest,Entry run)
        {
            dest.Status=run.Status; dest.Files=run.Files; dest.Bytes=run.Bytes; dest.Failed=run.Failed;
            dest.ReusedFiles=run.ReusedFiles; dest.ReusedBytes=run.ReusedBytes;
            dest.SourceFormat=run.SourceFormat;dest.SourceCoverage=run.SourceCoverage;dest.CloseWarning=run.CloseWarning;
            dest.LatestRunId=run.Id; dest.ArchiveFolders=run.ArchiveFolders;
            if(dest.TitleState!="已确认") { dest.Project=run.Project; dest.Title=run.Project; }
            dest.Note="最近一次核对登记 "+run.Id+"。"+run.Note;
            string prefix=run.JobDir.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
            if(!String.IsNullOrEmpty(run.Report) && run.Report.StartsWith(prefix,ArchivePath.Comparison))
                dest.Report=Path.Combine(dest.TargetRoot,"DiscArchive",run.Id,run.Report.Substring(prefix.Length));
        }

        private void WriteReplicaTo(Entry e, string directory,Entry targetAccess=null)
        {
            if (!IsOriginalTarget(targetAccess??e)) throw new IOException("目标硬盘已离线或不是原目标硬盘。");
            Directory.CreateDirectory(directory);
            AtomicWrite(Path.Combine(directory, "entry.xml"), delegate(Stream stream) { Serializer.Serialize(stream, new List<Entry> { e }); });
            AtomicText(Path.Combine(directory, "登记信息.txt"),
                "盘号：" + e.Id + "\r\n标题：" + e.Title + "\r\n标题确认：" + e.TitleState + "\r\n项目：" + e.Project + "\r\n状态：" + e.Status +
                "\r\n目标硬盘：" + e.TargetName + "\r\n目标路径：" + e.TargetRoot + "\r\n原图：" + e.OriginalPhoto + "\r\n登记时间：" + e.Created +
                "\r\n完整文件数：" + e.Files + "\r\n完整字节数：" + e.Bytes + "\r\n失败条目：" + e.Failed + (e.Warnings>0 ? "\r\n时间提示：" + e.Warnings : "") +
                "\r\n原盘号："+(e.CanonicalId??e.Id)+"\r\n已核对沿用文件："+e.ReusedFiles+"\r\n最近复核登记："+e.LatestRunId+
                (String.IsNullOrEmpty(e.SourceFormat)?"":"\r\n读取格式："+e.SourceFormat)+(String.IsNullOrEmpty(e.SourceCoverage)?"":"\r\n未覆盖范围："+e.SourceCoverage)+
                (String.IsNullOrEmpty(e.CloseWarning)?"":"\r\n"+e.CloseWarning)+
                "\r\n项目目录：" + String.Join(" / ",e.ArchiveFolders??new string[0]) + "\r\n卷标：" + e.DiscLabel +
                "\r\n识图结果：" + e.VisionNote + "\r\n盘面候选：" + e.PhotoText + "\r\n命名依据：" + e.NamingReason + "\r\n抢救记录：" + e.Report + "\r\n备注：" + e.Note + "\r\n");
        }

        private static byte[] MakeThumbnail(Stream source)
        {
#if MAC_PORT
            return PortableSupport.MakeThumbnail(source);
#else
            using (Image image = Image.FromStream(source, true, true))
            {
                if (Array.IndexOf(image.PropertyIdList, 0x0112) >= 0)
                {
                    byte[] value = image.GetPropertyItem(0x0112).Value;
                    int orientation = value.Length >= 2 ? value[0] | (value[1] << 8) : 1;
                    RotateFlipType rotation = RotateFlipType.RotateNoneFlipNone;
                    switch (orientation)
                    {
                        case 2: rotation = RotateFlipType.RotateNoneFlipX; break;
                        case 3: rotation = RotateFlipType.Rotate180FlipNone; break;
                        case 4: rotation = RotateFlipType.Rotate180FlipX; break;
                        case 5: rotation = RotateFlipType.Rotate90FlipX; break;
                        case 6: rotation = RotateFlipType.Rotate90FlipNone; break;
                        case 7: rotation = RotateFlipType.Rotate270FlipX; break;
                        case 8: rotation = RotateFlipType.Rotate270FlipNone; break;
                    }
                    image.RotateFlip(rotation);
                }
                double scale = Math.Min(1.0, 1200.0 / Math.Max(image.Width, image.Height));
                int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                using (Bitmap result = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(result))
                using (MemoryStream output = new MemoryStream())
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, 0, 0, width, height);
                    result.Save(output, ImageFormat.Jpeg);
                    return output.ToArray();
                }
            }
#endif
        }

        private static void CleanAttempt(string directory, string[] files)
        {
            try
            {
                foreach (string file in files) { string path = Path.Combine(directory, file); if (File.Exists(path)) File.Delete(path); }
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static FileStream Acquire(string path)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (DateTime.UtcNow >= deadline) throw; Thread.Sleep(50); }
            }
        }

        private static void AtomicText(string path, string text)
        {
            AtomicWrite(path, delegate(Stream stream) { byte[] bytes = Utf8.GetPreamble(); stream.Write(bytes, 0, bytes.Length); bytes = Utf8.GetBytes(text); stream.Write(bytes, 0, bytes.Length); });
        }

        internal static void AtomicWrite(string path, Action<Stream> write)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, path + ".bak", true); }
                    catch (PlatformNotSupportedException) { ReplaceByRename(temporary, path); }
                    catch (IOException error)
                    {
                        int code = error.HResult & 0xffff;
                        if (code != 1 && code != 50) throw;
                        ReplaceByRename(temporary, path);
                    }
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ReplaceByRename(string temporary, string path)
        {
            // Some external file systems do not support ReplaceFile. Retain a recoverable
            // previous version; two renames are not a power-loss-atomic transaction.
            string backup = path + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
            try { File.Move(temporary, path); }
            catch { if (!File.Exists(path)) File.Move(backup, path); throw; }
        }
    }
}
