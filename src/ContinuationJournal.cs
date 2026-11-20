using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace DiscArchive
{
    // Only relative paths are replayed. The caller has already confirmed disc identity.
    internal sealed class ContinuationJournal : IDisposable
    {
        internal sealed class Entry { internal string Destination, Hash; internal long Length; internal bool Directory, Complete; }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly string source, target;
        private readonly Dictionary<string, Entry> fileHistory = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> oldFiles = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> encounteredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> uncertainPrefixes = new List<string>();
        private string targetIdentity, legacyTarget;
        private FileStream gate, log;
        internal ContinuationJournal(string directory, string sourceRoot, string targetRoot)
        {
            source = Root(sourceRoot); target = Root(targetRoot);
            string journalRoot = Root(directory);
            if (journalRoot.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(journalRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("续拷记录不能与原盘目录重叠。");
            Safe(directory); Directory.CreateDirectory(directory);
            try
            {
                string gatePath = Path.Combine(directory, "continuation.lock"); Safe(gatePath);
                try { gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException e) { throw new IOException("此原盘已有活动任务，或续拷锁不可用；不能同时续拷。", e); }
                ReadIdentity(directory);
                string path = Path.Combine(directory, "continuation-v1.tsv"); Safe(path);
                bool importing = !File.Exists(path);
                if (!importing)
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    if (lines.Length == 0 || lines[0] != "DiscArchive continuation v1") throw new IOException("续拷账本格式损坏，已停止。");
                    for (int i = 1; i < lines.Length; i++) Replay(lines[i]);
                }
                else
                {
                    ImportLegacy(directory);
                    foreach (KeyValuePair<string, Entry> item in entries) if (!item.Value.Directory) fileHistory[item.Key] = item.Value;
                }
                foreach (KeyValuePair<string, Entry> item in fileHistory) oldFiles.Add(item.Key, item.Value);
                // Bind the original destination before any route can be used.
                string binding = Path.Combine(directory, "continuation-target.txt"); Safe(binding);
                if (File.Exists(binding)) { if (!String.Equals(File.ReadAllText(binding, Encoding.UTF8), targetIdentity, StringComparison.OrdinalIgnoreCase)) throw new IOException("续拷原目标与本次目标不一致。"); }
                else { if (!importing) throw new IOException("续拷目标绑定缺失，不能确认原目标。"); using (FileStream f = new FileStream(binding, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { byte[] b = Encoding.UTF8.GetBytes(targetIdentity); f.Write(b, 0, b.Length); f.Flush(true); } }
                log = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (log.Length == 0) Write("DiscArchive continuation v1");
                if (importing) foreach (KeyValuePair<string, Entry> item in entries)
                {
                    Entry e = item.Value;
                    string body = (e.Directory ? "D" : "R") + "\t" + Encode(item.Key) + "\t" + Encode(e.Destination) + "\t0\t" + e.Hash;
                    Write(body + "\t" + Digest(body));
                }

            }
            catch { Dispose(); throw; }
        }
        private void ReadIdentity(string directory)
        {
            targetIdentity = target;
            string marker = Path.Combine(target, ".discarchive-target.id"); Safe(marker);
            string id = null;
            if (File.Exists(marker))
            {
                Guid guid; string value = File.ReadAllText(marker, Encoding.UTF8).Trim();
                if (!Guid.TryParse(value, out guid)) throw new IOException("原目标身份标记损坏。");
                id = guid.ToString("N"); targetIdentity = "guid:" + id;
            }
            string replica = Path.Combine(directory, "entry.xml"); Safe(replica);
            if (!File.Exists(replica)) return;
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            XmlDocument doc = new XmlDocument(); doc.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(replica, settings)) doc.Load(reader);
            XmlNodeList rows = doc.SelectNodes("/ArrayOfEntry/Entry");
            if (rows == null || rows.Count != 1) throw new IOException("原盘登记副本格式不明确。");
            XmlNode idNode = rows[0].SelectSingleNode("TargetId"), rootNode = rows[0].SelectSingleNode("TargetRoot");
            Guid registered;
            if (idNode == null || !Guid.TryParse(idNode.InnerText, out registered) || id == null || registered.ToString("N") != id) throw new IOException("原盘登记副本与目标身份不符。");
            if (rootNode != null && !String.IsNullOrWhiteSpace(rootNode.InnerText)) legacyTarget = Root(rootNode.InnerText);
        }
        private static string Root(string s) { return Path.GetFullPath(s).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
        private static string Encode(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        private static string Decode(string s) { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s)); }
        private static string Digest(string s) { using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", ""); }
        internal static void Safe(string p)
        {
            string current = Path.GetFullPath(p);
            while (current != null) { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("续拷拒绝目标链接：" + current); current = Path.GetDirectoryName(current); }
        }
        private static string Relative(string root, string p)
        {
            p = Path.GetFullPath(p); if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("续拷路线越界。");
            string rel = p.Substring(root.Length); Validate(rel); return rel;
        }
        private static void Validate(string rel)
        {
            if (String.IsNullOrEmpty(rel) || Path.IsPathRooted(rel)) throw new IOException("续拷相对路线损坏。");
            foreach (string part in rel.Split('\\', '/')) if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith(".") || part.EndsWith(" ")) throw new IOException("续拷相对路线损坏。");
        }
        private static void ValidateDestination(string rel)
        {
            Validate(rel); string first = rel.Split('\\', '/')[0];
            if (first.Equals("DiscArchive", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".discarchive", StringComparison.OrdinalIgnoreCase)) throw new IOException("续拷路线指向登记保留区域。");
        }
        private void Replay(string line)
        {
            try
            {
                string[] f = line.Split('\t'); if (f.Length != 6) throw new IOException();
                string body = String.Join("\t", f, 0, 5); if (Digest(body) != f[5]) throw new IOException();
                string src = Decode(f[1]), dst = Decode(f[2]); Validate(src); ValidateDestination(dst);
                long length; if (!Int64.TryParse(f[3], out length) || length < 0 || (f[0] != "D" && f[0] != "R" && f[0] != "F")) throw new IOException();
                if (f[0] == "F") { if (f[4].Length != 64) throw new IOException(); foreach (char c in f[4]) if (!Uri.IsHexDigit(c)) throw new IOException(); }
                Entry entry = new Entry { Destination = dst, Directory = f[0] == "D", Complete = f[0] == "F", Length = length, Hash = f[4] };
                entries[src] = entry;
                if (!entry.Directory) fileHistory[src] = entry;
            }
            catch (Exception e) { throw new IOException("续拷账本损坏；未把旧数据当作完成。", e); }
        }
        internal void Encounter(string src, bool directory)
        { if (!directory) encounteredFiles.Add(Relative(source, src)); }
        internal void Uncertain(string src)
        {
            string full = Path.GetFullPath(src).TrimEnd('\\', '/');
            string root = source.TrimEnd('\\', '/');
            if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) { uncertainPrefixes.Add(""); return; }
            uncertainPrefixes.Add(Relative(source, src));
        }
        internal List<KeyValuePair<string, Entry>> MissingFiles()
        {
            List<KeyValuePair<string, Entry>> missing = new List<KeyValuePair<string, Entry>>();
            foreach (KeyValuePair<string, Entry> item in oldFiles)
            {
                if (encounteredFiles.Contains(item.Key)) continue;
                bool covered = false;
                foreach (string prefix in uncertainPrefixes)
                    if (prefix.Length == 0 || String.Equals(item.Key, prefix, StringComparison.OrdinalIgnoreCase) || item.Key.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { covered = true; break; }
                if (!covered) missing.Add(new KeyValuePair<string, Entry>(Path.Combine(source, item.Key), item.Value));
            }
            return missing;
        }
        internal Entry Find(string src) { Entry e; entries.TryGetValue(Relative(source, src), out e); return e; }
        internal string Destination(Entry e) { string p = Path.Combine(target, e.Destination); Safe(p); return p; }
        internal void Route(string src, string dst, bool directory) { Record(src, dst, directory ? "D" : "R", 0, ""); }
        internal void Complete(string src, string dst, long length, string hash) { Record(src, dst, "F", length, hash); }
        private void Record(string src, string dst, string type, long length, string hash)
        {
            string destination = Relative(target, dst); ValidateDestination(destination);
            string body = type + "\t" + Encode(Relative(source, src)) + "\t" + Encode(destination) + "\t" + length + "\t" + hash;
            string line = body + "\t" + Digest(body); Write(line); Replay(line);
        }
        private void Write(string s) { byte[] b = Encoding.UTF8.GetBytes(s + "\r\n"); log.Write(b, 0, b.Length); log.Flush(true); }
        internal static string HashFile(string p, CancellationToken cancel, Action check, DiscSource media = null)
        {
            if (media == null || media.Native) Safe(p);
            using (Stream f = (media ?? DiscSource.Windows).OpenRead(p)) using (SHA256 h = SHA256.Create())
            { byte[] b = new byte[1024 * 1024]; int n; while ((n = f.Read(b, 0, b.Length)) != 0) { cancel.ThrowIfCancellationRequested(); if (check != null) check(); h.TransformBlock(b, 0, n, b, 0); } h.TransformFinalBlock(b, 0, 0); return BitConverter.ToString(h.Hash).Replace("-", ""); }
        }
        private static string Unescape(string s)
        {
            StringBuilder b = new StringBuilder(); for (int i = 0; i < s.Length; i++) { char c = s[i]; if (c == '\\') { if (++i == s.Length) throw new IOException("旧路径清单转义损坏。"); c = s[i]; if (c == 't') c = '\t'; else if (c == 'r') c = '\r'; else if (c == 'n') c = '\n'; else if (c != '\\') throw new IOException("旧路径清单转义损坏。"); } b.Append(c); } return b.ToString();
        }
        private void ImportLegacy(string directory)
        {
            List<string> lists = new List<string>(); Collect(directory, lists);
            foreach (string list in lists) foreach (string line in File.ReadAllLines(list, Encoding.UTF8))
            {
                string[] f = line.Split('\t'); if (f.Length != 4 || (f[0] != "目录" && f[0] != "项目目录" && f[0] != "文件" && f[0] != "条目")) continue;
                string old = Unescape(f[1]), dst = Unescape(f[2]); if (!Path.IsPathRooted(old) || !Path.IsPathRooted(dst)) throw new IOException("旧路径清单不是绝对路线。");
                string rel = old.Substring(Path.GetPathRoot(old).Length); Validate(rel); string dest = legacyTarget != null && Path.GetFullPath(dst).StartsWith(legacyTarget, StringComparison.OrdinalIgnoreCase) ? Relative(legacyTarget, dst) : Relative(target, dst);
                ValidateDestination(dest);
                Entry existing; if (entries.TryGetValue(rel, out existing) && !String.Equals(existing.Destination, dest, StringComparison.OrdinalIgnoreCase)) throw new IOException("旧路径清单有多套路线，无法安全自动续拷。");
                entries[rel] = new Entry { Destination = dest, Directory = f[0] == "目录" || f[0] == "项目目录", Complete = false, Hash = f[0] == "文件" ? "legacy" : "" };
            }
        }
        private static void Collect(string dir, List<string> paths)
        {
            Safe(dir); string list = Path.Combine(dir, "路径清单.tsv"); if (File.Exists(list)) { Safe(list); paths.Add(list); }
            foreach (string child in Directory.GetDirectories(dir)) { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧记录目录包含链接。"); string name = Path.GetFileName(child); if (name.StartsWith("抢救记录-", StringComparison.Ordinal) || name.StartsWith("抢救-", StringComparison.Ordinal)) Collect(child, paths); }
        }
        public void Dispose() { if (log != null) log.Dispose(); if (gate != null) gate.Dispose(); }
    }
}
