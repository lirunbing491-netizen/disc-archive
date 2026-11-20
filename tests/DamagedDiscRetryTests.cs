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

// Generated temporary media only. Failures are stream read errors after a real
// output buffer, rather than exclusive-open locks or cancellation callbacks.
internal static class DamagedDiscRetryTests
{
    private sealed class ReadFault : Stream
    {
        private readonly Stream inner;
        private long delivered;
        internal ReadFault(Stream stream) { inner = stream; }
        public override int Read(byte[] bytes, int offset, int count)
        {
            if (delivered >= 1024 * 1024) throw new IOException("Synthetic damaged sector after first MiB");
            int n = inner.Read(bytes, offset, (int)Math.Min(count, 1024 * 1024 - delivered));
            delivered += n; return n;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] bytes, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Media : DiscSource
    {
        internal string Root, Bad;
        internal bool CopyPhase, Damage, SampleUnavailable;
        internal int FaultOpens;
        internal int CopyOpens;
        internal override bool Native { get { return true; } }
        internal override IEnumerable<string> Entries(string dir) { return Directory.GetFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal); }
        internal override FileAttributes Attributes(string path) { return File.GetAttributes(path); }
        internal override long Length(string path) { return new FileInfo(path).Length; }
        internal override DateTime Modified(string path) { return File.GetLastWriteTime(path); }
        internal override Stream OpenRead(string path)
        {
            if (CopyPhase) CopyOpens++;
            if (Path.GetFileName(path) == Bad && !CopyPhase && SampleUnavailable) throw new IOException("Synthetic unreadable identity sample");
            Stream stream = File.OpenRead(path);
            if (Path.GetFileName(path) == Bad && CopyPhase && Damage) { FaultOpens++; return new ReadFault(stream); }
            return stream;
        }
    }
    private sealed class Reader : IReader
    {
        internal Media Media;
        public string[] Drives() { return new[] { @"X:\" }; }
        public bool Empty(string drive) { return false; }
        public bool Ready(string drive) { return true; }
        public MountedDisc Open(string drive)
        {
            Media.CopyPhase = false;
            return new MountedDisc(Media.Root, "190823-合成坏盘重试", delegate { if (!Directory.Exists(Media.Root)) throw new IOException("Fixture removed"); }, null, Media);
        }
    }
    private static string root, photo;
    private static int checks;
    private static void Check(bool yes, string message)
    { if (!yes) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static string Dir(string path) { return Directory.CreateDirectory(Path.Combine(root, path)).FullName; }
    private static Entry Copy(Workstation station, Media media)
    {
        Task<Entry> task = station.Copy(delegate(string text) { if (text.StartsWith("归档位置已确定：", StringComparison.Ordinal)) media.CopyPhase = true; });
        Stopwatch timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 30) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException("Generated workflow exceeded 30 seconds");
        return task.GetAwaiter().GetResult();
    }
    private static string[] Payloads(string target) { return Directory.GetFiles(target, "*.bin", SearchOption.AllDirectories).OrderBy(x => x).ToArray(); }
    private static void RetryDamaged(string name, string bad, bool weak)
    {
        string source = Dir(name + "-source"), target = Dir(name + "-target");
        foreach (string file in new[] { "001.bin", "002.bin", "003.bin" }) {
            byte[] bytes = new byte[3 * 1024 * 1024]; int salt = file[2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 17 + salt * 31) % 251);
            File.WriteAllBytes(Path.Combine(source, file), bytes);
        }
        Media media = new Media { Root = source, Bad = bad, Damage = true, SampleUnavailable = weak };
        Workstation station = new Workstation(new Catalog(Dir(name + "-state")), new Reader { Media = media });
        Entry intake = station.Register(photo, target, "", @"X:\", "", true);
        Entry first = Copy(station, media);
        Check(first.Status == Workstation.Partial && first.Files == 2 && first.Bytes == 6L * 1024 * 1024 && first.Failed == 1 && media.FaultOpens == 1,
            name + ": one midstream source failure produces partial, excludes fragment bytes, and continues copying");
        string[] originals = Payloads(target);
        Check(originals.Length == 2 && originals.Any(x => Path.GetFileName(x) == "003.bin") && !originals.Any(x => Path.GetFileName(x) == bad), name + ": later healthy sibling is published, failed file is absent");
        string fragment = Directory.GetFiles(intake.JobDir, "*.partial", SearchOption.AllDirectories).Single();
        Check(new FileInfo(fragment).Length == 1024 * 1024 && File.ReadAllText(Path.Combine(first.Report, "失败清单.tsv")).Contains("Synthetic damaged sector"), name + ": durable first-MiB fragment and explicit failure receipt exist");
        // Poison generated incomplete bytes: appending/reusing the prefix cannot pass.
        using (FileStream file = File.Open(fragment, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { int b = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(b ^ 255)); file.Flush(true); }
        byte[] fragmentBytes = File.ReadAllBytes(fragment);
        Dictionary<string, DateTime> timestamps = originals.ToDictionary(x => x, File.GetLastWriteTimeUtc);
        string previousReport = first.Report;
        media.Damage = false; media.SampleUnavailable = false; media.CopyOpens = 0;
        station.Retry(first, @"X:\");
        Entry done = Copy(station, media);
        Check(done.Status == Workstation.Done && done.Files == 3 && done.Failed == 0 && done.ReusedFiles == 2 && done.ReusedBytes == 6L * 1024 * 1024,
            name + ": normal Retry recovers failed file and reuses two complete files");
        Check(media.CopyOpens == (weak ? 3 : 1), name + (weak ? ": incomplete identity requires full source hashes before reuse" : ": strong identity reuses healthy targets without recopying sources"));
        Check(Payloads(target).Length == 3 && originals.All(x => File.GetLastWriteTimeUtc(x) == timestamps[x]), name + ": one payload tree and untouched complete-file timestamps");
        foreach (string file in Payloads(target)) Check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(source, Path.GetFileName(file)))), name + ": full source bytes verified " + Path.GetFileName(file));
        Check(File.ReadAllBytes(fragment).SequenceEqual(fragmentBytes) && done.Report != previousReport && File.Exists(Path.Combine(previousReport, "失败清单.tsv")), name + ": poisoned fragment and historical failure receipt remain unchanged");
        Entry stored = station.Book.List().Single();
        Check(stored.Id == intake.Id && stored.Status == Workstation.Done && stored.Files == 3 && stored.ReusedFiles == 2, name + ": original catalog entry reflects recovered total");

        // Once a complete identity exists, known-ID retry must refuse changed size
        // or sampled contents before touching any delivered payload or old outcome.
        string report = done.Report;
        byte[][] delivered = Payloads(target).Select(File.ReadAllBytes).ToArray();
        string changed = Path.Combine(source, bad);
        byte[] original = File.ReadAllBytes(changed);
        original[0] ^= 63; File.WriteAllBytes(changed, original);
        station.Retry(done, @"X:\"); Entry conflict = Copy(station, media);
        Check(conflict.Status == Workstation.Review && conflict.Files == 0 && conflict.Note.Contains("冲突"), name + ": changed sampled content is refused by normal retry");
        File.AppendAllText(changed, "synthetic-size-change");
        station.Retry(done, @"X:\"); Entry sizeConflict = Copy(station, media);
        Check(sizeConflict.Status == Workstation.Review && sizeConflict.Note.Contains("冲突"), name + ": changed file length is refused by normal retry");
        Check(Payloads(target).Select((path, index) => File.ReadAllBytes(path).SequenceEqual(delivered[index])).All(x => x) && station.Book.List().Single().Report == report && station.Book.List().Single().Status == Workstation.Done,
            name + ": refusal preserves all payloads and original completed summary");
    }
    [STAThread]
    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "DiscArchive-DamagedRetry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            photo = Path.Combine(root, "generated-photo.jpg");
            using (Bitmap image = new Bitmap(80, 50)) { using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.White); image.Save(photo, ImageFormat.Jpeg); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            RetryDamaged("early-strong", "001.bin", false);
            RetryDamaged("middle-strong", "002.bin", false);
            RetryDamaged("middle-incomplete-identity", "002.bin", true);
            Console.WriteLine("PASS " + checks + " damaged-disc retry checks. Evidence: " + root); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Evidence: " + root); return 1; }
    }
}
