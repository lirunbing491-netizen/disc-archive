using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using DiscArchive;

public static class CopyTests
{
    private static string root;
    private static int passed;

    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "DiscArchiveCopyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("nested, empty, hidden, Unicode, streaming hashes", Success);
            Test("empty disc rejected", delegate { Scenario("empty", delegate(string s, string j) { Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j); }); });
            Test("special disc rejected", Special);
            Test("overlap rejected", Overlap);
            Test("pre-cancel rejected", PreCancel);
            Test("mid-copy cancel retained", Cancel);
            Test("source addition detected", SourceAddition);
            Test("source mutation detected", SourceMutation);
            Test("target tampering detected by final inventory", TargetTampering);
            Test("existing exports preserved", Existing);
            Test("late destination collision preserved", LateCollision);
            Test("locked source fails without success", Locked);
            Test("junction rejected", Junction);
            Test("media callback blocks initial reads", InitialMediaFailure);
            Test("media callback blocks target verification", VerificationMediaFailure);
            Test("media callback blocks final publication", PublicationMediaFailure);
            Test("final target reread detects same-length corruption", TargetCorruption);
            Test("DVD visible files end-to-end archive with empty AUDIO_TS and ROM", DvdVideo);
            Test("incomplete DVD VIDEO_TS rejected", InvalidDvdVideo);
            Test("VCD remains rejected alongside valid DVD", VcdRejected);
            Console.WriteLine("PASS " + passed + " tests; temporary fixture: " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Fails(Action body)
    {
        bool failed = false;
        try { body(); } catch (IOException) { failed = true; } catch (OperationCanceledException) { failed = true; }
        Assert(failed, "Expected failure");
    }
    private static void NoSuccess(string job) { Assert(!File.Exists(Path.Combine(job, "校验清单.tsv")), "Failure published successful manifest"); }
    private static void Scenario(string name, Action<string, string> body)
    {
        string dir = Path.Combine(root, name), source = Path.Combine(dir, "source"), job = Path.Combine(dir, "job");
        Directory.CreateDirectory(source); Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "照片与元数据.txt"), "existing metadata");
        body(source, job);
        Assert(File.ReadAllText(Path.Combine(job, "照片与元数据.txt")) == "existing metadata", "Metadata changed");
    }
    private static void Populate(string source) { File.WriteAllText(Path.Combine(source, "a.txt"), "original source"); }
    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void Success()
    {
        Scenario("success", delegate(string s, string j)
        {
            Directory.CreateDirectory(Path.Combine(s, "空目录", "子目录"));
            File.WriteAllText(Path.Combine(s, "中文.txt"), "导盘完整内容");
            File.WriteAllText(Path.Combine(s, ".dotfile"), "dot");
            File.WriteAllBytes(Path.Combine(s, "empty.bin"), new byte[0]);
            File.SetAttributes(Path.Combine(s, ".dotfile"), FileAttributes.Hidden);
            byte[] bytes = new byte[3 * 1024 * 1024 + 37]; new Random(19).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(s, "large.bin"), bytes);
            CopyResult result = DiscCopy.Run(s, j, null, CancellationToken.None);
            Assert(!result.DvdVideoFiles, "Data disc misclassified as DVD");
            Assert(result.Files == 4, "File count");
            long length = 0;
            string text = File.ReadAllText(Path.Combine(j, "校验清单.tsv"));
            foreach (string file in Directory.GetFiles(s))
            {
                length += new FileInfo(file).Length;
                string copied = Path.Combine(j, "原盘文件", Path.GetFileName(file));
                Assert(Hash(file) == Hash(copied), "Independent source/destination hash mismatch");
                Assert(text.Contains(Hash(file)), "Manifest hash missing");
            }
            Assert(length == result.Bytes, "Byte count");
            Assert(Directory.Exists(Path.Combine(j, "原盘文件", "空目录", "子目录")), "Empty directory lost");
            Assert(!Directory.Exists(Path.Combine(j, "未完成-原盘文件")), "Stage still exists");
            Assert(text.Contains("# 完成：4"), "Completion missing");
        });
    }
    private static void Special()
    {
        foreach (string name in new string[] { "video_ts", "BDMV", "VCD", "track01.cda" })
            Scenario("special-" + name, delegate(string s, string j) { Populate(s); File.WriteAllText(Path.Combine(s, name), "x"); Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j); });
    }
    private static void Overlap()
    {
        Scenario("overlap", delegate(string s, string j)
        {
            Populate(s); string nested = Path.Combine(s, "job"); Directory.CreateDirectory(nested);
            Fails(delegate { DiscCopy.Run(s, nested, null, CancellationToken.None); });
            Fails(delegate { DiscCopy.Run(s, s, null, CancellationToken.None); });
            Fails(delegate { DiscCopy.Run(s, Path.GetDirectoryName(s), null, CancellationToken.None); });
        });
    }
    private static void PreCancel()
    {
        Scenario("pre-cancel", delegate(string s, string j) { Populate(s); CancellationTokenSource c = new CancellationTokenSource(); c.Cancel(); Fails(delegate { DiscCopy.Run(s, j, null, c.Token); }); NoSuccess(j); });
    }
    private static void Cancel()
    {
        Scenario("cancel", delegate(string s, string j)
        {
            Populate(s); CancellationTokenSource c = new CancellationTokenSource();
            Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { c.Cancel(); }, c.Token); });
            NoSuccess(j); Assert(Directory.Exists(Path.Combine(j, "未完成-原盘文件")), "Missing preserved partial directory");
        });
    }
    private static void SourceAddition()
    {
        Scenario("addition", delegate(string s, string j) { Populate(s); Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { File.WriteAllText(Path.Combine(s, "new.txt"), "new"); }, CancellationToken.None); }); NoSuccess(j); });
    }
    private static void SourceMutation()
    {
        Scenario("mutation", delegate(string s, string j) { Populate(s); Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { File.AppendAllText(Path.Combine(s, "a.txt"), "changed"); }, CancellationToken.None); }); NoSuccess(j); });
    }
    private static void TargetTampering()
    {
        Scenario("tampering", delegate(string s, string j) { Populate(s); Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { File.WriteAllText(Path.Combine(j, "未完成-原盘文件", "foreign.txt"), "extra"); }, CancellationToken.None); }); NoSuccess(j); });
    }
    private static void Existing()
    {
        Scenario("existing", delegate(string s, string j) { Populate(s); string old = Path.Combine(j, "原盘文件"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "keep.txt"), "keep"); Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); Assert(File.ReadAllText(Path.Combine(old, "keep.txt")) == "keep", "Overwritten"); NoSuccess(j); });
    }
    private static void LateCollision()
    {
        Scenario("collision", delegate(string s, string j) { Populate(s); Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { Directory.CreateDirectory(Path.Combine(j, "原盘文件")); File.WriteAllText(Path.Combine(j, "原盘文件", "keep.txt"), "keep"); }, CancellationToken.None); }); NoSuccess(j); Assert(File.ReadAllText(Path.Combine(j, "原盘文件", "keep.txt")) == "keep", "Collision overwritten"); });
    }
    private static void Locked()
    {
        Scenario("locked", delegate(string s, string j) { Populate(s); using (FileStream locked = new FileStream(Path.Combine(s, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j); });
    }
    private static void Junction()
    {
        Scenario("junction", delegate(string s, string j)
        {
            Populate(s); string link = Path.Combine(s, "linked"), target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using (Process process = Process.Start(info)) { process.WaitForExit(); Assert(process.ExitCode == 0, "Junction creation failed: " + process.StandardError.ReadToEnd()); }
            try { Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j); }
            finally { Directory.Delete(link); }
        });
    }
    private static void InitialMediaFailure()
    {
        Scenario("initial-media-failure", delegate(string s, string j)
        {
            Populate(s); int calls = 0;
            Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None, delegate { calls++; throw new IOException("test media identity mismatch"); }); });
            Assert(calls == 1, "Initial media callback not invoked");
            Assert(!Directory.Exists(Path.Combine(j, "未完成-原盘文件")), "Copy started before initial verification");
            NoSuccess(j);
        });
    }
    private static void VerificationMediaFailure()
    {
        Scenario("verify-media-failure", delegate(string s, string j)
        {
            Populate(s); bool checking = false, threw = false;
            Fails(delegate { DiscCopy.Run(s, j, delegate(string p) { if (p.StartsWith("校验：")) checking = true; }, CancellationToken.None,
                delegate { if (checking) { threw = true; throw new IOException("test destination replaced before reread"); } }); });
            Assert(threw, "Verification boundary callback not invoked");
            Assert(File.Exists(Path.Combine(j, "未完成-原盘文件", "a.txt")), "Partial copy lost");
            NoSuccess(j);
        });
    }
    private static void PublicationMediaFailure()
    {
        Scenario("publish-media-failure", delegate(string s, string j)
        {
            Populate(s); bool threw = false;
            Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None, delegate
            {
                string partial = Path.Combine(j, "未完成-校验清单.tsv");
                if (File.Exists(partial))
                {
                    string text;
                    using (StreamReader reader = new StreamReader(new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                        text = reader.ReadToEnd();
                    if (text.Contains("# 完成："))
                    { threw = true; throw new IOException("test source lease expired immediately before publication"); }
                }
            }); });
            Assert(threw, "Final direct callback not invoked after verification");
            Assert(!Directory.Exists(Path.Combine(j, "原盘文件")), "Published before final verification");
            NoSuccess(j);
        });
    }
    private static void TargetCorruption()
    {
        Scenario("target-corruption", delegate(string s, string j)
        {
            Populate(s); bool changed = false;
            Fails(delegate { DiscCopy.Run(s, j, delegate(string p)
            {
                if (!changed && p.StartsWith("校验："))
                {
                    string target = Path.Combine(j, "未完成-原盘文件", "a.txt");
                    byte[] bytes = File.ReadAllBytes(target); bytes[0] ^= 1; File.WriteAllBytes(target, bytes); changed = true;
                }
            }, CancellationToken.None); });
            Assert(changed, "Did not reach final independent reread"); NoSuccess(j);
        });
    }
    private static void MakeDvd(string source)
    {
        string video = Path.Combine(source, "VIDEO_TS"); Directory.CreateDirectory(video);
        File.WriteAllText(Path.Combine(video, "VIDEO_TS.IFO"), "fixture IFO bytes; this test does not establish playability");
        File.WriteAllText(Path.Combine(video, "VIDEO_TS.BUP"), "fixture backup bytes");
        File.WriteAllText(Path.Combine(video, "VTS_01_1.VOB"), "fixture VOB bytes; no transcoding");
    }
    private static void DvdVideo()
    {
        Scenario("dvd-video", delegate(string s, string j)
        {
            MakeDvd(s);
            Directory.CreateDirectory(Path.Combine(s, "AUDIO_TS"));
            Directory.CreateDirectory(Path.Combine(s, "ROM", "empty-folder"));
            File.WriteAllText(Path.Combine(s, "ROM", "shooting-notes.txt"), "拍摄说明");
            File.WriteAllText(Path.Combine(s, "README.txt"), "additional original data");
            CopyResult result = DiscCopy.Run(s, j, null, CancellationToken.None);
            Assert(result.DvdVideoFiles, "Valid visible DVD structure not classified");
            Assert(result.Files == 5, "DVD/ROM file count mismatch");
            long total = 0;
            string output = Path.Combine(j, "原盘文件"), manifest = File.ReadAllText(Path.Combine(j, "校验清单.tsv"));
            foreach (string file in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(s.Length + 1);
                Assert(Hash(file) == Hash(Path.Combine(output, relative)), "DVD original bytes changed: " + relative);
                Assert(manifest.Contains(Hash(file)), "DVD manifest hash missing: " + relative);
                total += new FileInfo(file).Length;
            }
            foreach (string dir in Directory.GetDirectories(s, "*", SearchOption.AllDirectories))
                Assert(Directory.Exists(Path.Combine(output, dir.Substring(s.Length + 1))), "DVD directory missing");
            Assert(result.Bytes == total, "DVD byte count mismatch");
            Assert(manifest.Contains("待试播") && manifest.Contains("不是 ISO"), "DVD limitations missing from manifest");
        });
    }
    private static void InvalidDvdVideo()
    {
        foreach (string kind in new string[] { "empty", "ifo-only", "vob-only", "ifo-is-directory" })
        {
            Scenario("invalid-dvd-" + kind, delegate(string s, string j)
            {
                string video = Path.Combine(s, "VIDEO_TS"); Directory.CreateDirectory(video);
                if (kind == "ifo-only") File.WriteAllText(Path.Combine(video, "VIDEO_TS.IFO"), "ifo");
                if (kind == "vob-only" || kind == "ifo-is-directory") File.WriteAllText(Path.Combine(video, "VTS_01_1.VOB"), "vob");
                if (kind == "ifo-is-directory") Directory.CreateDirectory(Path.Combine(video, "VIDEO_TS.IFO"));
                Populate(s);
                Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j);
            });
        }
    }
    private static void VcdRejected()
    {
        Scenario("dvd-and-vcd", delegate(string s, string j)
        {
            MakeDvd(s); Directory.CreateDirectory(Path.Combine(s, "VCD"));
            File.WriteAllText(Path.Combine(s, "VCD", "INFO.VCD"), "fixture");
            Fails(delegate { DiscCopy.Run(s, j, null, CancellationToken.None); }); NoSuccess(j);
        });
    }
}
