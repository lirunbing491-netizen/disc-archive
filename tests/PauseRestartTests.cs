using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using DiscArchive;

// Two independently started Windows processes, not two objects in one process.
// Only the generated fixture is readable through the synthetic optical reader.
public static class PauseRestartTests
{
    private const string Label = "130708-暂停跨天续导合成素材";
    private sealed class SyntheticReader : IReader
    {
        internal string Root, Letter;
        public string[] Drives() { return new[] { Letter }; }
        public bool Empty(string letter) { return false; }
        public bool Ready(string letter) { return letter == Letter; }
        public MountedDisc Open(string letter)
        {
            if (letter != Letter) throw new IOException("Synthetic drive unavailable.");
            return new MountedDisc(Root, Label, delegate {
                if (!Directory.Exists(Root)) throw new IOException("Synthetic medium removed.");
            }, null);
        }
    }
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static string Q(string value) { return "\"" + value + "\""; }
    private static string Hash(string path)
    { using (SHA256 hash = SHA256.Create()) using (FileStream input = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""); }
    private static T Pump<T>(Task<T> task)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 40) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic STA workflow exceeded 40 seconds.");
        return task.GetAwaiter().GetResult();
    }
    private static string[] Payloads(string target)
    { return Directory.GetFiles(target, "*.bin", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(); }
    private static void Guard(string root)
    {
        string full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(full, "synthetic-pause-restart.fixture")))
            throw new IOException("Only a generated temporary fixture is allowed.");
    }
    private static Workstation Station(string root, bool second)
    {
        string state = Path.Combine(root, second ? "machine-two-state" : "machine-one-state");
        SyntheticReader reader = new SyntheticReader { Root = Path.Combine(root, second ? "disc-Y" : "disc-X"), Letter = second ? @"Y:\" : @"X:\" };
        return new Workstation(new Catalog(state), reader);
    }
    private static void Pause(string root, Form ui)
    {
        Workstation station = Station(root, false);
        string target = Path.Combine(root, "backup"), photo = Path.Combine(root, "label.jpg");
        Entry registration = station.Register(photo, target, "", @"X:\", "", true);
        int uiThread = Thread.CurrentThread.ManagedThreadId;
        bool stoppedOnUi = false, gateEntered = false;
        string firstPath = null, fragmentPath = null;
        Exception uiError = null;
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            Entry paused = Pump(station.Copy(delegate(string message) {
                string[] complete = Payloads(target);
                if (complete.Length != 1 || gateEntered) return;
                int position = message.LastIndexOf("已读 ", StringComparison.Ordinal);
                if (position < 0) return;
                string[] amounts = message.Substring(position + 3).Split('/');
                long read, length;
                if (amounts.Length != 2 || !Int64.TryParse(amounts[0].Trim(), out read) ||
                    !Int64.TryParse(amounts[1].Replace("字节", "").Trim(), out length)) return;
                // A short delay at the next file's start enables the real periodic
                // progress check after its first full buffer, before EOF/publication.
                if (read == 0) { Thread.Sleep(1100); return; }
                if (read >= length) return;
                string[] partials = Directory.GetFiles(registration.JobDir, "*.partial", SearchOption.AllDirectories);
                if (partials.Length != 1 || new FileInfo(partials[0]).Length == 0) return;
                gateEntered = true; firstPath = complete[0]; fragmentPath = partials[0];
                ui.BeginInvoke(new Action(delegate {
                    try {
                        if (Thread.CurrentThread.ManagedThreadId != uiThread) throw new Exception("Stop did not run on the UI thread.");
                        station.Stop(); stoppedOnUi = true;
                    } catch (Exception error) { uiError = error; }
                    finally { release.Set(); }
                }));
                if (!release.WaitOne(10000)) throw new TimeoutException("STA Stop did not release the synthetic read gate.");
            }));
            if (uiError != null) throw uiError;
            Check(gateEntered && stoppedOnUi, "process one calls Stop on STA while a real partial file is held at the read gate");
            Check(paused.Status == Workstation.Paused && paused.Files == 1 && paused.Bytes == 3L * 1024 * 1024 && paused.ReusedFiles == 0 && paused.Failed == 0,
                "process one returns paused with one durable complete file and excludes unfinished bytes");
            Check(!station.Busy && station.Current == null && Payloads(target).Length == 1, "paused workstation ends cleanly without publishing the second file");
            long partialLength = new FileInfo(fragmentPath).Length;
            Check(partialLength > 0 && partialLength < 3L * 1024 * 1024, "unfinished file retains an actual partial buffer");
            // Poison only the generated incomplete buffer. An implementation that
            // resumes by appending this fragment instead of reading from byte zero
            // will now fail the second process's full-source hash comparison.
            using (FileStream partial = new FileStream(fragmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                int firstByte = partial.ReadByte(); partial.Position = 0; partial.WriteByte((byte)(firstByte ^ 255)); partial.Flush(true);
            }
            Entry stored = station.Book.List().Single();
            Check(stored.Id == registration.Id && stored.Status == Workstation.Paused && stored.Files == 1, "paused status and original ID are durably cataloged before process exit");
            new XElement("pause", new XAttribute("id", registration.Id), new XAttribute("pid", Process.GetCurrentProcess().Id),
                new XAttribute("first", firstPath), new XAttribute("hash", Hash(firstPath)), new XAttribute("ticks", File.GetLastWriteTimeUtc(firstPath).Ticks),
                new XAttribute("fragment", fragmentPath), new XAttribute("partialLength", partialLength), new XAttribute("partialHash", Hash(fragmentPath)))
                .Save(Path.Combine(root, "paused-receipt.xml"));
        }
    }
    private static void Restart(string root)
    {
        XElement receipt = XElement.Load(Path.Combine(root, "paused-receipt.xml"));
        string id = (string)receipt.Attribute("id"), first = (string)receipt.Attribute("first"), target = Path.Combine(root, "backup");
        Workstation station = Station(root, true);
        Check(station.Book.List().Count == 0, "process two starts with an empty independent local catalog");
        Entry intake = station.ResumeKnown(target, id, @"Y:\");
        Check(intake.Id != id && intake.CanonicalId == id && intake.Drive == @"Y:\", "ResumeKnown links a new machine and drive to the original disc ID");
        FileStream lockedSource = null;
        Entry done;
        try {
            done = Pump(station.Copy(delegate(string message) {
                // Identity sampling has completed. A strong match permits reuse by
                // hashing the target; exclusively hold this source to prove it is not reread.
                if (lockedSource == null && message.StartsWith("归档位置已确定：", StringComparison.Ordinal))
                    lockedSource = new FileStream(Path.Combine(root, "disc-Y", Path.GetFileName(first)), FileMode.Open, FileAccess.Read, FileShare.None);
            }));
        } finally { if (lockedSource != null) lockedSource.Dispose(); }
        Check(lockedSource != null && done.Status == Workstation.Done && done.Files == 2 && done.ReusedFiles == 1 && done.ReusedBytes == 3L * 1024 * 1024 && done.Bytes == 6L * 1024 * 1024 && done.Failed == 0,
            "fresh process finishes two files by hashing and reusing one target while its source is locked");
        Check(done.CanonicalId == id && done.LatestRunId == intake.Id, "final receipt retains original canonical ID and separate insertion history");
        string[] payloads = Payloads(target);
        Check(payloads.Length == 2 && payloads.Contains(first) && payloads.All(x => Path.GetDirectoryName(x) == Path.GetDirectoryName(first)),
            "continuation produces exactly one payload tree with no second copy or extra source wrapper");
        Check(Hash(first) == (string)receipt.Attribute("hash") && File.GetLastWriteTimeUtc(first).Ticks == (long)receipt.Attribute("ticks"), "original completed payload bytes and modification time are untouched");
        foreach (string path in payloads)
            Check(new FileInfo(path).Length == 3L * 1024 * 1024 && Hash(path) == Hash(Path.Combine(root, "disc-Y", Path.GetFileName(path))), "delivered file matches full source from byte zero: " + Path.GetFileName(path));
        string fragment = (string)receipt.Attribute("fragment");
        Check(new FileInfo(fragment).Length == (long)receipt.Attribute("partialLength") && Hash(fragment) == (string)receipt.Attribute("partialHash"), "poisoned synthetic partial remains unchanged and cannot supply the completed file prefix");
        Check(File.ReadAllText(Path.Combine(done.Report, "路径清单.tsv")).Contains("SHA256核实沿用"), "actual second-process report records SHA256-verified reuse");
        Entry original = station.Book.LookupOriginal(target, id);
        Check(original.Id == id && original.Status == Workstation.Done && original.Files == 2 && original.ReusedFiles == 1 && original.LatestRunId == intake.Id,
            "original shared registration is updated to the completed total");
        Check(Process.GetCurrentProcess().Id != (int)receipt.Attribute("pid"), "restart ran in a genuinely different Windows process");
        new XElement("restart", new XAttribute("canonical", id), new XAttribute("status", done.Status), new XAttribute("files", done.Files),
            new XAttribute("reused", done.ReusedFiles), new XAttribute("pid", Process.GetCurrentProcess().Id)).Save(Path.Combine(root, "restarted-receipt.xml"));
    }
    private static void Child(string root, string mode)
    {
        string exe = Assembly.GetExecutingAssembly().Location;
        Check(File.Exists(exe + ".config"), "child uses the same executable runtime configuration as the shipped application");
        ProcessStartInfo info = new ProcessStartInfo(exe, mode + " " + Q(root)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (Process child = Process.Start(info)) {
            Task<string> output = Task.Run(() => child.StandardOutput.ReadToEnd());
            Task<string> errors = Task.Run(() => child.StandardError.ReadToEnd());
            if (!child.WaitForExit(65000)) { child.Kill(); child.WaitForExit(); throw new TimeoutException("Synthetic child exceeded 65 seconds."); }
            Console.Write(output.Result);
            Check(child.ExitCode == 0, "child exits normally: " + mode + " " + errors.Result);
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            if (args.Length == 2) {
                Guard(args[1]); Application.EnableVisualStyles();
                using (Form ui = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) }) {
                    Exception failure = null;
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    ui.Shown += delegate { ui.BeginInvoke(new Action(delegate {
                        try {
                            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && SynchronizationContext.Current is WindowsFormsSynchronizationContext, "worker owns a real STA WinForms synchronization context");
                            if (args[0] == "--pause-worker") Pause(args[1], ui);
                            else if (args[0] == "--restart-worker") Restart(args[1]);
                            else throw new ArgumentException("Unknown synthetic worker mode.");
                        } catch (Exception error) { failure = error; }
                        finally { ui.Close(); }
                    })); };
                    Application.Run(ui);
                    if (failure != null) throw failure;
                }
                return 0;
            }
            string root = Path.Combine(Path.GetTempPath(), "DiscArchive-PauseRestart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "synthetic-pause-restart.fixture"), "temporary synthetic media only");
            Directory.CreateDirectory(Path.Combine(root, "backup"));
            foreach (string source in new[] { "disc-X", "disc-Y" }) {
                string folder = Directory.CreateDirectory(Path.Combine(root, source)).FullName;
                for (int i = 1; i <= 2; i++) {
                    byte[] bytes = new byte[3 * 1024 * 1024]; for (int j = 0; j < bytes.Length; j++) bytes[j] = (byte)((j * 17 + i * 43) % 251);
                    File.WriteAllBytes(Path.Combine(folder, "00" + i + ".bin"), bytes);
                }
            }
            using (Bitmap bitmap = new Bitmap(160, 100)) { using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White); bitmap.Save(Path.Combine(root, "label.jpg"), ImageFormat.Jpeg); }
            Console.WriteLine("Synthetic two-process fixture: " + root);
            Child(root, "--pause-worker"); Child(root, "--restart-worker");
            Check(File.Exists(Path.Combine(root, "paused-receipt.xml")) && File.Exists(Path.Combine(root, "restarted-receipt.xml")), "both independent process receipts are retained for inspection");
            Console.WriteLine("PASS two-process STA pause, normal exit, fresh-machine ResumeKnown and verified continuation.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
