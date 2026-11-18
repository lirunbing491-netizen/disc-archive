using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Windows.Forms;

namespace DiscArchive
{
    // Exercise the real WinForms event loop and shipped copy/catalog paths.
    // Optical hardware and OCR worker output are synthetic. Real OCR has separate generated-image tests.
    internal sealed class SimulatedDrive : IReader
    {
        internal string Source;
        internal bool Loaded, Connected = true;
        internal int Runs;
        internal Action OnVerify;
        public string[] Drives() { return Connected ? new string[] { @"X:\" } : new string[0]; }
        public bool Empty(string root) { return Connected && !Loaded; }
        public bool Ready(string root) { return Connected && Loaded; }
        public string Label="170621-星河访谈素材";
        public MountedDisc Open(string root)
        {
            Interlocked.Increment(ref Runs);
            return new MountedDisc(Source,Label,delegate {
                if (!Loaded || !Connected) throw new IOException("模拟介质移除。");
                if (OnVerify!=null) OnVerify();
            },null);
        }
    }

    internal static class WorkflowTests
    {
        private static int checks;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        private static Form NoticeFor(string heading)
        {
            return Application.OpenForms.Cast<Form>().FirstOrDefault(w=>w.Visible && w.AccessibleRole==AccessibleRole.Alert && w.AccessibleName==heading);
        }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Check(bool yes,string name) { if(!yes) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        private static T Field<T>(MainForm form,string name) { return (T)typeof(MainForm).GetField(name,Private).GetValue(form); }
        private static void Call(MainForm form,string name,params object[] args) { typeof(MainForm).GetMethod(name,Private).Invoke(form,args); }
        private static void Spin(Func<bool> until, int seconds=14)
        {
            Stopwatch time=Stopwatch.StartNew();
            do { Application.DoEvents(); if(until()) return; Thread.Sleep(20); } while(time.Elapsed.TotalSeconds<seconds);
            throw new TimeoutException("Timeout waiting for workflow. " + seconds);
        }
        private static void WaitMs(int ms) { Stopwatch t=Stopwatch.StartNew(); Spin(delegate {return t.ElapsedMilliseconds>=ms;}); }
        private static void Snap(MainForm f,string path)
        { using(Bitmap b=new Bitmap(f.Width,f.Height)) {f.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height)); b.Save(path,ImageFormat.Png);} }

        private static void CheckLargeCatalog(string root,SimulatedDrive reader,Entry example)
        {
            string state=Directory.CreateDirectory(Path.Combine(root,"scale-state")).FullName;
            var rows=new System.Collections.Generic.List<Entry>();
            for(int i=0;i<3000;i++)
                rows.Add(new Entry {Id="TEST-"+i,Title="项目 "+i,TitleState="已确认",Project="隔离样本",Status=Workstation.Done,Photo=example.Photo,TargetName="硬盘",Created=example.Created});
            using(var data=File.Create(Path.Combine(state,"catalog.xml")))
                new System.Xml.Serialization.XmlSerializer(rows.GetType()).Serialize(data,rows);
            Stopwatch elapsed=Stopwatch.StartNew();
            using(MainForm f=new MainForm(state,reader))
            {
                f.ShowInTaskbar=false;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-10000,-10000);f.Show();
                Field<TabControl>(f,"tabs").SelectedIndex=1;Application.DoEvents();
                Check(Field<DataGridView>(f,"grid").Rows.Count==3000,"desk loads 3000 isolated catalog entries ("+elapsed.ElapsedMilliseconds+" ms)");
                Field<TextBox>(f,"search").Text="项目 2999";
                Check(Field<DataGridView>(f,"grid").Rows.Count==1,"lookup across 3000 entries");
                f.Close();
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string root=Path.Combine(Path.GetTempPath(),"DiscArchive-Workflow-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                string source=Directory.CreateDirectory(Path.Combine(root,"test-data-CD")).FullName;
                string disk=Directory.CreateDirectory(Path.Combine(root,"test-target")).FullName;
                string inbox=Directory.CreateDirectory(Path.Combine(root,"photo-inbox")).FullName;
                string local=Path.Combine(root,"state");
                string label=Path.Combine(root,"label.jpg");
                using(Bitmap b=new Bitmap(1200,600)) using(Graphics g=Graphics.FromImage(b))
                using(Font font=new Font("Microsoft YaHei",65,FontStyle.Bold)) {
                    g.Clear(Color.White);g.DrawString("ARCHIVE 2024",font,Brushes.Black,30,180); b.Save(label,ImageFormat.Jpeg);
                }
                Directory.CreateDirectory(Path.Combine(source,"DCIM","空文件夹"));
                string original=Path.Combine(source,"DCIM","00001.MTS"); File.WriteAllText(original,"simulated bytes / 早年摄像");
                File.Copy(label,Path.Combine(inbox,"older.jpg"));
                SimulatedDrive reader=new SimulatedDrive{Source=source};
                Application.EnableVisualStyles();
                Rectangle outside=SystemInformation.VirtualScreen;
                outside.Offset(outside.Width+4000,outside.Height+4000);
                using(MainForm f=new MainForm(local,reader,new CompletionNotice(delegate{return outside;},25000)))
                {
                    f.ShowInTaskbar=false; f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-10000,-10000); f.Show();
                    Field<TextBox>(f,"target").Text=disk;
                    Field<TextBox>(f,"project").Text="星河访谈素材";
                    Workstation station=Field<Workstation>(f,"station");
                    Check(Field<VisionOptions>(f,"vision").Mode=="metadata","default archive needs no model, connection or OCR");
                    Field<VisionOptions>(f,"vision").Mode="ocr";
                    Call(f,"BeginWatch",inbox); WaitMs(1700);
                    Check(station.Current==null&&station.Book.List().Count==0,"pre-existing photo is not bound");
                    File.Copy(label,Path.Combine(inbox,"fresh.jpg"));
                    Spin(delegate {return station.Current!=null;});
                    Entry first=station.Current;
                    Check(first.Status==Workstation.Waiting && station.EmptySeen && reader.Runs==0,"fresh photo receives one ID before disc insertion");
                    Check(File.ReadAllBytes(label).SequenceEqual(File.ReadAllBytes(first.OriginalPhoto)) && File.Exists(first.Photo),"archive holds original photo and registry holds preview");
                    Snap(f,Path.Combine(root,"capture-screen.png"));
                    string dcim=Path.Combine(disk,"170621-星河访谈素材","DCIM");
                    DateTime oldTime=new DateTime(2008,5,6,12,0,0,DateTimeKind.Utc);
                    File.SetLastWriteTimeUtc(original,oldTime);
                    Directory.SetLastWriteTimeUtc(Path.Combine(source,"DCIM"),oldTime);
                    SafeFileHandle held=null;
                    reader.OnVerify=delegate {
                        if(held==null && File.Exists(Path.Combine(dcim,"00001.MTS")) && Directory.Exists(Path.Combine(dcim,"空文件夹"))) {
                            held=CreateFile(dcim,0x80000000,5,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
                            if(held.IsInvalid) throw new IOException("Could not establish directory sharing fixture.");
                        }
                    };
                    try { reader.Loaded=true; Spin(delegate {return station.Current==null;}); }
                    finally { reader.OnVerify=null; if(held!=null) held.Dispose(); }
                    Entry done=station.Book.List().Single();
                    Check(done.Status==Workstation.Done && done.Files==1 && done.Bytes==new FileInfo(original).Length && reader.Runs==1,"insertion automatically copied and registered");
                    Check(held!=null && done.Warnings==0 && done.Failed==0 && !done.Note.Contains("时间"),"locked output directory still completes without any timestamp maintenance");
                    var popup=NoticeFor("本张已拷出，可以换盘");
                    Check(popup!=null && popup.AccessibleDescription.Contains(done.Project) && popup.AccessibleDescription.Contains("失败条目 0"),"actual completion automatically shows a success notice with counts");
                    string saved=Path.Combine(disk,"170621-星河访谈素材","DCIM","00001.MTS");
                    Check(File.GetLastWriteTimeUtc(saved)!=oldTime && Directory.GetLastWriteTimeUtc(dcim)!=oldTime,"destination file and folder keep their natural copy times");
                    Check(File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(saved)) && Directory.Exists(Path.Combine(Path.GetDirectoryName(saved),"空文件夹")),"verified output preserves files and empty folders");
                    Check(File.ReadAllText(Path.Combine(done.Report,"路径清单.tsv")).Contains("00001.MTS"),"source/destination register is readable");
                    Check(done.ArchiveFolders.Single()=="170621-星河访谈素材" && done.NamingReason.Contains("卷标") && done.PhotoText.Contains("2024"),"directory name and OCR evidence are independent");
                    Check(File.ReadAllBytes(label).SequenceEqual(File.ReadAllBytes(Path.Combine(disk,done.Project,"盘面照片",done.Id+".jpg"))),"full disc image filed with project");
                    Field<TabControl>(f,"tabs").SelectedIndex=1;
                    Field<TextBox>(f,"editTitle").Text="虚构庆典 B机 第2盘";
                    Call(f,"SaveEdit");
                    Check(station.Book.List().Single().Title=="虚构庆典 B机 第2盘" && station.Book.List().Single().TitleState=="已确认" && File.Exists(saved),"manual correction updates same ID, leaves media file path available");
                    Field<TextBox>(f,"search").Text="虚构庆典"; Check(Field<DataGridView>(f,"grid").Rows.Count==1,"catalog search finds corrected title");
                    Snap(f,Path.Combine(root,"catalog-screen.png"));
                    Field<TextBox>(f,"search").Text=""; Field<TabControl>(f,"tabs").SelectedIndex=0;
                    File.Copy(label,Path.Combine(inbox,"next.jpg"));
                    Spin(delegate {return station.Current!=null;});
                    Entry second=station.Current; WaitMs(2100);
                    Check(!station.EmptySeen && reader.Runs==1 && second.Id!=done.Id,"previous disc still in drive cannot be paired with next photo");
                    reader.Loaded=false; Spin(delegate{return station.EmptySeen;});
                    string bad=Path.Combine(source,"bad.IIQ"); File.WriteAllText(bad,"unreadable");
                    using(FileStream locked=File.Open(bad,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                        reader.Loaded=true; Spin(delegate{return station.Current==null;});
                    }
                    Entry rejected=station.Book.List().Single(e=>e.Id==second.Id);
                    Check(rejected.Status==Workstation.Partial && rejected.Files==1 && rejected.Failed==1,"unreadable file recorded; healthy files recovered");
                    Check(NoticeFor("部分拷出，请查看本盘记录")!=null && popup.AccessibleDescription.Contains("失败条目 1"),"real file failure refreshes the same notice with a partial result");
                    int attemptsBefore=Directory.GetDirectories(rejected.JobDir).Length;
                    reader.Loaded=false; station.Retry(rejected,@"X:\"); Call(f,"ShowArmed",rejected);
                    reader.Loaded=true; Spin(delegate{return station.Current==null;});
                    Entry redone=station.Book.List().Single(e=>e.Id==second.Id);
                    Check(redone.Status==Workstation.Done && redone.Files==2 && Directory.GetDirectories(redone.JobDir).Length==attemptsBefore+1 && station.Book.List().Count==2,"retry preserves disc ID, old report, and previously recovered content");
                    Check(File.Exists(saved) && File.ReadAllText(saved).Contains("simulated bytes"),"earlier project files survive a same-name disc and retry");
                    File.Copy(label,Path.Combine(inbox,"batch1.jpg")); File.Copy(label,Path.Combine(inbox,"batch2.jpg"));
                    Spin(delegate {return Field<FileSystemWatcher>(f,"watcher")==null;});
                    Check(station.Current==null && station.Book.List().Count==2,"multiple new photos pause automatic pairing");
                    string csv=Path.Combine(root,"登记总表.csv"); station.Book.Export(csv);
                    Check(File.ReadAllText(csv).Contains("虚构庆典 B机 第2盘"),"CSV contains confirmed title");
                    reader.Loaded=false;
                    Entry interrupted=station.Register(label,disk,done.TargetId,@"X:\","");
                    interrupted.Status=Workstation.Running; station.Book.Save(interrupted);
                    Workstation reopened=new Workstation(new Catalog(local),reader);
                    Check(reopened.Book.List().Single(e=>e.Id==interrupted.Id).Status==Workstation.Review,"restart marks interrupted records for review");
                    station.Stop();
                    f.Close();
                }
                CheckLargeCatalog(root,reader,new Catalog(local).List().First());
                CheckGate(root,reader,label);
                CheckFallback(root,reader,label);
                Console.WriteLine("Workflow checks: "+checks+" / evidence: "+root); return 0;
            }
            catch(Exception ex) {Console.Error.WriteLine(ex.ToString());return 1;}
        }

        private static void CheckGate(string root,SimulatedDrive drive,string label)
        {
            string disk=Directory.CreateDirectory(Path.Combine(root,"gate-target")).FullName;
            using(ManualResetEvent recognition=new ManualResetEvent(false))
            {
                Workstation s=new Workstation(new Catalog(Path.Combine(root,"gate-state")),drive,delegate(string path,CancellationToken token) {
                    if(!recognition.WaitOne(10000)) throw new TimeoutException();
                    return "170621-错误候选";
                },delegate {return "model";});
                drive.Loaded=true;
                Entry e=s.Register(label,disk,"",@"X:\","",true);
                var ready=s.CanStart(false); Spin(()=>ready.IsCompleted);
                Check(ready.Result,"empty tray at shutter permits immediate insertion");
                var copy=s.Copy(delegate {});
                WaitMs(280);
                Check(!copy.IsCompleted && !Directory.Exists(Path.Combine(disk,drive.Label)),"mounted disc waits for outstanding photo analysis before copying");
                recognition.Set(); Spin(()=>copy.IsCompleted);
                Check(copy.Result.Status==Workstation.Done && copy.Result.Project==drive.Label && copy.Result.PhotoText.Contains("错误候选"),"conflicting photo hint recorded but reliable volume selects project");
            }
        }
        private static void CheckFallback(string root,SimulatedDrive drive,string label)
        {
            string disk=Directory.CreateDirectory(Path.Combine(root,"offline-target")).FullName;
            var prior=new Catalog(Path.Combine(root,"legacy")); var baseline=prior.Create(label,disk,@"X:\","");
            var second=Program.PrepareTrial(Path.Combine(root,"legacy"));
            var book=new Catalog(second);
            Check(book.List().Count==1 && book.List()[0].Photo==baseline.Photo && File.Exists(baseline.Photo),"first launch migrates the previous registry and retains local photo access");
            Workstation s=new Workstation(book,drive,delegate(string p,CancellationToken t){throw new IOException("模拟接口不可用");},delegate {return "model";});
            drive.Loaded=true;
            s.Register(label,disk,baseline.TargetId,@"X:\","",true);
            var run=s.Copy(delegate{}); Spin(()=>run.IsCompleted);
            Check(run.Result.Status==Workstation.Done && run.Result.VisionNote.Contains("模拟接口不可用") && run.Result.Project==drive.Label,"model error falls back to volume, preserves original photo, and finishes rescue");
            Check(prior.List().Count==1 && book.List().Count==2,"previous version cannot remove new trial records on next save");
        }
    }
}
