using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using DiscArchive;

internal static class RescueTests
{
    private static string root, source, target, attempt;
    private static int passed;
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        string fixture = Path.Combine(root, Guid.NewGuid().ToString("N"));
        source = Path.Combine(fixture, "source"); target = Path.Combine(fixture, "target"); attempt = Path.Combine(target, "attempt");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    private static void Put(string rel, string content) { string path = Path.Combine(source, rel); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content); }
    private static RescueResult Run(IDictionary<string, string> map = null, Action<string> progress = null, Action verify = null, CancellationToken token = default(CancellationToken))
    { return RescueCopy.Run(source, target, "项目", map, "盘-001", attempt, progress, verify, token); }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    private static SafeFileHandle MetadataLock(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0x80000000, 1 | 4, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert(!handle.IsInvalid, "real Windows sharing handle: " + Marshal.GetLastWin32Error());
        Assert(Throws<IOException>(delegate {
            if(Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddYears(-1));
            else File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddYears(-1));
        }), "fixture must actually block metadata writes");
        return handle;
    }
    private static int ConcurrentWorker(string[] args)
    {
        try
        {
            CancellationTokenSource c = new CancellationTokenSource(); bool signaled = false;
            IDictionary<string,string> map = args[5] == "mapped" ? new Dictionary<string,string>{{"项目","项目"}} : null;
            RescueResult r = RescueCopy.Run(args[1], args[2], "项目", map, "duplicate-disc", args[3], delegate(string message) {
                if (signaled) return; signaled = true; File.WriteAllText(args[4] + ".ready", "ready");
                Stopwatch timer = Stopwatch.StartNew();
                while (!File.Exists(args[4] + ".go")) { if(timer.ElapsedMilliseconds > 15000) throw new IOException("barrier timeout"); Thread.Sleep(20); }
                if(args[6] == "cancel") c.Cancel();
            }, null, c.Token);
            Console.WriteLine("files=" + r.Files + ";failed=" + r.Failed); return r.Failed == 0 ? 0 : 2;
        }
        catch(RescueCanceledException) { return args[6] == "cancel" ? 0 : 3; }
        catch(Exception e) { Console.Error.WriteLine(e); return 4; }
    }
    private static string Q(string value) { return "\"" + value + "\""; }
    private static void ConcurrentPair(string other, bool mapped, bool cancelFirst, bool crashFirst = false)
    {
        string fixture=Path.GetDirectoryName(source); string[] barriers={Path.Combine(fixture,"barrier-a"),Path.Combine(fixture,"barrier-b")};
        string[] sources={source,other}; Process[] workers=new Process[2];
        try
        {
            for(int i=0;i<2;i++)
            {
                string arguments="--concurrent-worker " + Q(sources[i]) + " " + Q(target) + " " + Q(attempt) + " " + Q(barriers[i]) + " " + (mapped?"mapped":"normal") + " " + (i==0 && cancelFirst?"cancel":"copy");
                workers[i]=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, arguments) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
            }
            Stopwatch timer=Stopwatch.StartNew();
            while(!File.Exists(barriers[0]+".ready") || !File.Exists(barriers[1]+".ready"))
            {
                Assert(timer.ElapsedMilliseconds<15000,"both processes must reach source reading concurrently; allocation must not serialize copying");
                for(int i=0;i<2;i++) if(workers[i].HasExited) throw new Exception("worker exited before barrier: "+workers[i].StandardError.ReadToEnd());
                Thread.Sleep(20);
            }
            if(crashFirst) { workers[0].Kill(); workers[0].WaitForExit(); }
            for(int i=0;i<2;i++) File.WriteAllText(barriers[i]+".go","go");
            for(int i=0;i<2;i++)
            {
                Assert(workers[i].WaitForExit(20000),"worker completes");
                string output=workers[i].StandardOutput.ReadToEnd(), error=workers[i].StandardError.ReadToEnd();
                if(i==0 && crashFirst) continue;
                Assert(workers[i].ExitCode==0,"worker "+i+": "+output+error);
                if(!(i==0 && cancelFirst)) Assert(output.Contains("failed=0"),"competition is not a source failure");
            }
        }
        finally { foreach(Process worker in workers) if(worker!=null) { if(!worker.HasExited) worker.Kill(); worker.Dispose(); } }
    }
    private static void Payloads(string folder, params string[] expected)
    {
        List<string> actual=new List<string>(); foreach(string file in Directory.GetFiles(folder,"*.bin",SearchOption.AllDirectories)) actual.Add(File.ReadAllText(file));
        actual.Sort(StringComparer.Ordinal); Array.Sort(expected,StringComparer.Ordinal);
        Assert(actual.Count==expected.Length,"payload count "+actual.Count+" expected "+expected.Length);
        for(int i=0;i<expected.Length;i++) Assert(actual[i]==expected[i],"payload bytes/ownership "+i);
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--concurrent-worker") return ConcurrentWorker(args);
        root = Path.Combine(Path.GetTempPath(), "DiscArchiveRescueTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Test("nested, root files, hidden sidecar, empty directory and mapping", delegate {
                Put("电影\\子目录\\正片.bin", "movie"); Put("电影\\.附档", "meta"); Put("根文件.txt", "root"); Directory.CreateDirectory(Path.Combine(source, "电影\\空目录"));
                RescueResult r = Run(new Dictionary<string,string> { {"电 影".Replace(" ", ""), "另一个项目"} });
                Assert(r.Files == 3 && r.Failed == 0, "counts");
                Assert(File.ReadAllText(Path.Combine(target, "另一个项目\\电影\\子目录\\正片.bin")) == "movie", "hierarchy");
                Assert(Directory.Exists(Path.Combine(target, "另一个项目\\电影\\空目录")), "empty directory");
                Assert(File.Exists(Path.Combine(target, "项目\\根文件.txt")) && File.Exists(r.Report), "root and report");
            });
            Test("case insensitive top mapping", delegate { Put("TOP\\a.txt", "a"); Run(new Dictionary<string,string> {{"top", "mapped"}}); Assert(File.Exists(Path.Combine(target,"mapped\\TOP\\a.txt")), "mapping case"); });
            Test("file and directory modification times are not restored from source", delegate {
                Put("拍摄\\片段.mov", "movie"); Directory.CreateDirectory(Path.Combine(source,"拍摄\\空目录"));
                DateTime stamp = new DateTime(2008,2,3,4,5,6,DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(source,"拍摄\\片段.mov"),stamp);
                Directory.SetLastWriteTimeUtc(Path.Combine(source,"拍摄\\空目录"),stamp.AddDays(1));
                Directory.SetLastWriteTimeUtc(Path.Combine(source,"拍摄"),stamp.AddDays(2)); Run();
                Assert(File.GetLastWriteTimeUtc(Path.Combine(target,"项目\\拍摄\\片段.mov"))!=stamp,"file keeps copy time");
                Assert(Directory.GetLastWriteTimeUtc(Path.Combine(target,"项目\\拍摄\\空目录"))!=stamp.AddDays(1),"empty directory keeps copy time");
                Assert(Directory.GetLastWriteTimeUtc(Path.Combine(target,"项目\\拍摄"))!=stamp.AddDays(2),"parent directory keeps copy time");
            });
            Test("embedded EXIF capture metadata bytes survive unchanged", delegate {
                // Little-endian TIFF: IFD0 -> Exif IFD -> DateTimeOriginal ASCII value.
                byte[] data=new byte[76]; data[0]=data[1]=73; data[2]=42; data[4]=8;
                data[8]=1; data[10]=0x69; data[11]=0x87; data[12]=4; data[14]=1; data[18]=26;
                data[26]=1; data[28]=3; data[29]=0x90; data[30]=2; data[32]=20; data[36]=44;
                byte[] date=Encoding.ASCII.GetBytes("2016:04:09 20:53:53\0"); Buffer.BlockCopy(date,0,data,44,date.Length);
                File.WriteAllBytes(Path.Combine(source,"capture.tif"),data); RescueResult r=Run();
                byte[] copied=File.ReadAllBytes(Path.Combine(target,"项目\\capture.tif"));
                Assert(r.Files==1 && copied.Length==data.Length,"metadata file size");
                for(int i=0;i<data.Length;i++) Assert(copied[i]==data[i],"EXIF file byte "+i);
                Assert(Encoding.ASCII.GetString(copied,44,19)=="2016:04:09 20:53:53","embedded capture date unchanged");
            });
            Test("large fast file verification throttled instead of every megabyte", delegate {
                File.WriteAllBytes(Path.Combine(source,"large.bin"),new byte[64*1024*1024]); int checks=0;
                RescueResult r=Run(null,null,delegate { checks++; });
                Assert(r.Files==1 && checks>=3 && checks<24,"verification calls="+checks); Console.WriteLine("  64MB verify calls="+checks);
            });
            Test("real directory sharing lock needs no time write and visits later siblings", delegate {
                Put("locked\\a.txt", "a"); Put("later\\b.txt", "b"); SafeFileHandle handle=null;
                try {
                    RescueResult r=Run(null,null,delegate {
                        string dir=Path.Combine(target,"项目\\locked");
                        if(handle==null && File.Exists(Path.Combine(dir,"a.txt"))) handle=MetadataLock(dir);
                    });
                    Assert(handle!=null && r.Files==2 && r.Failed==0,"directory sharing lock tolerated");
                    Assert(File.ReadAllText(Path.Combine(target,"项目\\later\\b.txt"))=="b","later sibling retained");
                    Assert(!File.ReadAllText(Path.Combine(Path.GetDirectoryName(r.Report),"路径清单.tsv")).Contains("时间提示"),"no artificial time warning");
                } finally { if(handle!=null) handle.Dispose(); }
            });
            Test("real mapped project sharing lock visits next project without time restoration", delegate {
                Put("甲\\a", "a"); Put("乙\\b", "b"); SafeFileHandle handle=null;
                try {
                    RescueResult r=Run(new Dictionary<string,string>{{"甲","甲"},{"乙","乙"}},null,delegate {
                        string dir=Path.Combine(target,"甲");
                        if(handle==null && File.Exists(Path.Combine(dir,"a"))) handle=MetadataLock(dir);
                    });
                    Assert(handle!=null && r.Files==2 && r.Failed==0 && File.Exists(Path.Combine(target,"乙\\b")),"mapped root sharing lock tolerated");
                } finally { if(handle!=null) handle.Dispose(); }
            });
            Test("real file sharing conflict preserves complete publication", delegate {
                Put("a.txt","complete"); Put("b.txt","sibling"); SafeFileHandle handle=null;
                try {
                    RescueResult r=Run(null,null,delegate {
                        if(handle!=null || !Directory.Exists(attempt)) return;
                        foreach(string file in Directory.GetFiles(attempt,"*.partial",SearchOption.AllDirectories))
                            if(new FileInfo(file).Length==8) { handle=MetadataLock(file); break; }
                    });
                    Assert(handle!=null && r.Files==2 && r.Failed==0,"file sharing lock needs no time write");
                    Assert(File.ReadAllText(Path.Combine(target,"项目\\a.txt"))=="complete" && File.Exists(Path.Combine(target,"项目\\b.txt")),"complete bytes and sibling");
                } finally { if(handle!=null) handle.Dispose(); }
            });
            Test("damaged complete fragment is fatal before publication", delegate {
                Put("a.txt","complete"); bool damaged=false;
                try {
                    Run(null,null,delegate {
                        if(damaged || !Directory.Exists(attempt)) return;
                        foreach(string file in Directory.GetFiles(attempt,"*.partial",SearchOption.AllDirectories))
                            if(new FileInfo(file).Length==8) { File.WriteAllText(file,"bad"); damaged=true; break; }
                    }); throw new Exception("damaged fragment accepted");
                } catch(RescueStoppedException e) { Assert(damaged && e.Result.Files==0 && !File.Exists(Path.Combine(target,"项目\\a.txt")),"damaged output not successful"); }
            });
            Test("missing output directory remains fatal", delegate {
                Directory.CreateDirectory(Path.Combine(source,"empty")); bool removed=false;
                try {
                    Run(null,null,delegate {
                        string dir=Path.Combine(target,"项目\\empty");
                        if(!removed && Directory.Exists(dir)) { Directory.Delete(dir); removed=true; }
                    }); throw new Exception("missing directory accepted");
                } catch(RescueStoppedException e) { Assert(removed && e.Result.Files==0,"missing output fatal"); }
            });
            Test("verify failure while directory is held stays fatal", delegate {
                Put("locked\\a","a"); SafeFileHandle handle=null;
                try {
                    Run(null,null,delegate {
                        if(handle!=null) throw new IOException("identity changed while directory held");
                        string dir=Path.Combine(target,"项目\\locked");
                        if(File.Exists(Path.Combine(dir,"a"))) handle=MetadataLock(dir);
                    }); throw new Exception("identity failure ignored");
                } catch(RescueStoppedException e) {
                    Assert(handle!=null && e.Result.Files==1 && e.InnerException.Message.Contains("identity changed"),"identity check remains fatal");
                } finally { if(handle!=null) handle.Dispose(); }
            });
            Test("cancellation while directory is held stays canceled", delegate {
                Put("locked\\a","a"); SafeFileHandle handle=null; CancellationTokenSource c=new CancellationTokenSource();
                try {
                    Run(null,null,delegate {
                        if(handle!=null) { c.Cancel(); return; }
                        string dir=Path.Combine(target,"项目\\locked");
                        if(File.Exists(Path.Combine(dir,"a"))) handle=MetadataLock(dir);
                    },c.Token); throw new Exception("cancellation ignored");
                } catch(RescueCanceledException e) { Assert(handle!=null && e.Result.Files==1,"cancellation remains canceled"); }
                finally { if(handle!=null) handle.Dispose(); c.Dispose(); }
            });
            Test("target root disappearance stops without successful completion", delegate {
                Put("a","a"); bool removed=false;
                try {
                    RescueCopy.Run(source,target,"项目",null,"盘-001",Path.Combine(Path.GetDirectoryName(target),"external-attempt"),null,delegate {
                        if(!removed && File.Exists(Path.Combine(target,"项目\\a"))) {
                            Directory.Move(target,Path.Combine(Path.GetDirectoryName(target),"detached-target")); removed=true;
                        }
                    },CancellationToken.None); throw new Exception("missing target accepted");
                } catch(RescueStoppedException) { Assert(removed && !Directory.Exists(target),"target root loss fatal"); }
            });
            Test("large file reports byte progress about once per second", delegate {
                File.WriteAllBytes(Path.Combine(source,"large.bin"),new byte[4*1024*1024]); List<string> reports=new List<string>();
                RescueResult r=Run(null,delegate(string message) { reports.Add(message); if(reports.Count<=2) Thread.Sleep(1050); });
                Assert(r.Files==1 && reports.Count>=3,"periodic progress count");
                Assert(reports[1].Contains("1048576 / 4194304") && reports[reports.Count-1].Contains("4194304 / 4194304"),"byte progress");
            });
            Test("entire conflicting tree isolated without overwrite", delegate {
                Put("same\\a.txt", "new"); Put("same\\deep\\b.txt", "b"); string old = Path.Combine(target, "项目\\same"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old,"a.txt"), "old");
                RescueResult r = Run(); Assert(r.Files == 2 && File.ReadAllText(Path.Combine(old,"a.txt")) == "old", "old preserved");
                string[] origins = Directory.GetDirectories(Path.Combine(target,"项目"), "来源-*"); Assert(origins.Length == 1, "one origin"); Assert(File.Exists(Path.Combine(origins[0], "same\\deep\\b.txt")), "whole subtree");
            });
            Test("multiple conflicting top entries share one disc source folder", delegate {
                Put("a.txt","new-a"); Put("b.txt","new-b"); Put("12-2\\13\\jpg\\image.jpg","image");
                Directory.CreateDirectory(Path.Combine(target,"项目\\12-2")); File.WriteAllText(Path.Combine(target,"项目\\a.txt"),"old-a"); File.WriteAllText(Path.Combine(target,"项目\\b.txt"),"old-b");
                RescueResult r=Run(); string origin=Path.Combine(target,"项目\\来源-盘-001");
                Assert(r.Files==3 && Directory.GetDirectories(Path.Combine(target,"项目"),"来源-*").Length==1,"one shared origin");
                Assert(File.ReadAllText(Path.Combine(origin,"a.txt"))=="new-a" && File.ReadAllText(Path.Combine(origin,"b.txt"))=="new-b","root conflicts grouped");
                Assert(File.Exists(Path.Combine(origin,"12-2\\13\\jpg\\image.jpg")),"single-project hierarchy not flattened");
                Run(); Assert(Directory.Exists(Path.Combine(target,"项目\\来源-盘-001-2")),"retry independent origin");
            });
            Test("two explicitly mapped project directories become project roots", delegate {
                Put("170621-星河工作室\\12-2\\13\\jpg\\图.jpg","p"); Put("另一项目\\附件\\说明.txt","q");
                RescueResult r=Run(new Dictionary<string,string> {{"170621-星河工作室","170621-星河工作室"},{"另一项目","另一项目"}});
                Assert(r.Files==2 && File.Exists(Path.Combine(target,"170621-星河工作室\\12-2\\13\\jpg\\图.jpg")),"first project root");
                Assert(File.Exists(Path.Combine(target,"另一项目\\附件\\说明.txt")),"second project root");
                Assert(!Directory.Exists(Path.Combine(target,"170621-星河工作室\\170621-星河工作室")),"no repeated project directory");
            });
            Test("mapped project conflict isolates all child siblings as one group", delegate {
                Put("项目甲\\12-2\\13\\jpg\\图.jpg","new"); Put("项目甲\\附档.txt","sidecar");
                Directory.CreateDirectory(Path.Combine(target,"项目甲\\12-2")); File.WriteAllText(Path.Combine(target,"项目甲\\12-2\\old.txt"),"old");
                RescueResult r=Run(new Dictionary<string,string>{{"项目甲","项目甲"}}); string origin=Path.Combine(target,"项目甲\\来源-盘-001");
                Assert(r.Files==2 && File.Exists(Path.Combine(origin,"12-2\\13\\jpg\\图.jpg")) && File.Exists(Path.Combine(origin,"附档.txt")),"complete group");
                Assert(!File.Exists(Path.Combine(target,"项目甲\\附档.txt")) && File.ReadAllText(Path.Combine(target,"项目甲\\12-2\\old.txt"))=="old","no split or overwrite");
            });
            Test("mapped reserved photo group cannot collide with archive original", delegate {
                Put("项目甲\\盘面照片\\photo.jpg","source"); Put("项目甲\\附档.txt","meta");
                Directory.CreateDirectory(Path.Combine(target,"项目甲\\盘面照片")); File.WriteAllText(Path.Combine(target,"项目甲\\盘面照片\\原图.jpg"),"original");
                Run(new Dictionary<string,string>{{"项目甲","项目甲"}});
                Assert(File.ReadAllText(Path.Combine(target,"项目甲\\盘面照片\\原图.jpg"))=="original","photo preserved");
                Assert(File.Exists(Path.Combine(target,"项目甲\\来源-盘-001\\盘面照片\\photo.jpg")) && File.Exists(Path.Combine(target,"项目甲\\来源-盘-001\\附档.txt")),"photo and sibling group");
            });
            Test("reserved photo names isolated", delegate { Put("photo.jpg", "source photo"); RescueResult r = Run(); Assert(r.Files == 1 && !File.Exists(Path.Combine(target,"项目\\photo.jpg")), "photo reserved"); });
            Test("locked source skipped while siblings continue", delegate {
                Put("bad.bin", "bad"); Put("good\\ok.txt", "ok");
                using (FileStream locked = new FileStream(Path.Combine(source,"bad.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
                { RescueResult r = Run(); Assert(r.Files == 1 && r.Failed == 1, "source skip"); Assert(File.Exists(Path.Combine(target,"项目\\good\\ok.txt")), "continued"); }
            });
            Test("CDA not counted as extracted audio", delegate { Put("Track01.cda", "placeholder"); Put("notes.txt", "n"); RescueResult r = Run(); Assert(r.Files == 1 && r.Failed == 1 && !File.Exists(Path.Combine(target,"项目\\Track01.cda")), "CDA"); });
            Test("mid-file source read error preserves fragment and continues", delegate {
                byte[] data = new byte[3 * 1024 * 1024]; string bad = Path.Combine(source,"bad.bin"); File.WriteAllBytes(bad,data); Put("ok.txt", "ok");
                using (FileStream locked = new FileStream(bad,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    locked.Lock(1024 * 1024,1024 * 1024);
                    try {
                        RescueResult r = Run(); Assert(r.Files == 1 && r.Failed == 1, "mid-read count");
                        string recordDir = Path.GetDirectoryName(r.Report); string[] partials = Directory.GetFiles(Path.Combine(recordDir,"未完成残片"));
                        Assert(partials.Length == 1 && new FileInfo(partials[0]).Length == 1024 * 1024, "one MB fragment");
                        Assert(!File.Exists(Path.Combine(target,"项目\\bad.bin")), "no incomplete publication");
                    } finally { locked.Unlock(1024 * 1024,1024 * 1024); }
                }
            });
            Test("pre-cancel exposes counts and readable report", delegate {
                Put("a", "a"); CancellationTokenSource c = new CancellationTokenSource(); c.Cancel();
                try { Run(null,null,null,c.Token); throw new Exception("cancel ignored"); }
                catch (RescueCanceledException e) { Assert(e.Result.Files == 0 && File.ReadAllText(e.Result.Report).Contains("已取消"), "cancel receipt"); }
            });
            Test("mid-copy cancellation retains partial bytes and publishes nothing", delegate {
                File.WriteAllBytes(Path.Combine(source,"large.bin"),new byte[5 * 1024 * 1024]); CancellationTokenSource c = new CancellationTokenSource();
                try { Run(null,null,delegate {
                    if (Directory.Exists(attempt)) foreach (string p in Directory.GetFiles(attempt,"*.partial",SearchOption.AllDirectories)) if(new FileInfo(p).Length > 0) c.Cancel();
                },c.Token); throw new Exception("mid cancel ignored"); }
                catch(RescueCanceledException e) { Assert(e.Result.Files == 0 && Directory.GetFiles(attempt,"*.partial",SearchOption.AllDirectories).Length == 1,"partial retained"); Assert(!File.Exists(Path.Combine(target,"项目\\large.bin")),"no canceled publication"); }
            });
            Test("directory enumeration failure recorded and sibling retained", delegate {
                Put("broken\\a", "a"); Put("ok.txt", "ok"); bool moved = false;
                RescueResult r = Run(null,null,delegate {
                    if (!moved && Directory.Exists(Path.Combine(target,"项目\\broken"))) { Directory.Move(Path.Combine(source,"broken"),Path.Combine(Path.GetDirectoryName(source),"removed-source-folder")); moved = true; }
                });
                Assert(r.Files == 1 && r.Failed == 1 && File.ReadAllText(Path.Combine(Path.GetDirectoryName(r.Report),"失败清单.tsv")).Contains("目录枚举"),"enumeration receipt");
            });
            Test("verify failure after first publication preserves counts", delegate {
                Put("a", "a"); Put("b", "b");
                try { Run(null,null,delegate { if (Directory.Exists(Path.Combine(target,"项目")) && Directory.GetFiles(Path.Combine(target,"项目")).Length > 0) throw new IOException("media changed"); }); throw new Exception("verify ignored"); }
                catch (RescueStoppedException e) { Assert(e.Result.Files == 1 && e.Result.Bytes == 1 && e.InnerException.Message == "media changed", "verify result"); }
            });
            Test("destination failure stops instead of source skip", delegate {
                Put("a", "a"); File.WriteAllText(Path.Combine(target,"项目"), "occupied");
                try { Run(); throw new Exception("target failure ignored"); }
                catch (RescueStoppedException e) { Assert(e.Result.Failed == 0 && e.Result.Files == 0, "fatal destination"); }
            });
            Test("source and target overlap rejected", delegate { Assert(Throws<IOException>(delegate { RescueCopy.Run(source, source, "项目", null,"1",attempt,null,null,CancellationToken.None); }), "overlap"); });
            Test("project path escape rejected", delegate { Assert(Throws<ArgumentException>(delegate { RescueCopy.Run(source,target,"..",null,"1",attempt,null,null,CancellationToken.None); }), "escape"); });
            Test("source junction rejected without copying linked tree", delegate {
                string linked = Path.Combine(source,"linked"); string actual = Path.Combine(Path.GetDirectoryName(source),"actual"); Directory.CreateDirectory(actual); File.WriteAllText(Path.Combine(actual,"private.txt"),"private");
                ProcessStartInfo info = new ProcessStartInfo("cmd.exe","/c mklink /J \"" + linked + "\" \"" + actual + "\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                using(Process p=Process.Start(info)) { p.WaitForExit(); Assert(p.ExitCode==0,"junction setup"); }
                try { RescueResult r=Run(); Assert(r.Files==0 && r.Failed==1,"junction skipped"); }
                finally { Directory.Delete(linked); }
            });
            Test("reused attempts get independent records and old file preserved", delegate { Put("a", "a"); RescueResult a = Run(); RescueResult b = Run(); Assert(a.Report != b.Report && b.Files == 1 && File.Exists(a.Report), "independent retries"); });
            Test("two real processes same project same top directory and duplicate disc IDs", delegate {
                Put("same\\a.bin","process-a"); string other=Path.Combine(Path.GetDirectoryName(source),"source-b"); Directory.CreateDirectory(Path.Combine(other,"same")); File.WriteAllText(Path.Combine(other,"same\\a.bin"),"process-b");
                ConcurrentPair(other,false,false); Payloads(Path.Combine(target,"项目"),"process-a","process-b");
                Assert(Directory.GetDirectories(Path.Combine(target,"项目"),"来源-*").Length==1,"one competing tree isolated");
                Run(); Run(); Payloads(Path.Combine(target,"项目"),"process-a","process-b","process-a","process-a");
            });
            Test("two real processes different top groups copy concurrently in one project", delegate {
                Put("alpha\\a.bin","alpha"); string other=Path.Combine(Path.GetDirectoryName(source),"source-b"); Directory.CreateDirectory(Path.Combine(other,"beta")); File.WriteAllText(Path.Combine(other,"beta\\b.bin"),"beta");
                ConcurrentPair(other,false,false); Payloads(Path.Combine(target,"项目"),"alpha","beta");
                Assert(File.Exists(Path.Combine(target,"项目\\alpha\\a.bin")) && File.Exists(Path.Combine(target,"项目\\beta\\b.bin")),"distinct groups retain roots");
            });
            Test("two real processes mapped groups claim all siblings as a unit", delegate {
                Put("项目\\shared\\a.bin","a-tree"); Put("项目\\a.bin","a-sidecar"); string other=Path.Combine(Path.GetDirectoryName(source),"source-b"); Directory.CreateDirectory(Path.Combine(other,"项目\\shared")); File.WriteAllText(Path.Combine(other,"项目\\shared\\b.bin"),"b-tree"); File.WriteAllText(Path.Combine(other,"项目\\b.bin"),"b-sidecar");
                ConcurrentPair(other,true,false); Payloads(Path.Combine(target,"项目"),"a-tree","a-sidecar","b-tree","b-sidecar");
                string[] groups=Directory.GetDirectories(Path.Combine(target,"项目"),"来源-*"); Assert(groups.Length==1,"one group isolated");
                string[] trees=Directory.GetFiles(Path.Combine(groups[0],"shared"),"*.bin"); Assert(trees.Length==1,"no tree mixing");
                string sidecar=File.Exists(Path.Combine(groups[0],"a.bin"))?"a":"b"; Assert(File.ReadAllText(trees[0])==sidecar+"-tree" && File.ReadAllText(Path.Combine(groups[0],sidecar+".bin"))==sidecar+"-sidecar","siblings stay owned by same disc");
            });
            Test("real concurrent cancellation retains claim and retry never joins another disc", delegate {
                Put("same\\a.bin","canceled-retry"); string other=Path.Combine(Path.GetDirectoryName(source),"source-b"); Directory.CreateDirectory(Path.Combine(other,"same")); File.WriteAllText(Path.Combine(other,"same\\b.bin"),"completed");
                ConcurrentPair(other,false,true); Payloads(Path.Combine(target,"项目"),"completed"); Run(); Payloads(Path.Combine(target,"项目"),"completed","canceled-retry");
                string[] a=Directory.GetFiles(Path.Combine(target,"项目"),"a.bin",SearchOption.AllDirectories), b=Directory.GetFiles(Path.Combine(target,"项目"),"b.bin",SearchOption.AllDirectories);
                Assert(Path.GetDirectoryName(a[0])!=Path.GetDirectoryName(b[0]),"retry has a different tree owner");
            });
            Test("mapped and ordinary top entries cannot claim each other's origin children", delegate {
                Put("项目\\same.bin","mapped"); Put("same.bin","ordinary");
                Directory.CreateDirectory(Path.Combine(target,"项目")); File.WriteAllText(Path.Combine(target,"项目\\same.bin"),"legacy");
                RescueResult r=Run(new Dictionary<string,string>{{"项目","项目"}});
                Assert(r.Files==2 && r.Failed==0,"both overlapping source groups copied"); Payloads(Path.Combine(target,"项目"),"legacy","mapped","ordinary");
            });
            Test("killed real process leaves ownership fenced and retry independent", delegate {
                Put("same\\a.bin","after-crash"); string other=Path.Combine(Path.GetDirectoryName(source),"source-b"); Directory.CreateDirectory(Path.Combine(other,"same")); File.WriteAllText(Path.Combine(other,"same\\b.bin"),"survivor");
                ConcurrentPair(other,false,false,true); Run(); Payloads(Path.Combine(target,"项目"),"after-crash","survivor");
                string[] a=Directory.GetFiles(Path.Combine(target,"项目"),"a.bin",SearchOption.AllDirectories), b=Directory.GetFiles(Path.Combine(target,"项目"),"b.bin",SearchOption.AllDirectories);
                Assert(Path.GetDirectoryName(a[0])!=Path.GetDirectoryName(b[0]),"crash retry has separate ownership");
            });
            Test("cancellation while waiting for allocation lock is not a source error", delegate {
                Put("a.bin","a"); string area=Path.Combine(target,".discarchive-coordination-v1"); Directory.CreateDirectory(area);
                CancellationTokenSource c=new CancellationTokenSource(); c.CancelAfter(250);
                using(FileStream held=new FileStream(Path.Combine(area,"allocation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                {
                    try { Run(null,null,null,c.Token); throw new Exception("lock wait ignored cancellation"); }
                    catch(RescueCanceledException e) { Assert(e.Result.Files==0 && e.Result.Failed==0,"canceled allocation receipt"); }
                }
                c.Dispose(); Run(); Payloads(Path.Combine(target,"项目"),"a");
            });
            Console.WriteLine("PASS " + passed + " rescue tests. Isolated fixture: " + root); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(root,true); }
    }
}
