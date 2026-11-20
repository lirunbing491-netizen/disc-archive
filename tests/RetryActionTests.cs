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

internal static class RetryActionTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int checks;
    private sealed class Source : DiscSource
    {
        internal bool Fail=true;
        internal int BadOpens,GoodOpens;
        internal override IEnumerable<string> Entries(string p) {return DiscSource.Windows.Entries(p);}
        internal override FileAttributes Attributes(string p) {return DiscSource.Windows.Attributes(p);}
        internal override long Length(string p) {return DiscSource.Windows.Length(p);}
        internal override DateTime Modified(string p) {return DiscSource.Windows.Modified(p);}
        internal override Stream OpenRead(string p) {
            if(Path.GetFileName(p)=="bad.bin") {BadOpens++;if(Fail)throw new IOException("synthetic unreadable item");}
            else GoodOpens++;
            return DiscSource.Windows.OpenRead(p);
        }
    }
    private sealed class Reader : IReader
    {
        internal string Root,Other;
        internal bool Loaded,Connected=true,Wrong,Coverage;
        internal Source Source=new Source();
        internal int Opens;
        internal bool BlockReady;
        internal readonly ManualResetEvent ReadyStarted=new ManualResetEvent(false),ReadyRelease=new ManualResetEvent(false);
        public string[] Drives() {return Connected ? new[]{@"X:\",@"Y:\"} : new[]{@"Y:\"};}
        public bool Empty(string d) {return d==@"Y:\" || !Loaded;}
        public bool Ready(string d) {
            if(BlockReady && d==@"X:\") {ReadyStarted.Set();if(!ReadyRelease.WaitOne(15000))throw new TimeoutException("synthetic ready gate");}
            return d==@"X:\" && Connected && Loaded;
        }
        public MountedDisc Open(string d) {
            if(!Ready(d))throw new IOException("synthetic no media");
            Opens++;
            var disc=new MountedDisc(Wrong?Other:Root,"241101-Test",delegate {},null,Source);
            if(Coverage)disc.UncoveredTracks="synthetic unsupported track";
            return disc;
        }
    }
    private static T Field<T>(object o,string n) {return (T)o.GetType().GetField(n,Hidden|BindingFlags.Public).GetValue(o);}
    private static object Call(object o,string n,params object[] a) {return o.GetType().GetMethod(n,Hidden).Invoke(o,a);}
    private static void Check(bool ok,string text) {if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    private static void Wait(Func<bool> predicate,string why) {
        var clock=Stopwatch.StartNew();while(clock.Elapsed.TotalSeconds<25) {Application.DoEvents();if(predicate())return;Thread.Sleep(10);}throw new TimeoutException(why);
    }
    private static void Await(Task task) {Wait(()=>task.IsCompleted,"retry task");task.GetAwaiter().GetResult();}
    private static Button NoticeRetry(CompletionNotice notice) {
        Form w=Field<Form>(notice,"driveWindow");
        return w==null?null:w.Controls.OfType<FlowLayoutPanel>().SelectMany(p=>p.Controls.OfType<Button>()).FirstOrDefault();
    }
    private static Entry Read(string state,string id) {return new Catalog(state).List().Single(e=>e.Id==id);}
    [STAThread]
    public static int Main()
    {
        Application.EnableVisualStyles();
        string root=Path.Combine(Path.GetTempPath(),"RetryActionTests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string state=Directory.CreateDirectory(Path.Combine(root,"state")).FullName;
            string target=Directory.CreateDirectory(Path.Combine(root,"target")).FullName;
            string source=Directory.CreateDirectory(Path.Combine(root,"source")).FullName;
            string other=Directory.CreateDirectory(Path.Combine(root,"other")).FullName;
            File.WriteAllText(Path.Combine(source,"good.bin"),"original good payload");File.WriteAllText(Path.Combine(source,"bad.bin"),"original recovered payload");
            File.WriteAllText(Path.Combine(other,"different.bin"),"another disc");
            string photo=Path.Combine(root,"photo.png");using(var b=new Bitmap(64,64))b.Save(photo,ImageFormat.Png);
            new VisionOptions {Mode="metadata"}.Save(state);
            var reader=new Reader {Root=source,Other=other};
            Rectangle area=new Rectangle(SystemInformation.VirtualScreen.Right+5000,SystemInformation.VirtualScreen.Bottom+5000,1000,800);
            using(var notice=new CompletionNotice(()=>area,500))
            using(var form=new MainForm(state,reader,notice)) {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=area.Location;form.Show();
                Field<TextBox>(form,"target").Text=target;
                Call(form,"RegisterFor",Field<object>(form,"selectedSlot"),photo,true);
                Workstation worker=Field<Workstation>(form,"station");Entry first=worker.Current;
                reader.Loaded=true;Await((Task)Call(form,"Poll",Field<object>(form,"selectedSlot"),false));
                Entry partial=Read(state,first.Id);
                Check(partial.Status==Workstation.Partial && partial.Files==1 && partial.Failed>0,"actual unreadable source produces partial completion");
                Button noticeButton=NoticeRetry(notice);
                Check(noticeButton!=null && noticeButton.Text.Contains("X:") && noticeButton.Text.Contains("重试这张盘"),"partial notification offers drive-specific retry");
                Button retry=Field<Button>(form,"retryDisc");
                Wait(()=>Field<Form>(notice,"driveWindow")==null,"notice expiry");
                Check(retry.Visible && retry.Enabled,"selected drive retains retry after notification expiry");
                int badBefore=reader.Source.BadOpens,goodBefore=reader.Source.GoodOpens;
                reader.Source.Fail=false;
                form.RetryConfirmation=q=>{Check(q.Contains(first.Id) && q.Contains(@"X:\") && q.Contains("照片"),"retry confirmation identifies original photo, number and drive");return true;};
                retry.PerformClick();Wait(()=>worker.Current==null && !worker.Busy && Read(state,first.Id).Status==Workstation.Done,"same inserted disc retry");
                Entry done=Read(state,first.Id);
                Check(done.Files==2 && done.Failed==0 && done.ReusedFiles==1,"retry repairs bad item while retaining verified good item");
                Check(reader.Source.BadOpens>badBefore && reader.Source.GoodOpens>goodBefore,"retry actually reads recovered source and verifies good source");
                Check(new Catalog(state).List().Count==1 && done.RegistrationId==first.RegistrationId && done.Photo==first.Photo,"retry retains original registration and photograph");
                Check(!retry.Visible && NoticeRetry(notice)==null,"fully completed result has no retry action");
                // Build a complete identity with an uncovered track, then test changed media rejection.
                worker.ChooseMatch=(e,m,c)=>Task.FromResult<DiscMatch>(null);
                reader.Loaded=false;reader.Coverage=true;
                Call(form,"RegisterFor",Field<object>(form,"selectedSlot"),photo,true);
                Entry second=worker.Current;reader.Loaded=true;Await((Task)Call(form,"Poll",Field<object>(form,"selectedSlot"),false));
                Entry protectedEntry=Read(state,second.Id);Check(protectedEntry.Status==Workstation.Partial,"coverage warning retains partial status");
                form.RetryConfirmation=q=>true;
                object slot=Field<object>(form,"selectedSlot");object expected=Field<object>(slot,"RetryEntry");
                reader.Wrong=true;reader.Coverage=false;
                Await((Task)Call(form,"RetrySlot",slot,expected));
                Entry retained=Read(state,second.Id);
                Check(retained.Status==protectedEntry.Status && retained.Report==protectedEntry.Report && retained.Files==protectedEntry.Files,"wrong disc cannot overwrite protected original receipt");
                Check(Field<string>(slot,"Detail").Contains("冲突"),"wrong disc identity conflict is shown on home screen");
                reader.Connected=false;int opens=reader.Opens;
                Await((Task)Call(form,"RetrySlot",slot,Field<object>(slot,"RetryEntry")));
                Check(reader.Opens==opens,"disconnected drive never opens through stale result");
                reader.Connected=true;reader.Wrong=false;reader.Loaded=true;
                object stale=Field<object>(slot,"RetryEntry");
                reader.BlockReady=true;
                Task pending=(Task)Call(form,"RetrySlot",slot,stale);
                Wait(()=>reader.ReadyStarted.WaitOne(0),"background readiness probe");
                Task duplicate=(Task)Call(form,"RetrySlot",slot,stale);
                Check(duplicate.IsCompleted && !pending.IsCompleted && Field<ComboBox>(form,"drives").Enabled,"slow media query leaves UI responsive and suppresses duplicate retry");
                reader.Loaded=false;
                Call(form,"RegisterFor",slot,photo,true);
                reader.BlockReady=false;reader.ReadyRelease.Set();Await(pending);
                Await((Task)Call(form,"RetrySlot",slot,stale));
                Check(worker.Current!=null && worker.Current.Id!=second.Id && reader.Opens==opens,"new registration invalidates pending readiness check and old retry callback");
                worker.Stop();form.Close();
            }
            Console.WriteLine("PASS "+checks+" checks. Generated fixtures: "+root);return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);Console.Error.WriteLine(root);return 1;}
    }
}
