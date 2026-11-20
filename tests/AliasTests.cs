using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscArchive;

public static class AliasTests
{
    private static void Assert(bool value, string label)
    { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    private static AliasChoice Inspect(string root, string name) { return ProjectAliases.Inspect(root, name, CancellationToken.None); }
    private static string Folder(string root, string name) { Directory.CreateDirectory(Path.Combine(root, name)); return name; }
    private static Process Child(string args)
    {
        return Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, args) { UseShellExecute = false, CreateNoWindow = true });
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0] == "probe") return Inspect(args[1], "240102-ACMEE").Resolved == "240102-ACME" ? 0 : 2;
            if (args[0] == "write")
            { for (int i = 0; i < 20; i++) ProjectAliases.Remember(args[1], "240102-Client" + args[2] + "-" + i, "240102-ACME"); return 0; }
            if (args[0] == "resolve")
            {
                AliasChoice choice = ProjectAliases.ResolveForWrite(args[1], args[2], false, CancellationToken.None);
                return Directory.Exists(Path.Combine(args[1], choice.Resolved)) && choice.Suggestions.Length == 0 ? 0 : 4;
            }
            return 3;
        }
        string temp = Path.Combine(Path.GetTempPath(), "DiscArchive-AliasTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Console.WriteLine("Synthetic fixtures: " + temp);
        try
        {
            string root = Path.Combine(temp, "archive"); Directory.CreateDirectory(root);
            Folder(root, "240102-ACME"); Folder(root, "240102-客户 春季 A");
            Assert(Inspect(root, "2024年1月2日-ＡＣＭＥ").Resolved == "240102-ACME", "NFKC and Chinese date symbols equivalent");
            Assert(Inspect(root, "2024.01.02_ACME").Resolved == "240102-ACME", "date and separators equivalent");
            Assert(Inspect(root, "24-1-2-ACME").Resolved == "240102-ACME", "short separated date equivalent");
            Assert(Inspect(root, "2024 年 1 月 2 日 — ACME").Resolved == "240102-ACME", "spaces around date symbols equivalent");
            Assert(Inspect(root, "240102- 客 户_春季-A").Resolved == "240102-客户 春季 A", "whitespace and separators equivalent");
            AliasChoice typo = Inspect(root, "240102-ACMEE");
            Assert(typo.Resolved == typo.Proposed && typo.Suggestions.Contains("240102-ACME"), "spelling difference advisory only");
            AliasChoice nearCustomer = Inspect(root, "240102-ACNE");
            Assert(nearCustomer.Resolved == nearCustomer.Proposed, "similar different customer never merged");
            foreach (string name in new[] { "240102-客户 春季 B", "240102-客户 春季 A 2", "240102-客户 春季 A 成片", "240102-客户 春季 AB", "240103-ACME" })
                Assert(Inspect(root, name).Resolved == name, "distinct project preserved: " + name);
            Assert(Inspect(root, "240103-ACME").Suggestions.Length == 0, "other date not fuzzy candidate");
            ProjectAliases.Remember(root, "240102-ACMEE", "240102-ACME");
            Assert(Inspect(root, "240102-ACMEE").Resolved == "240102-ACME", "explicit alias read back");
            Folder(root, "240102-ACMEE"); File.WriteAllText(Path.Combine(root, "240102-ACMEE", "old.txt"), "existing old project");
            Assert(Inspect(root, "240102-ACMEE").Resolved == "240102-ACME", "confirmed alias overrides already split directory");
            AliasChoice redirected = ProjectAliases.ResolveForWrite(root, "240102-ACMEE", false, CancellationToken.None);
            Assert(redirected.Resolved == "240102-ACME" && File.ReadAllText(Path.Combine(root, "240102-ACMEE", "old.txt")) == "existing old project", "new writes route to confirmed project without moving old content");
            using (Process child = Child("probe \"" + root + "\"")) { child.WaitForExit(); Assert(child.ExitCode == 0, "alias survives process restart"); }
            string other = Path.Combine(temp, "other"); Directory.CreateDirectory(other); Folder(other, "240102-ACME");
            Assert(Inspect(other, "240102-ACMEE").Resolved == "240102-ACMEE", "alias scoped to archive target");
            ProjectAliases.Remember(root, "240103-ACME", "240102-ACME");
            Assert(Inspect(root, "240103-ACME").Resolved == "240102-ACME", "explicit human cross-date alias allowed");
            foreach (string unknown in new[] { "241201-拍摄日未知-ACME-disc1", "241201-混装待归属-disc2", "241201-项目未知-disc3", "241201-RAW原档-disc4", "241201-仅导入日-ACME" })
            {
                ProjectAliases.Remember(root, unknown, "240102-ACME");
                Assert(Inspect(root, unknown).Resolved == unknown, "uncertain disc not memorized: " + unknown);
            }
            Folder(root, "240102-A C M E");
            Assert(Inspect(root, "240102-ACME 2").Resolved == "240102-ACME 2", "project number not stripped");
            Folder(root, "2024-01-02-ACME");
            AliasChoice ambiguous = Inspect(root, "20240102_ACME");
            Assert(ambiguous.Resolved == ambiguous.Proposed && ambiguous.Suggestions.Length == 2, "multiple equivalent destinations require choice");
            ProjectAliases.Remember(root, "240102-CONFLICT", "240102-ACME");
            ProjectAliases.Remember(root, "240102-CONFLICT", "240102-A C M E");
            AliasChoice conflict = Inspect(root, "240102-CONFLICT");
            Assert(conflict.Resolved == "240102-CONFLICT" && conflict.Suggestions.Contains("240102-ACME") && conflict.Suggestions.Contains("240102-A C M E"), "competing aliases fail closed with valid manual choices");
            AliasChoice blocked = ProjectAliases.ResolveForWrite(root, "240102-CONFLICT", false, CancellationToken.None);
            Assert(blocked.Suggestions.Length > 0 && !Directory.Exists(Path.Combine(root, "240102-CONFLICT")), "unconfirmed conflict does not create project");
            Folder(root, "240102-Temporary"); ProjectAliases.Remember(root, "240102-Stale", "240102-Temporary");
            Directory.Move(Path.Combine(root, "240102-Temporary"), Path.Combine(root, "240102-Moved"));
            Assert(Inspect(root, "240102-Stale").Resolved == "240102-Stale", "missing alias target never resurrected");
            Assert(Inspect(root, "240102-Stale").Suggestions.Length > 0, "stale alias offers existing manual choices");
            Assert(!Directory.Exists(Path.Combine(root, "240102-Stale")), "inspection never creates project folder");
            bool traversal = false; try { ProjectAliases.Remember(root, "240102-ACME", "..\\outside"); } catch (IOException) { traversal = true; }
            Assert(traversal, "manual destination cannot escape root");
            Assert(Inspect(root, "..\\outside").Resolved == "..\\outside" && Inspect(root, "..\\outside").Reason.Contains("不安全"), "unsafe proposal is reported without resolution");
            string[] known = ProjectAliases.ListKnownProjects(root, CancellationToken.None);
            Assert(known.Contains("240102-ACME") && !known.Any(x => x.StartsWith(".discarchive")), "manual inventory contains ordinary directories only");
            string outside = Path.Combine(temp, "outside"); Directory.CreateDirectory(outside);
            string link = Path.Combine(root, "240102-Linked");
            using (Process junction = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            { junction.WaitForExit(); Assert(junction.ExitCode == 0, "synthetic junction fixture created"); }
            Assert(!ProjectAliases.ListKnownProjects(root, CancellationToken.None).Contains("240102-Linked"), "linked directory excluded from manual choices");
            bool linked = false; try { ProjectAliases.Remember(root, "240102-LinkAlias", "240102-Linked"); } catch (IOException) { linked = true; }
            Assert(linked, "manual alias cannot target directory link");
            Assert(ProjectAliases.ListKnownProjects(link, CancellationToken.None).Length == 0, "linked archive root rejected");
            foreach (string reserved in new[] { "DiscArchive", "盘面照片", ".discarchive-custom", "..\\escape", "NUL", "240102-Project/sub" })
            {
                bool rejected = false; try { ProjectAliases.ResolveForWrite(root, reserved, true, CancellationToken.None); } catch (IOException) { rejected = true; }
                Assert(rejected, "write resolver rejects reserved or unsafe project: " + reserved);
            }
            string hidden = Folder(root, "240102-Hidden"); File.SetAttributes(Path.Combine(root, hidden), File.GetAttributes(Path.Combine(root, hidden)) | FileAttributes.Hidden);
            Assert(!ProjectAliases.ListKnownProjects(root, CancellationToken.None).Contains(hidden), "hidden directory not offered as project");
            bool hiddenRejected = false; try { ProjectAliases.ResolveForWrite(root, hidden, true, CancellationToken.None); } catch (IOException) { hiddenRejected = true; }
            Assert(hiddenRejected, "hidden existing project rejected for writes");
            string fresh = Path.Combine(temp, "dual-station"); Directory.CreateDirectory(fresh);
            using (Process a = Child("resolve \"" + fresh + "\" \"2024年1月2日-ＡＣＭＥ_春季\""))
            using (Process b = Child("resolve \"" + fresh + "\" \"240102 - ACME-春季\""))
            { a.WaitForExit(); b.WaitForExit(); Assert(a.ExitCode == 0 && b.ExitCode == 0, "both workstations resolve new equivalent project"); }
            Assert(Directory.GetDirectories(fresh).Length == 1, "target-side selection lock creates one equivalent project directory");
            string uncertain = "240102-ACMEE 春季";
            AliasChoice waitForChoice = ProjectAliases.ResolveForWrite(fresh, uncertain, false, CancellationToken.None);
            Assert(waitForChoice.Suggestions.Length > 0 && !Directory.Exists(Path.Combine(fresh, uncertain)), "near spelling returns suggestions without creating project");
            AliasChoice keep = ProjectAliases.ResolveForWrite(fresh, uncertain, true, CancellationToken.None);
            Assert(keep.Resolved == uncertain && Directory.Exists(Path.Combine(fresh, uncertain)), "explicit keep creates current project name");
            string canonical = Directory.GetDirectories(fresh).Select(Path.GetFileName).First(x => x != uncertain);
            AliasChoice formatKeep = ProjectAliases.ResolveForWrite(fresh, "2024.01.02_ACME 春季", true, CancellationToken.None);
            Assert(formatKeep.Resolved == canonical && Directory.GetDirectories(fresh).Length == 2, "keep decision preserves unique safe format equivalence");
            using (Process a = Child("write \"" + root + "\" A"))
            using (Process b = Child("write \"" + root + "\" B"))
            { a.WaitForExit(); b.WaitForExit(); Assert(a.ExitCode == 0 && b.ExitCode == 0, "two processes serialize alias writes"); }
            for (int i = 0; i < 20; i++)
            {
                Assert(Inspect(root, "240102-ClientA-" + i).Resolved == "240102-ACME" && Inspect(root, "240102-ClientB-" + i).Resolved == "240102-ACME", "concurrent records retained " + i);
            }
            File.AppendAllText(Path.Combine(root, ".discarchive-project-aliases.v1"), "torn-record");
            ProjectAliases.Remember(root, "240102-AFTERTORN", "240102-ACME");
            Assert(Inspect(root, "240102-AFTERTORN").Resolved == "240102-ACME", "torn tail cannot swallow next record");
            CancellationTokenSource canceled = new CancellationTokenSource(); canceled.Cancel(); bool cancelSeen = false;
            try { ProjectAliases.Inspect(root, "240102-ACME", canceled.Token); } catch (OperationCanceledException) { cancelSeen = true; }
            Assert(cancelSeen, "inspection cancellation honored");
            Console.WriteLine("PASS alias suite; synthetic fixtures retained for inspection."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
