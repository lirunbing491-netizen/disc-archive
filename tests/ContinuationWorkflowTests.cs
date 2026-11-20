using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscArchive;

internal static class ContinuationWorkflowTests
{
    private sealed class Drive : IReader
    {
        internal string Letter, Source, Label;
        public string[] Drives() { return new[] { Letter }; }
        public bool Empty(string letter) { return false; }
        public bool Ready(string letter) { return letter == Letter; }
        public MountedDisc Open(string letter)
        {
            if (letter != Letter) throw new IOException("Synthetic drive unavailable");
            return new MountedDisc(Source, Label, delegate { if (!Directory.Exists(Source)) throw new IOException("Synthetic medium removed"); }, null);
        }
    }
    private static int checks, states;
    private static string root, photo;
    private static void Check(bool yes, string name)
    { if (!yes) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static T Wait<T>(Task<T> task)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 25) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic workflow exceeded 25 seconds");
        return task.GetAwaiter().GetResult();
    }
    private static string Dir(string name) { return Directory.CreateDirectory(Path.Combine(root, name)).FullName; }
    private static string Source(string name)
    {
        string source = Dir(name);
        File.WriteAllText(Path.Combine(source, "first.bin"), "synthetic archive payload one");
        File.WriteAllText(Path.Combine(source, "second.bin"), "synthetic archive payload two with distinct length");
        Directory.CreateDirectory(Path.Combine(source, "empty-folder"));
        return source;
    }
    private static Workstation Station(string source, string letter, string label)
    {
        // Every call has an independent local catalog and a new reader/workstation object.
        return new Workstation(new Catalog(Dir("workstation-state-" + (++states))), new Drive { Source = source, Letter = letter, Label = label });
    }
    private static Entry Register(Workstation station, string target)
    {
        Drive drive = (Drive)station.Reader;
        return station.Register(photo, target, "", drive.Letter, "", true);
    }
    private static Entry Copy(Workstation station) { return Wait(station.Copy(delegate { })); }
    private static void Select(Workstation station, string id, Action<DiscMatch> inspect)
    {
        station.ChooseMatch = delegate(Entry e, List<DiscMatch> choices, CancellationToken token)
        {
            Check(choices.Any(x => x.Id == id), "original ID offered to independent workstation");
            DiscMatch chosen = choices.Single(x => x.Id == id);
            if (inspect != null) inspect(chosen);
            return Task.FromResult(chosen);
        };
    }
    private static string[] Payloads(string target)
    { return Directory.GetFiles(target, "*.bin", SearchOption.AllDirectories).OrderBy(x => x).ToArray(); }
    private static void CompleteAndCrossStation(Form owner)
    {
        string disk = Dir("complete-target"), label = "130708-星河访谈素材";
        Workstation firstStation = Station(Source("complete-disc-X"), @"X:\", label);
        Entry first = Register(firstStation, disk);
        Entry firstDone = Copy(firstStation);
        Check(firstDone.Status == Workstation.Done && firstDone.Files == 2 && firstDone.ReusedFiles == 0, "first registration copies two real files");
        string[] originalPaths = Payloads(disk);
        Check(originalPaths.Length == 2 && Directory.Exists(Path.Combine(disk, label, "empty-folder")), "first copy preserves project paths and empty directory");
        Dictionary<string, DateTime> times = originalPaths.ToDictionary(x => x, File.GetLastWriteTimeUtc);

        Workstation secondStation = Station(Source("complete-disc-Y"), @"Y:\", label);
        Entry second = Register(secondStation, disk);
        Check(second.Id != first.Id && second.Drive != first.Drive, "fresh workstation and drive letter allocate a new intake ID");
        bool windowChosen = false;
        secondStation.ChooseMatch = delegate(Entry intake, List<DiscMatch> choices, CancellationToken token)
        {
            int selected = choices.FindIndex(x => x.Id == first.Id);
            Check(selected >= 0, "completed original appears in actual choice window");
            Task<DiscMatch> answer = ArchiveChoices.Match(owner, intake.Drive, intake.Id, choices, token);
            Form window = owner.OwnedForms.Single();
            window.StartPosition = FormStartPosition.Manual; window.Location = owner.Location;
            Check(!window.Modal && owner.Enabled, "actual same-disc confirmation leaves owner usable");
            Find<ListBox>(window, null).SelectedIndex = selected;
            Find<Button>(window, "确认同一张：核对并补缺").PerformClick();
            windowChosen = true;
            return answer;
        };
        Entry secondDone = Copy(secondStation);
        Check(windowChosen && secondDone.Status == Workstation.Done && secondDone.Files == 2 && secondDone.ReusedFiles == 2, "fresh registration of completed disc reuses all files with zero new copies");
        Check(Payloads(disk).SequenceEqual(originalPaths) && originalPaths.All(x => File.GetLastWriteTimeUtc(x) == times[x]), "completed continuation keeps exactly the original payload paths and timestamps");
        Check(secondDone.CanonicalId == first.Id && secondDone.LatestRunId == second.Id, "fresh intake links original ID and current run ID");
        Check(File.Exists(first.OriginalPhoto) && File.Exists(second.OriginalPhoto) && first.OriginalPhoto != second.OriginalPhoto, "both intake photos remain separately archived");
        Check(File.Exists(Path.Combine(disk, label, "盘面照片", first.Id + ".jpg")) && File.Exists(Path.Combine(disk, label, "盘面照片", second.Id + ".jpg")), "both photos also appear in the existing project");
        Entry origin = secondStation.Book.ReadOrigin(secondDone, first.Id);
        Check(origin.Status == Workstation.Done && origin.LatestRunId == second.Id && origin.ReusedFiles == 2, "original shared receipt reports latest completed verification");
        Check(origin.Report == secondDone.Report && File.Exists(Path.Combine(origin.Report, "路径清单.tsv")), "original receipt links to the actual current-run report");
        Check(File.ReadAllText(Path.Combine(secondDone.Report, "路径清单.tsv")).Contains("SHA256核实沿用"), "actual report records hash-verified reuse");

        // Closing a real non-modal choice must not silently accept the suggested old disc.
        Workstation canceledStation = Station(Source("complete-disc-Z"), @"Z:\", label);
        Entry canceledIntake = Register(canceledStation, disk);
        canceledStation.ChooseMatch = delegate(Entry intake, List<DiscMatch> choices, CancellationToken token)
        {
            Task<DiscMatch> answer = ArchiveChoices.Match(owner, intake.Drive, intake.Id, choices, token);
            Form window = owner.OwnedForms.Single(); window.Location = owner.Location; window.Close(); return answer;
        };
        Entry canceled = Copy(canceledStation);
        Check(canceled.Status == Workstation.Review && canceled.Files == 0 && String.IsNullOrEmpty(canceled.CanonicalId), "closing match choice leaves a review entry and never consents to merge");
        Check(Payloads(disk).SequenceEqual(originalPaths) && canceledStation.Book.ReadOrigin(canceled, first.Id).LatestRunId == second.Id, "canceling leaves original data and latest outcome unchanged");
        Check(File.Exists(canceledIntake.OriginalPhoto), "canceling preserves the newly registered photo");

        Workstation separateStation = Station(Source("complete-disc-W"), @"W:\", label);
        Entry separate = Register(separateStation, disk);
        bool separatelyChosen = false;
        separateStation.ChooseMatch = delegate(Entry intake, List<DiscMatch> choices, CancellationToken token)
        { Check(choices.Any(x => x.Id == first.Id), "old disc is available before explicit separate-disc choice"); separatelyChosen = true; return Task.FromResult<DiscMatch>(null); };
        Entry separateDone = Copy(separateStation);
        Check(separatelyChosen && separateDone.Status == Workstation.Done && separateDone.CanonicalId == separate.Id && separateDone.ReusedFiles == 0, "explicit new-disc choice creates an independent canonical record");
        Check(Payloads(disk).Length == 4 && originalPaths.All(File.Exists), "separate disc stores its own files while preserving the first disc");
        Check(separateStation.Book.ReadOrigin(separateDone, first.Id).LatestRunId == second.Id, "independent disc does not overwrite the first disc outcome");
    }
    private static void PartialThenThreeVerifications()
    {
        string disk = Dir("partial-target"), label = "180722-合成庆典实录";
        string source = Source("partial-disc-first");
        Workstation firstStation = Station(source, @"P:\", label);
        Entry first = Register(firstStation, disk), partial;
        using (FileStream locked = File.Open(Path.Combine(source, "second.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            partial = Copy(firstStation);
        Check(partial.Status == Workstation.Partial && partial.Files == 1 && partial.Failed == 1, "real exclusive source-file lock produces a partial first attempt");
        string firstPath = Payloads(disk).Single();
        DateTime preserved = File.GetLastWriteTimeUtc(firstPath);
        Check(File.Exists(Path.Combine(partial.JobDir, "continuation-v1.tsv")), "partial attempt writes persistent continuation journal");
        for (int run = 0; run < 3; run++)
        {
            Workstation reopened = Station(Source("partial-disc-reinsert-" + run), new[] { @"Q:\", @"R:\", @"S:\" }[run], label);
            Entry intake = Register(reopened, disk);
            Select(reopened, first.Id, delegate(DiscMatch match) { Check(match.Status == (run == 0 ? Workstation.Partial : Workstation.Done), "fresh discovery reads current original status"); });
            Entry result = Copy(reopened);
            Check(result.Status == Workstation.Done && result.Files == 2 && result.Failed == 0, "restart verification " + (run + 1) + " completes all source files");
            Check(result.ReusedFiles == (run == 0 ? 1 : 2), "restart verification " + (run + 1) + " copies only missing files");
            Check(Payloads(disk).Length == 2 && File.GetLastWriteTimeUtc(firstPath) == preserved, "restart verification " + (run + 1) + " keeps one payload tree without recopying recovered file");
            Entry original = reopened.Book.ReadOrigin(result, first.Id);
            Check(original.Status == Workstation.Done && original.Failed == 0 && original.LatestRunId == intake.Id && original.Report == result.Report, "restart verification " + (run + 1) + " refreshes original outcome and latest report link");
            Check(File.ReadAllBytes(Path.Combine(((Drive)reopened.Reader).Source, "second.bin")).SequenceEqual(File.ReadAllBytes(Payloads(disk).Single(x => Path.GetFileName(x) == "second.bin"))), "recovered missing file matches actual source bytes");
        }
        Check(Directory.GetFiles(Path.Combine(disk, label, "盘面照片"), "*.jpg").Length == 4, "initial and all three verification photos remain in project");
    }
    private static void ConfirmedProjectAlias()
    {
        string disk = Dir("alias-target"), known = "130708-星河访谈素材", proposed = "130708-星河访谈素才";
        Directory.CreateDirectory(Path.Combine(disk, known));
        Workstation first = Station(Source("alias-disc-first"), @"J:\", proposed);
        int decisions = 0;
        first.ChooseProject = delegate(Entry intake, string suggestion, string[] options, CancellationToken token)
        { Check(suggestion == proposed && options.Contains(known), "similar project is advisory and offers known directory"); decisions++; return Task.FromResult(known); };
        Register(first, disk); Entry firstDone = Copy(first);
        Check(decisions == 1 && firstDone.Status == Workstation.Done && firstDone.Project == known, "explicit project decision uses known project");
        Check(!Directory.Exists(Path.Combine(disk, proposed)), "explicit project decision creates no misspelled project tree");
        string source = Source("alias-disc-next");
        File.Move(Path.Combine(source, "first.bin"), Path.Combine(source, "different.bin"));
        File.AppendAllText(Path.Combine(source, "second.bin"), "different disc");
        Workstation next = Station(source, @"K:\", proposed);
        next.ChooseMatch = delegate { return Task.FromResult<DiscMatch>(null); };
        next.ChooseProject = delegate { throw new Exception("Confirmed alias unexpectedly required a second project decision"); };
        Register(next, disk); Entry nextDone = Copy(next);
        Check(nextDone.Status == Workstation.Done && nextDone.Project == known, "independent restarted workstation reuses confirmed target-local alias");
        Check(ProjectAliases.Inspect(disk, proposed, CancellationToken.None).Resolved == known, "alias receipt resolves exact proposed name to known project");
        Check(!Directory.Exists(Path.Combine(disk, proposed)), "remembered alias still avoids duplicate project folder");
    }
    private static void ResumeLegacyByKnownId()
    {
        string disk = Dir("known-id-target"), project = "190823-合成晚会纪实";
        string source = Source("known-id-disc-new-drive");
        Catalog oldBook = new Catalog(Dir("legacy-registration-state"));
        // Historical absolute drive paths are text only. No H: or G: device is opened.
        Entry historical = oldBook.Create(photo, disk, @"H:\", "");
        string projectDirectory = Directory.CreateDirectory(Path.Combine(disk, project)).FullName;
        string wrongPath = Path.Combine(projectDirectory, "first.bin");
        byte[] correct = File.ReadAllBytes(Path.Combine(source, "first.bin"));
        byte[] wrong = (byte[])correct.Clone(); wrong[0] ^= 0x3f;
        File.WriteAllBytes(wrongPath, wrong);
        string legacyReport = Directory.CreateDirectory(Path.Combine(historical.JobDir, "抢救-legacy")).FullName;
        // Shipped old path-list format: full source/destination paths, no digest.
        // It contains only one supposedly complete file; second.bin has no historical row.
        File.WriteAllText(Path.Combine(legacyReport, "路径清单.tsv"),
            "类型\t来源\t目标\t说明\r\n文件\t" + @"H:\first.bin".Replace("\\", "\\\\") + "\t" + wrongPath.Replace("\\", "\\\\") + "\t完整 " + wrong.Length + " 字节\r\n");
        historical.Status = Workstation.Partial; historical.Project = project;
        historical.ArchiveFolders = new[] { project }; historical.DiscLabel = project;
        historical.Report = legacyReport; historical.Files = 1; historical.Bytes = wrong.Length; historical.Failed = 1;
        historical.Note = "合成旧版记录：一项已登记，一项未完成；路径清单无内容摘要。";
        oldBook.Save(historical);
        Check(!File.Exists(Path.Combine(historical.JobDir, "continuation-v1.tsv")), "historical fixture has no modern continuation journal");
        Check(!Directory.Exists(Path.Combine(disk, ".discarchive-identity-v1")), "historical fixture has no modern strong identity");

        Workstation resumed = Station(source, @"N:\", project);
        resumed.ChooseMatch = delegate { throw new Exception("Known-ID resume must not require photo match selection"); };
        Entry intake = resumed.ResumeKnown(disk, historical.Id, @"N:\");
        Check(intake.CanonicalId == historical.Id && intake.Id != historical.Id && intake.Drive == @"N:\", "known-ID resume on independent workstation uses original ID and changed source drive");
        Check(File.ReadAllBytes(intake.OriginalPhoto).SequenceEqual(File.ReadAllBytes(historical.OriginalPhoto)), "known-ID resume obtains existing archived photo without new shutter input");
        Entry result = Copy(resumed);
        Check(result.Status == Workstation.Done && result.Files == 2 && result.Failed == 0 && result.ReusedFiles == 0, "old no-digest report resumes all files and never trusts same length");
        Check(File.ReadAllBytes(wrongPath).SequenceEqual(wrong), "same-length wrong historical copy is preserved byte for byte");
        string[] payloads = Directory.GetFiles(projectDirectory).OrderBy(x => x).ToArray();
        Check(payloads.Length == 3, "known-ID recovery preserves old object plus exactly two valid payload files");
        foreach (string original in Directory.GetFiles(source, "*.bin"))
        {
            byte[] bytes = File.ReadAllBytes(original);
            Check(payloads.Count(path => File.ReadAllBytes(path).SequenceEqual(bytes)) == 1, "known-ID recovery includes exactly one correct copy of " + Path.GetFileName(original));
        }
        Check(Directory.Exists(Path.Combine(projectDirectory, "empty-folder")), "known-ID recovery enumerates source and preserves missing empty folder");
        Entry origin = resumed.Book.ReadOrigin(result, historical.Id);
        Check(origin.Status == Workstation.Done && origin.Files == 2 && origin.Failed == 0 && origin.LatestRunId == intake.Id && origin.Report == result.Report, "known-ID recovery updates original summary and latest-run report");
        Check(File.Exists(Path.Combine(origin.Report, "路径清单.tsv")) && File.Exists(Path.Combine(historical.JobDir, "续导结果.xml")), "known-ID recovery publishes final report and original continuation receipt");
        Check(File.Exists(Path.Combine(disk, ".discarchive-identity-v1", "records", historical.Id + ".txt")), "successful old-record recovery establishes strong identity");

        // A manual ID is not permission to use an incompatible disc after identity exists.
        string incompatible = Source("known-id-incompatible-disc");
        File.WriteAllBytes(Path.Combine(incompatible, "first.bin"), wrong);
        Workstation rejected = Station(incompatible, @"T:\", project);
        string previousReport = origin.Report, previousLatest = origin.LatestRunId;
        byte[][] previousBytes = payloads.Select(File.ReadAllBytes).ToArray();
        rejected.ResumeKnown(disk, historical.Id, @"T:\");
        Entry refusal = Copy(rejected);
        Check(refusal.Status == Workstation.Review && refusal.Files == 0 && refusal.Note.Contains("冲突"), "manually entering known ID clearly rejects conflicting optical content");
        Entry afterRefusal = rejected.Book.ReadOrigin(refusal, historical.Id);
        Check(afterRefusal.Status == Workstation.Done && afterRefusal.Files == 2 && afterRefusal.Failed == 0 && afterRefusal.LatestRunId == previousLatest && afterRefusal.Report == previousReport, "wrong-disc refusal does not downgrade original completed history");
        Check(Directory.GetFiles(projectDirectory).OrderBy(x => x).SequenceEqual(payloads) && payloads.Select((path, index) => File.ReadAllBytes(path).SequenceEqual(previousBytes[index])).All(x => x), "wrong-disc refusal leaves every original payload unchanged");
    }
    private static T Find<T>(Control rootControl, string text) where T : Control
    {
        foreach (Control child in rootControl.Controls)
        {
            T found = child as T;
            if (found != null && (text == null || found.Text == text)) return found;
            found = Find<T>(child, text); if (found != null) return found;
        }
        return null;
    }
    [STAThread]
    public static int Main(string[] args)
    {
        // Any argument also exercises permanent paths longer than 260 characters.
        // Run with the shipped manifest and a copy of app.config beside the test executable.
        root = Path.Combine(Path.GetTempPath(), args.Length > 0 ? "DiscArchive-ContinuationWorkflow-" + Guid.NewGuid().ToString("N") + "-" + new string('L', 64) : "DA-CW-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            photo = Path.Combine(root, "synthetic-disc-photo.jpg");
            using (Bitmap bitmap = new Bitmap(240, 160)) { using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Azure); bitmap.Save(photo, ImageFormat.Jpeg); }
            Rectangle screen = SystemInformation.VirtualScreen;
            using (Form owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(screen.Right + 4000, screen.Bottom + 4000), ShowInTaskbar = false })
            {
                Exception failure = null;
                owner.Shown += delegate
                {
                    owner.BeginInvoke(new Action(delegate
                    {
                        try { CompleteAndCrossStation(owner); PartialThenThreeVerifications(); ConfirmedProjectAlias(); ResumeLegacyByKnownId(); }
                        catch (Exception error) { failure = error; }
                        finally { owner.Close(); }
                    }));
                };
                Application.Run(owner);
                if (failure != null) throw failure;
            }
            Console.WriteLine("Continuation workflow checks: " + checks + " / evidence: " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Evidence: " + root); return 1; }
    }
}
