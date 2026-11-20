using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiscArchive
{
    internal sealed class AliasChoice
    {
        public string Proposed, Resolved, Reason;
        public bool NeedsDecision;
        public string[] Suggestions;
    }

    // Metadata is local to the selected archive root. Only ResolveForWrite creates project folders.
    internal static class ProjectAliases
    {
        private const string StoreName = ".discarchive-project-aliases.v1";
        private const string SelectionLockName = ".discarchive-project-selection.lock";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private sealed class Identity { public string Day, Name; }

        public static AliasChoice Inspect(string targetRoot, string proposedFolder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AliasChoice result = new AliasChoice { Proposed = proposedFolder, Resolved = proposedFolder,
                Reason = "未找到可安全自动归并的已知项目", Suggestions = new string[0] };
            string root = SafeRoot(targetRoot);
            if (root == null || !SafeLeaf(proposedFolder)) { result.Reason = "目录不安全，未应用项目别名"; return result; }
            string[] known = ListKnownProjects(root, token);
            Identity proposed = Parse(proposedFolder);
            bool storeUsable = true;
            string[] aliases = proposed == null ? new string[0] : ReadAliases(root, proposedFolder, token, out storeUsable);
            if (!storeUsable)
            {
                result.Reason = "项目别名暂时不可读，需重新人工选择";
                result.Suggestions = known.Take(8).ToArray();
                return result;
            }
            if (aliases.Length > 0)
            {
                // Even a stale competing destination makes this key ambiguous: do not guess.
                if (aliases.Length == 1 && known.Contains(aliases[0], ArchivePath.Comparer))
                { result.Resolved = known.First(x => String.Equals(x, aliases[0], ArchivePath.Comparison)); result.Reason = "采用此归档目标内已确认的项目别名"; return result; }
                result.NeedsDecision=true; result.Reason = "已确认别名失效或存在冲突，需重新人工选择";
                result.Suggestions = known.Where(x => aliases.Contains(x, ArchivePath.Comparer)).Concat(known).Distinct(ArchivePath.Comparer).Take(8).ToArray();
                return result;
            }
            string exact = known.FirstOrDefault(x => String.Equals(x, proposedFolder, ArchivePath.Comparison));
            if (exact != null) { result.Resolved = exact; result.Reason = "已存在同名项目目录"; return result; }
            if (proposed == null) { result.Reason = "混装、未知或日期/项目不明确，保留单盘隔离；可人工选择已有项目"; return result; }
            string[] equal = known.Where(x => Equivalent(proposed, Parse(x))).ToArray();
            if (equal.Length == 1) { result.Resolved = equal[0]; result.Reason = "拍摄日期和完整项目文字格式等价，采用已有目录"; return result; }
            if (equal.Length > 1) { result.NeedsDecision=true; result.Suggestions = equal.Take(8).ToArray(); result.Reason = "多个已有目录格式等价，需人工选择"; return result; }
            result.Suggestions = known.Select(x => new { Folder = x, Identity = Parse(x) })
                .Where(x => x.Identity != null && x.Identity.Day == proposed.Day && Similar(proposed.Name, x.Identity.Name))
                .OrderBy(x => Distance(proposed.Name, x.Identity.Name)).ThenBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
                .Take(5).Select(x => x.Folder).ToArray();
            if (result.Suggestions.Length > 0) { result.NeedsDecision=true; result.Reason = "有同日近似名称，仅供人工核对，未自动合并"; }
            return result;
        }

        // Hold a shared-target filesystem lock only while deciding and creating the destination.
        // A result asking for confirmation has no filesystem side effect except the lock metadata.
        public static AliasChoice ResolveForWrite(string targetRoot, string proposedFolder, bool keepProposed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string root = SafeRoot(targetRoot);
            if (root == null || !SafeLeaf(proposedFolder)) throw new IOException("归档项目必须是当前目标根内的安全单层目录，不能使用保留名称。");
            using (FileStream selection = OpenNamedStore(root, SelectionLockName, true, token))
            {
                AliasChoice choice = Inspect(root, proposedFolder, token);
                bool needsChoice=choice.NeedsDecision;
                if (needsChoice && !keepProposed) return choice;
                if (needsChoice && keepProposed)
                {
                    // Preserve the proposed name only on an explicit human decision. Safe unique
                    // format equivalence has already resolved above and continues to win.
                    choice.Resolved = proposedFolder;
                    choice.Suggestions = new string[0]; choice.NeedsDecision=false;
                    choice.Reason = "用户明确保留当前项目名称，既有目录和内容不搬动";
                }
                token.ThrowIfCancellationRequested();
                if (SafeRoot(root) == null || !SafeLeaf(choice.Resolved)) throw new IOException("归档目录已变化，请重新选择。");
                string destination = Path.Combine(root, choice.Resolved);
                if (Directory.Exists(destination) && !OrdinaryProjectDirectory(destination)) throw new IOException("归档项目不能是目录链接或隐藏保留目录。");
                Directory.CreateDirectory(destination);
                if (!OrdinaryProjectDirectory(destination)) throw new IOException("归档项目不能是目录链接或隐藏保留目录。");
                return choice;
            }
        }

        public static string[] ListKnownProjects(string targetRoot, CancellationToken token)
        {
            string root = SafeRoot(targetRoot);
            if (root == null) return new string[0];
            List<string> names = new List<string>();
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(directory);
                    if (SafeLeaf(name) && OrdinaryProjectDirectory(directory)) names.Add(name);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
            return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static void Remember(string targetRoot, string proposedFolder, string existingFolder)
        {
            string root = SafeRoot(targetRoot);
            if (root == null || !SafeLeaf(proposedFolder) || !SafeLeaf(existingFolder) ||
                !OrdinaryProjectDirectory(Path.Combine(root, existingFolder))) throw new IOException("只能选择当前归档目标内已存在的安全普通目录。");
            // A poor/unknown label can be manually routed for this disc, but must not become a global rule.
            if (Parse(proposedFolder) == null) return;
            using (FileStream stream = OpenStore(root, true, CancellationToken.None))
            {
                string payload = Convert.ToBase64String(Utf8.GetBytes(proposedFolder)) + "\t" + Convert.ToBase64String(Utf8.GetBytes(existingFolder));
                byte[] line = Utf8.GetBytes("\n" + payload + "\t" + Digest(payload) + "\n");
                stream.Seek(0, SeekOrigin.End); stream.Write(line, 0, line.Length); stream.Flush(true);
            }
        }

        private static string[] ReadAliases(string root, string proposed, CancellationToken token, out bool usable)
        {
            usable = true;
            HashSet<string> destinations = new HashSet<string>(ArchivePath.Comparer);
            try
            {
                using (FileStream stream = OpenStore(root, false, token))
                {
                    if (stream == null) return new string[0];
                    using (StreamReader reader = new StreamReader(stream, Utf8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            token.ThrowIfCancellationRequested();
                            string[] fields = line.Split('\t');
                            if (fields.Length != 3 || Digest(fields[0] + "\t" + fields[1]) != fields[2]) continue;
                            try
                            {
                                string key = Utf8.GetString(Convert.FromBase64String(fields[0]));
                                string destination = Utf8.GetString(Convert.FromBase64String(fields[1]));
                                if (String.Equals(key, proposed, StringComparison.OrdinalIgnoreCase) && SafeLeaf(destination)) destinations.Add(destination);
                            }
                            catch (FormatException) { }
                            catch (DecoderFallbackException) { }
                        }
                    }
                }
            }
            catch (IOException) { usable = false; }
            catch (UnauthorizedAccessException) { usable = false; }
            catch (DecoderFallbackException) { usable = false; }
            return destinations.ToArray();
        }

        private static FileStream OpenStore(string root, bool create, CancellationToken token)
        {
            return OpenNamedStore(root, StoreName, create, token);
        }
        private static FileStream OpenNamedStore(string root, string name, bool create, CancellationToken token)
        {
            string file = Path.Combine(root, name);
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (SafeRoot(root) == null) throw new IOException("归档目标不可安全访问。");
                if (!create && !File.Exists(file)) return null;
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("别名文件不能是链接。");
                try { return new FileStream(file, create ? FileMode.OpenOrCreate : FileMode.Open, create ? FileAccess.ReadWrite : FileAccess.Read, FileShare.None); }
                catch (IOException) { if (attempt >= 99) throw; token.WaitHandle.WaitOne(20); }
            }
        }

        private static bool SafeLeaf(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.Length <= 255 &&
                !name.StartsWith(".", StringComparison.Ordinal) &&
                !String.Equals(name.Normalize(NormalizationForm.FormKC), "DiscArchive", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(name.Normalize(NormalizationForm.FormKC), "盘面照片", StringComparison.OrdinalIgnoreCase) &&
                name == name.Trim() && !name.EndsWith(".", StringComparison.Ordinal) &&
                !Regex.IsMatch(name, "[<>:\"/\\\\|?*\\p{Cc}]") &&
                !Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase);
        }
        private static bool OrdinaryDirectory(string path)
        {
            try { FileAttributes a = File.GetAttributes(path); return (a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0; }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        private static bool OrdinaryProjectDirectory(string path)
        {
            try { return OrdinaryDirectory(path) && (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0; }
            catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        private static string SafeRoot(string root)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(root)) return null;
                DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(root));
                string full = current.FullName;
                while (current != null) { if (!OrdinaryDirectory(current.FullName)) return null; current = current.Parent; }
                return full;
            }
            catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }
        private static Identity Parse(string folder)
        {
            if (!SafeLeaf(folder)) return null;
            string text = Regex.Replace(folder.Normalize(NormalizationForm.FormKC).Trim(), "[‐‑‒–—]", "-");
            if (Regex.IsMatch(text, @"混装|未知|待归属|不详|未命名|拍摄日未知|导入日|仅导入|待确认|无盘号|RAW原档|JPG照片|影像素材|视频素材|设计文件|\b(?:unknown|untitled|mixed|imported)\b", RegexOptions.IgnoreCase)) return null;
            Match m = Regex.Match(text, @"^(?:(?<y>19\d{2}|20\d{2})[-_. 年]+(?<m>\d{1,2})[-_. 月]+(?<d>\d{1,2}) *日?|(?<full>\d{8})|(?<short>\d{6})|(?<y2>\d{2})[-_. 年]+(?<m2>\d{1,2})[-_. 月]+(?<d2>\d{1,2}) *日?)(?!\d)");
            if (!m.Success) return null;
            int y, month, day;
            string digits = m.Groups["full"].Success ? m.Groups["full"].Value : m.Groups["short"].Value;
            if (digits.Length > 0) { int n = digits.Length - 4; y = Int32.Parse(digits.Substring(0, n)); month = Int32.Parse(digits.Substring(n, 2)); day = Int32.Parse(digits.Substring(n + 2, 2)); }
            else if (m.Groups["y"].Success) { y = Int32.Parse(m.Groups["y"].Value); month = Int32.Parse(m.Groups["m"].Value); day = Int32.Parse(m.Groups["d"].Value); }
            else { y = Int32.Parse(m.Groups["y2"].Value); month = Int32.Parse(m.Groups["m2"].Value); day = Int32.Parse(m.Groups["d2"].Value); }
            if (y < 100) y += y < 50 ? 2000 : 1900;
            DateTime date; try { date = new DateTime(y, month, day); } catch (ArgumentOutOfRangeException) { return null; }
            string name = text.Substring(m.Length);
            if (Regex.IsMatch(name, @"(?<!\d)(?:19|20)\d{6}(?!\d)|\d{2,4}[-_.年]\d{1,2}[-_.月]\d{1,2}")) return null;
            name = Regex.Replace(name, @"[\s_\-‐‑‒–—.·]+", " ").Trim().ToUpperInvariant();
            name = Regex.Replace(name, @"(?<=[\u3400-\u9fff]) +(?=[\u3400-\u9fff])", "");
            if (!Regex.IsMatch(name, @"\p{L}") || name.Length < 2 || Regex.IsMatch(name, @"^(RAW|JPG|JPEG|PHOTOS?|IMAGES?|DVD|DISC|DATA|照片|素材|原片|成片|备份|测试)( |$)")) return null;
            return new Identity { Day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Name = name };
        }
        private static bool Equivalent(Identity a, Identity b) { return a != null && b != null && a.Day == b.Day && a.Name == b.Name; }
        private static bool Similar(string a, string b)
        {
            // Similarity is deliberately advisory, including short names and abbreviations.
            int distance = Distance(a, b);
            int minimumPrefix = Regex.IsMatch(a, @"^[\u3400-\u9fff]{2}") && Regex.IsMatch(b, @"^[\u3400-\u9fff]{2}") ? 2 : 3;
            return distance <= (Math.Max(a.Length, b.Length) >= 7 ? 2 : 1) ||
                (Math.Min(a.Length, b.Length) >= minimumPrefix && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
        }
        private static int Distance(string a, string b)
        {
            int[] row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++) { int previous = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1)); previous = old; } }
            return row[b.Length];
        }
        private static string Digest(string text) { using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Utf8.GetBytes(text))); }
    }
}
