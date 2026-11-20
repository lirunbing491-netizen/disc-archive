using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscArchive;

// Only fresh temporary fixtures are opened. Drive letters are synthetic labels.
// Run on a normal STA with the real Windows Forms synchronization context;
// Reader.Open and RescueCopy must still run on the thread pool.
internal static class CloseOutcomeTests
{
    private const string Label = "130708-关闭异常合成素材";
    private const string CloseReason = "SYNTHETIC_CLOSE_IO_FAILURE";
    private const string InnerReason = "SYNTHETIC_INNER_DEVICE_FAILURE";
    private const string VerifyReason = "SYNTHETIC_FINAL_VERIFY_FAILURE";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string root, photo;
    private static int serial, checks, uiThread;

    private sealed class BrokenLease : IDisposable
    {
        internal int Disposals;
        internal bool Broken = true;
        public void Dispose()
        {
            Disposals++;
            if (Broken) throw new IOException(CloseReason, new IOException(InnerReason, unchecked((int)0x8007001f)));
        }
    }
    private sealed class SyntheticReader : IReader
    {
        internal Func<IOpticalEjectTicket> Prepare;
        internal string Source, Coverage;
        internal string Drive = @"X:\";
        internal int OpenThread, VerifyCalls;
        internal bool OpenInPool, VerifyInPool;
        internal Action OnVerify;
        internal readonly BrokenLease Lease = new BrokenLease();
        public string[] Drives() { return new[] { Drive }; }
        public bool Empty(string drive) { return false; }
        public bool Ready(string drive) { return drive == Drive; }
        public MountedDisc Open(string drive)
        {
            if (drive != Drive) throw new IOException("Synthetic drive unavailable");
            OpenThread = Thread.CurrentThread.ManagedThreadId;
            OpenInPool = Thread.CurrentThread.IsThreadPoolThread;
            MountedDisc disc = new MountedDisc(Source, Label, delegate {
                VerifyCalls++;
                VerifyInPool |= Thread.CurrentThread.IsThreadPoolThread;
                if (OnVerify != null) OnVerify();
            }, Lease);
            disc.UncoveredTracks = Coverage;
            disc.PrepareEject = Prepare;
            return disc;
        }
    }
    private sealed class RecordingTicket : IOpticalEjectTicket
    {
        internal int Calls, Disposals;
        internal bool Refuse, HasReceipt, ClosedSource, InPool;
        internal readonly Fixture Fixture;
        internal RecordingTicket(Fixture f){Fixture=f;}
        public bool TryEject(out string reason)
        {
            Calls++;
            InPool=Thread.CurrentThread.IsThreadPoolThread;
            ClosedSource=Fixture.Reader.Lease.Disposals==1;
            var e=Fixture.Intake;
            HasReceipt=File.Exists(Path.Combine(e.JobDir,"续导结果.xml")) &&
                Fixture.Station.Book.List().Single(x=>x.Id==e.Id).Status==Workstation.Done &&
                Fixture.Station.Book.ReadOrigin(e,e.Id).Status==Workstation.Done;
            reason=Refuse ? "SYNTHETIC_DEVICE_BUSY" : "";
            return !Refuse;
        }
        public void Dispose(){Disposals++;}
    }
    private sealed class Fixture
    {
        internal string Target, State;
        internal SyntheticReader Reader;
        internal Workstation Station;
        internal Entry Intake;
    }
    private sealed class QuietOwner : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
    }
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static string Dir(string name)
    { return Directory.CreateDirectory(Path.Combine(root, name)).FullName; }
    private static Fixture NewFixture(string name, bool broken, string drive = @"X:\")
    {
        string prefix = (++serial) + "-" + name;
        string source = Dir(prefix + "-source");
        File.WriteAllText(Path.Combine(source, "first.bin"), "first real synthetic payload");
        File.WriteAllText(Path.Combine(source, "second.bin"), "second real synthetic payload with another length");
        Directory.CreateDirectory(Path.Combine(source, "empty-folder"));
        Fixture f = new Fixture { Target = Dir(prefix + "-target"), State = Dir(prefix + "-state"), Reader = new SyntheticReader { Source = source, Drive = drive } };
        f.Reader.Lease.Broken = broken;
        f.Station = new Workstation(new Catalog(f.State), f.Reader, delegate { return ""; }, delegate { return "metadata"; });
        f.Intake = f.Station.Register(photo, f.Target, "", drive, "", true);
        return f;
    }
    private static T Pump<T>(Task<T> task)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 35) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic close-outcome workflow exceeded 35 seconds");
        return task.GetAwaiter().GetResult();
    }
    private static string[] Payloads(string target)
    { return Directory.GetFiles(target, "*.bin", SearchOption.AllDirectories); }
    private static string Progress(Entry e)
    { return File.ReadAllText(Path.Combine(e.Report, "进度.tsv")); }
    private static string LastProgress(Entry e)
    { return File.ReadAllLines(Path.Combine(e.Report, "进度.tsv")).Last(x => x.Length != 0); }
    private static string Warning(Entry e)
    {
        // Reflection permits compiling this regression against the pre-fix tree,
        // where the behavioral assertions fail before a field has been added.
        FieldInfo field = typeof(Entry).GetField("CloseWarning");
        return field == null ? "" : (string)field.GetValue(e) ?? "";
    }
    private static void CheckWarning(Fixture f, Entry e, bool targetAvailable = true)
    {
        string warning = Warning(e);
        Check(warning.Contains(CloseReason) && warning.Contains(InnerReason) && warning.IndexOf("8007001F", StringComparison.OrdinalIgnoreCase) >= 0,
            "separate cleanup warning retains outer, inner and HRESULT evidence");
        Check((e.Note ?? "").Contains("关闭") || (e.Note ?? "").Contains("收尾"), "human-readable outcome points to the separate cleanup warning");
        Entry stored = f.Station.Book.List().Single(x => x.Id == e.Id);
        Check(Warning(stored) == warning && stored.Status == e.Status && stored.Files == e.Files && stored.Failed == e.Failed,
            "catalog round-trip preserves cleanup warning separately from outcome and counts");
        string[] evidence = Directory.GetFiles(f.Intake.JobDir, "*", SearchOption.AllDirectories)
            .Where(x => new[] { ".txt", ".log", ".tsv" }.Contains(Path.GetExtension(x).ToLowerInvariant()))
            .Where(x => !new[] { "进度.tsv", "路径清单.tsv", "失败清单.tsv" }.Contains(Path.GetFileName(x)))
            .Where(x => File.ReadAllText(x).Contains(CloseReason)).ToArray();
        if (targetAvailable) Check(evidence.Any(x => File.ReadAllText(x).Contains(InnerReason) && File.ReadAllText(x).IndexOf("8007001F", StringComparison.OrdinalIgnoreCase) >= 0),
            "independent cleanup-warning text file is durable outside copy progress and failure rows");
        Check(!File.ReadAllText(Path.Combine(e.Report, "失败清单.tsv")).Contains(CloseReason),
            "cleanup exception is never fabricated as a source-file failure");
        Check(f.Reader.Lease.Disposals == 1 && !f.Station.Busy && f.Station.Current == null,
            "one failed close still releases workstation ownership exactly once");
        Check(f.Reader.OpenInPool && f.Reader.OpenThread != uiThread && f.Reader.VerifyInPool && f.Reader.VerifyCalls > 0,
            "source open and verification use worker threads under a live STA context");
    }
    private static Entry Copy(Fixture f, Action<string> progress)
    { return Pump(f.Station.Copy(progress ?? delegate { })); }
    private static void Complete(bool broken)
    {
        Fixture f = NewFixture(broken ? "close-only" : "control", broken);
        Entry e = Copy(f, null);
        long bytes = Directory.GetFiles(f.Reader.Source, "*.bin").Sum(x => new FileInfo(x).Length);
        Check(e.Status == Workstation.Done && e.Files == 2 && e.Bytes == bytes && e.Failed == 0 && e.ReusedFiles == 0,
            "fully written and finally confirmed data is completed with honest new-file counts (broken close=" + broken + ")");
        Check(Payloads(f.Target).Length == 2 && Directory.Exists(Path.Combine(f.Target, Label, "empty-folder")),
            "actual output preserves all files and the empty directory");
        foreach (string source in Directory.GetFiles(f.Reader.Source, "*.bin"))
            Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(Payloads(f.Target).Single(x => Path.GetFileName(x) == Path.GetFileName(source)))),
                "published bytes match " + Path.GetFileName(source));
        Check(LastProgress(e).StartsWith("完成\t2\t" + bytes + "\t0\t", StringComparison.Ordinal) && !Progress(e).Contains(CloseReason),
            "completed rescue log retains real two-file zero-failure result without invented read errors");
        Entry original = f.Station.Book.ReadOrigin(e, e.CanonicalId);
        Check(original.Status == Workstation.Done && original.Files == 2 && original.Failed == 0 && original.Report == e.Report,
            "original registration summary agrees with completed copy");
        if (broken) CheckWarning(f, e);
        else Check(String.IsNullOrEmpty(Warning(e)) && !e.Note.Contains(CloseReason), "normal close produces no stale cleanup warning");
    }
    private static void PartialRead()
    {
        Fixture f = NewFixture("source-failure", true);
        int preparations=0;
        f.Reader.Prepare=delegate {preparations++;return new RecordingTicket(f);};
        Entry e;
        using (FileStream locked = File.Open(Path.Combine(f.Reader.Source, "second.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            e = Copy(f, null);
        Check(e.Status == Workstation.Partial && e.Files == 1 && e.Failed == 1 && e.ReusedFiles == 0 && Payloads(f.Target).Length == 1,
            "real source read failure stays partial despite a second close exception");
        Check(LastProgress(e).StartsWith("完成\t1\t", StringComparison.Ordinal) && LastProgress(e).Split('\t')[3] == "1",
            "partial rescue log retains its one true failed entry");
        CheckWarning(f, e);
        Check(preparations==0,"partial results leave tray available for retry");
    }

    private static void EjectAfterReceipts(bool refuse,bool blockReceipt)
    {
        Fixture f=NewFixture("eject",false);
        var ticket=new RecordingTicket(f){Refuse=refuse};
        bool boundWhileReading=false;
        f.Reader.Prepare=delegate {
            boundWhileReading=Thread.CurrentThread.IsThreadPoolThread && f.Reader.Lease.Disposals==0;
            if(blockReceipt) Directory.CreateDirectory(Path.Combine(f.Intake.JobDir,"续导结果.xml"));
            return ticket;
        };
        var done=Copy(f,null);
        Check(done.Status==Workstation.Done && done.Failed==0 && done.Files==2,"eject or summary issue never rewrites verified payload status");
        Check(boundWhileReading && ticket.Disposals==1,"prepare with source alive and always dispose protected ticket");
        if(blockReceipt) Check(ticket.Calls==0 && f.Station.LastEjectNote.Contains("汇总"),"incomplete summary prevents ejection");
        else {
            Check(ticket.Calls==1 && ticket.ClosedSource && ticket.HasReceipt && ticket.InPool,"one eject only after source close and both receipts, off UI thread");
            Check(f.Station.LastEjectNote.Contains(refuse ? "SYNTHETIC_DEVICE_BUSY" : "已接受"),"operator sees actual eject outcome");
        }
    }

    private static void EjectOnlyFinishedDrive()
    {
        Fixture first=NewFixture("pair-good",false,@"X:\"), second=NewFixture("pair-bad",false,@"Y:\");
        var one=new RecordingTicket(first); var two=new RecordingTicket(second);
        first.Reader.Prepare=()=>one; second.Reader.Prepare=()=>two;
        using(File.Open(Path.Combine(second.Reader.Source,"second.bin"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            Entry[] results=Pump(Task.WhenAll(first.Station.Copy(delegate{}),second.Station.Copy(delegate{})));
            Check(results[0].Status==Workstation.Done && results[1].Status==Workstation.Partial,"each optical drive keeps its own result");
        }
        Check(one.Calls==1 && two.Calls==0 && first.Station.LastEjectAccepted && !second.Station.LastEjectAccepted,
            "only the completed drive receives the eject command while the partial disc stays inserted");
    }
    private static void Interrupted(Form owner, bool cancel, bool targetLoss)
    {
        Fixture f = NewFixture(cancel ? "cancel" : targetLoss ? "target-loss" : "last-verify", true);
        bool armed = false, triggered = false;
        string marker = Path.Combine(f.Target, ".discarchive-target.id"), heldMarker = marker + ".synthetic-held";
        f.Reader.OnVerify = delegate {
            if (!armed || triggered || cancel) return;
            triggered = true;
            if (targetLoss) File.Move(marker, heldMarker); // Generated fixture only; restored below.
            else throw new IOException(VerifyReason);
        };
        Entry e;
        try {
            e = Copy(f, delegate(string message) {
                int completed = Payloads(f.Target).Length;
                if (!message.StartsWith("抢救：", StringComparison.Ordinal) || armed || completed != (cancel ? 1 : 2)) return;
                // This report is emitted after publication and durable journaling.
                armed = true;
                if (!cancel) return;
                using (ManualResetEvent release = new ManualResetEvent(false)) {
                    owner.BeginInvoke(new Action(delegate {
                        triggered = Thread.CurrentThread.ManagedThreadId == uiThread;
                        f.Station.Stop(); release.Set();
                    }));
                    if (!release.WaitOne(10000)) throw new TimeoutException("UI cancellation did not reach worker gate");
                }
            });
        } finally { if (File.Exists(heldMarker)) File.Move(heldMarker, marker); }
        Check(armed && triggered, "late " + (cancel ? "UI cancel" : targetLoss ? "target identity loss" : "source verify failure") + " was actually injected");
        Check(e.Status == (cancel ? Workstation.Paused : Workstation.Partial) && e.Files == (cancel ? 1 : 2) && e.Failed == 0 && e.ReusedFiles == 0,
            "primary interruption and durable counts survive close exception");
        Check(cancel ? e.Note.Contains("停止") : targetLoss ? e.Note.Contains("硬盘") : e.Note.Contains(VerifyReason),
            "primary stop reason is retained alongside the cleanup reason");
        Check(!LastProgress(e).StartsWith("完成\t", StringComparison.Ordinal) && !Progress(e).Contains(CloseReason),
            "interrupted progress never claims completed rescue or invents cleanup read failures");
        CheckWarning(f, e, !targetLoss);
    }
    private static void UncoveredTracks()
    {
        Fixture f = NewFixture("uncovered-tracks", true);
        f.Reader.Coverage = "合成轨道 2 为音轨，尚未提取。";
        Entry e = Copy(f, null);
        Check(e.Status == Workstation.Partial && e.Files == 2 && e.Failed == 0 && e.Note.Contains(f.Reader.Coverage),
            "uncovered audio track remains partial even when every visible file copied");
        CheckWarning(f, e);
    }
    private static object Field(object value, string name)
    { return value.GetType().GetField(name, Private | BindingFlags.Public).GetValue(value); }
    private static object Call(object value, string name, params object[] args)
    { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
    private static void ActualNoticeAndOriginal()
    {
        Fixture first = NewFixture("original", false);
        Entry original = Copy(first, null);
        string state = Dir("independent-notice-workstation");
        SyntheticReader reader = new SyntheticReader { Source = first.Reader.Source };
        Rectangle screen = SystemInformation.VirtualScreen;
        Rectangle outside = new Rectangle(screen.Right + 3000, screen.Bottom + 3000, 1000, 800);
        using (CompletionNotice notice = new CompletionNotice(delegate { return outside; }, 10000))
        using (MainForm form = new MainForm(state, reader, notice))
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = outside.Location; form.ShowInTaskbar = false;
            form.Show(); ((System.Windows.Forms.Timer)Field(form, "timer")).Stop();
            Workstation station = (Workstation)Field(form, "station");
            Check(station.Book.List().Count == 0, "notification test uses a genuinely independent local catalog");
            Entry intake = station.ResumeKnown(first.Target, original.Id, @"X:\");
            Call(form, "ShowArmed", intake);
            object slot = Field(form, "selectedSlot");
            Pump((Task<bool>)WrapPoll(form, slot));
            Entry e = station.Book.List().Single(x => x.Id == intake.Id);
            Entry origin = station.Book.ReadOrigin(e, original.Id);
            Check(e.Status == Workstation.Done && e.Files == 2 && e.ReusedFiles == 2 && e.Failed == 0 && Payloads(first.Target).Length == 2,
                "independent continuation confirms two reused files and zero new writes despite close error");
            Check(origin.Status == e.Status && origin.Files == e.Files && origin.ReusedFiles == e.ReusedFiles && origin.LatestRunId == e.Id && origin.Report == e.Report,
                "original registration receives the latest true completion and report");
            Form popup = (Form)Field(notice, "driveWindow");
            Check(popup != null && popup.Visible && popup.AccessibleName == "已导过，核对完成，可以换盘" &&
                popup.AccessibleDescription.Contains("本次新拷 0") && popup.AccessibleDescription.Contains("沿用 2") && popup.AccessibleDescription.Contains("失败条目 0"),
                "real MainForm completion notification matches registry status and counts");
            Check(!popup.Bounds.IntersectsWith(screen), "synthetic notification remains outside the user's desktop");
            CheckWarning(new Fixture { Station = station, Reader = reader, Intake = intake }, e);
            form.Close();
        }
    }
    private static async Task<bool> WrapPoll(MainForm form, object slot)
    { await (Task)Call(form, "Poll", slot, true); return true; }
    [STAThread]
    public static int Main()
    {
        try {
            Application.EnableVisualStyles();
            uiThread = Thread.CurrentThread.ManagedThreadId;
            // Keep legacy .NET Framework fixture paths below MAX_PATH, including
            // the real rescue attempt and partial-file subdirectories.
            root = Path.Combine(Path.GetTempPath(), "DA-C-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            photo = Path.Combine(root, "synthetic-label.jpg");
            using (Bitmap image = new Bitmap(64, 64)) { using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.White); image.Save(photo, ImageFormat.Jpeg); }
            using (Form owner = new QuietOwner()) {
                owner.StartPosition = FormStartPosition.Manual; owner.Location = new Point(SystemInformation.VirtualScreen.Right + 5000, SystemInformation.VirtualScreen.Bottom + 5000);
                owner.ShowInTaskbar = false; owner.Show();
                Check(SynchronizationContext.Current is WindowsFormsSynchronizationContext && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
                    "tests preserve normal STA Windows Forms synchronization context");
                Complete(false); Complete(true); PartialRead();
                EjectAfterReceipts(false,false); EjectAfterReceipts(true,false); EjectAfterReceipts(false,true);
                EjectOnlyFinishedDrive();
                Interrupted(owner, true, false); Interrupted(owner, false, false); Interrupted(owner, false, true);
                UncoveredTracks(); ActualNoticeAndOriginal(); owner.Close();
            }
            Console.WriteLine("PASS " + checks + " close-outcome checks; synthetic evidence retained: " + root);
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Synthetic evidence: " + root); return 1; }
    }
}
