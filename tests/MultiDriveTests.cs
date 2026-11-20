using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DiscArchive
{
    // All media, photographs, state and destinations are generated beneath one temporary root.
    // The real form, its timer, catalog and copy pipeline run; no physical optical device is opened.
    internal static class MultiDriveTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private sealed class Medium
        {
            internal string Drive, Source, Token, Destination;
            internal volatile bool Loaded, Connected = true, Hold;
            internal int Opens;
            internal readonly ManualResetEvent Paused = new ManualResetEvent(false);
            internal readonly ManualResetEvent Release = new ManualResetEvent(false);
            internal void Verify()
            {
                if (!Connected || !Loaded) throw new IOException("Synthetic medium disconnected.");
                if (!Hold || Paused.WaitOne(0)) return;
                // Hold only once a real payload has reached the target, not during planning.
                bool written = Directory.GetFiles(Destination,"*.bin",SearchOption.AllDirectories)
                    .Any(p => { try { return File.ReadAllText(p).StartsWith(Token,StringComparison.Ordinal); }
                        catch(IOException) { return false; } });
                if (!written) return;
                Paused.Set();
                if (!Release.WaitOne(25000)) throw new TimeoutException("Synthetic copy gate was not released.");
            }
        }
        private sealed class Readers : IReader
        {
            internal readonly Medium X, Y;
            internal Readers(Medium x,Medium y) { X=x; Y=y; }
            private Medium Get(string drive)
            {
                if(drive==X.Drive) return X;
                if(drive==Y.Drive) return Y;
                throw new IOException("Only synthetic X/Y drives are permitted.");
            }
            public string[] Drives() { return new[]{X,Y}.Where(m=>m.Connected).Select(m=>m.Drive).ToArray(); }
            public bool Empty(string drive) { Medium m=Get(drive); return m.Connected && !m.Loaded; }
            public bool Ready(string drive) { Medium m=Get(drive); return m.Connected && m.Loaded; }
            public MountedDisc Open(string drive)
            {
                Medium m=Get(drive);
                if(!m.Connected || !m.Loaded) throw new IOException("Synthetic medium is not ready.");
                Interlocked.Increment(ref m.Opens);
                return new MountedDisc(m.Source,"241101-双盘同项目",m.Verify,null);
            }
        }
        private static T Field<T>(MainForm form,string name)
        { return (T)typeof(MainForm).GetField(name,Hidden).GetValue(form); }
        private static void Call(MainForm form,string name,Type[] types,params object[] args)
        { typeof(MainForm).GetMethod(name,Hidden,null,types,null).Invoke(form,args); }
        private static void Check(bool value,string message)
        { if(!value) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
        private static void Spin(Func<bool> predicate,string why,int seconds=18)
        {
            Stopwatch watch=Stopwatch.StartNew();
            do { Application.DoEvents(); if(predicate()) return; Thread.Sleep(15); }
            while(watch.Elapsed.TotalSeconds<seconds);
            throw new TimeoutException(why);
        }
        private static void Settle(int milliseconds)
        { Stopwatch watch=Stopwatch.StartNew(); Spin(()=>watch.ElapsedMilliseconds>=milliseconds,"UI settlement"); }
        private static Workstation Select(MainForm form,string drive)
        {
            ComboBox selector=Field<ComboBox>(form,"drives");
            selector.SelectedItem=drive;
            Application.DoEvents();
            Check(selector.Text==drive,"selected synthetic camera/intake target "+drive);
            return Field<Workstation>(form,"station");
        }
        private static Entry Register(MainForm form,string photo)
        {
            Field<TextBox>(form,"project").Text="双盘同项目";
            Call(form,"Register",new[]{typeof(string),typeof(bool)},photo,false);
            return Field<Workstation>(form,"station").Current;
        }
        private static Entry Read(string state,string id) { return new Catalog(state).List().Single(e=>e.Id==id); }
        private static void Original(Entry entry,string photo,string name)
        { Check(File.ReadAllBytes(entry.OriginalPhoto).SequenceEqual(File.ReadAllBytes(photo)),name+" retains exact original photograph"); }
        private static void Photo(string path,Color color)
        {
            using(Bitmap bitmap=new Bitmap(600,300)) using(Graphics g=Graphics.FromImage(bitmap))
            { g.Clear(color); g.FillEllipse(Brushes.White,45,45,200,200); bitmap.Save(path,ImageFormat.Png); }
        }
        private static Medium Fixture(string root,string drive,string token,string destination)
        {
            string source=Directory.CreateDirectory(Path.Combine(root,token)).FullName;
            Directory.CreateDirectory(Path.Combine(source,"空目录"));
            // Same names, different bytes on both media: collisions must retain each provenance.
            for(int i=0;i<8;i++) File.WriteAllText(Path.Combine(source,"same"+i+".bin"),token+" payload "+i);
            return new Medium {Drive=drive,Source=source,Token=token,Destination=destination};
        }
        private static void Notice(string id,string drive)
        {
            Form notice=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Visible && f.AccessibleRole==AccessibleRole.Alert);
            Check(notice!=null && notice.AccessibleDescription.Contains(id) && notice.AccessibleDescription.Contains(drive.TrimEnd('\\','/')),
                "completion notice identifies its own drive and disc "+drive);
        }
        private static void VerifyPayload(Entry entry,Medium medium)
        {
            Check(entry.Status==Workstation.Done && entry.Files==8 && entry.Failed==0,"persisted complete count belongs to "+medium.Drive);
            string[] rows=Directory.GetFiles(entry.Report,"路径清单.tsv",SearchOption.AllDirectories);
            Check(rows.Length==1,"one readable path receipt for "+entry.Id);
            string[] paths=File.ReadAllLines(rows[0]).Skip(1).Select(s=>s.Split('\t'))
                .Where(c=>c.Length>=3 && c[1].EndsWith(".bin",StringComparison.OrdinalIgnoreCase)).Select(c=>c[2]).Distinct().ToArray();
            Check(paths.Length==8 && paths.All(p=>File.Exists(p) && File.ReadAllText(p).StartsWith(medium.Token,StringComparison.Ordinal)),
                "every receipt maps its own source bytes without cross-drive overwrite "+medium.Drive);
            Check(entry.ArchiveFolders.Length==1 && entry.ArchiveFolders[0]=="241101-双盘同项目","same project destination retained "+medium.Drive);
            string filedPhoto=Path.Combine(entry.TargetRoot,entry.ArchiveFolders[0],"盘面照片",entry.Id+Path.GetExtension(entry.OriginalPhoto));
            Check(File.Exists(filedPhoto) && File.ReadAllBytes(filedPhoto).SequenceEqual(File.ReadAllBytes(entry.OriginalPhoto)),
                "project photograph remains associated with its own disc "+medium.Drive);
        }
        private static void Round(string root,bool reverse)
        {
            string state=Path.Combine(root,"state"), target=Directory.CreateDirectory(Path.Combine(root,"target")).FullName;
            string photoX=Path.Combine(root,"X.png"), photoY=Path.Combine(root,"Y.png");
            Photo(photoX,Color.DarkRed); Photo(photoY,Color.DarkBlue);
            Medium x=Fixture(root,@"X:\","X-original",target), y=Fixture(root,@"Y:\","Y-original",target);
            Readers reader=new Readers(x,y);
            Medium slow=reverse ? y : x, fast=reverse ? x : y;
            slow.Hold=true;
            Rectangle outside=SystemInformation.VirtualScreen;
            outside.Offset(outside.Width+5000,outside.Height+5000);
            using(MainForm form=new MainForm(state,reader,new CompletionNotice(()=>outside,25000)))
            {
                Workstation sx=null,sy=null;
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-18000,-18000); form.Show();
                try
                {
                    Field<TextBox>(form,"target").Text=target;
                    sx=Select(form,x.Drive); Entry ex=Register(form,photoX);
                    sy=Select(form,y.Drive); Entry ey=Register(form,photoY);
                    Check(!Object.ReferenceEquals(sx,sy) && sx.Current.Id==ex.Id && sy.Current.Id==ey.Id,"each drive has its own live workstation and photo");
                    Check(ex.Id!=ey.Id && ex.RegistrationId!=ey.RegistrationId,"two simultaneous registrations receive distinct durable identities");
                    Check(ex.Drive==x.Drive && ey.Drive==y.Drive,"registration persists selected drive");
                    Original(ex,photoX,"X"); Original(ey,photoY,"Y");
                    Workstation slowStation=reverse ? sy : sx, fastStation=reverse ? sx : sy;
                    Entry slowEntry=reverse ? ey : ex, fastEntry=reverse ? ex : ey;
                    slow.Loaded=true;
                    Spin(()=>slow.Paused.WaitOne(0),"one copy must pause after writing real bytes");
                    Check(slowStation.Busy && Directory.GetFiles(target,"*.bin",SearchOption.AllDirectories).Length>0,"gate pauses an actual copy after payload reaches destination");
                    Check(Field<ComboBox>(form,"drives").Enabled,"drive/camera target remains switchable during another copy");
                    Check(Object.ReferenceEquals(Select(form,fast.Drive),fastStation),"busy peer does not capture selected camera target");
                    fast.Loaded=true;
                    Spin(()=>fastStation.Current==null && !fastStation.Busy,"unblocked drive completes independently of paused drive");
                    Check(slowStation.Busy && !slow.Release.WaitOne(0),"other drive completed while first gate stayed closed");
                    VerifyPayload(Read(state,fastEntry.Id),fast); Notice(fastEntry.Id,fast.Drive);
                    Check(Read(state,slowEntry.Id).Status!=Workstation.Done,"fast completion cannot mark paused disc done");
                    Select(form,slow.Drive); slowStation.Stop(); slow.Hold=false; slow.Release.Set();
                    Spin(()=>!slowStation.Busy && slowStation.Current==null,"stopped drive returns independently");
                    Entry stopped=Read(state,slowEntry.Id);
                    Check(stopped.Status!=Workstation.Done && stopped.Note.Contains("停止"),"single-drive cancellation is recorded on that disc");
                    Check(Read(state,fastEntry.Id).Status==Workstation.Done,"single-drive stop preserves peer completion");
                    Original(stopped,reverse ? photoY : photoX,"stopped disc");
                    slow.Loaded=false;
                    slowStation.Retry(stopped,slow.Drive);
                    Call(form,"ShowArmed",new[]{typeof(Entry)},stopped);
                    slow.Loaded=true;
                    Spin(()=>slowStation.Current==null && !slowStation.Busy,"retry completes original disc");
                    Entry retried=Read(state,slowEntry.Id);
                    Check(new Catalog(state).List().Count==2 && retried.RegistrationId==slowEntry.RegistrationId,"retry keeps original disc number and registration identity");
                    VerifyPayload(retried,slow); VerifyPayload(Read(state,fastEntry.Id),fast);
                    // New photo while the previous disc is still inserted must never reuse that media.
                    Select(form,fast.Drive); int opens=fast.Opens;
                    Entry next=Register(form,reverse ? photoY : photoX);
                    Settle(2100);
                    Check(next.Id!=fastEntry.Id && fast.Opens==opens && !fastStation.EmptySeen,"next photo cannot auto-read the previous inserted disc");
                    fast.Connected=false;
                    Spin(()=>fastStation.ManualOnly,"disconnected drive suspends automatic pairing");
                    fast.Connected=true; fast.Loaded=false; Settle(1100); fast.Loaded=true; Settle(1100);
                    Check(fast.Opens==opens && fastStation.Current.Id==next.Id,"reconnection cannot silently associate a replacement disc");
                    fastStation.Stop();
                    Check(Read(state,next.Id).Status==Workstation.Review && slowStation.Current==null,"unavailable waiting disc stops without affecting idle peer");
                    Original(Read(state,next.Id),reverse ? photoY : photoX,"new photo");
                    fast.Connected=false;
                    bool refused=false;
                    try { Register(form,photoX); }
                    catch(TargetInvocationException e) { refused=e.InnerException is IOException; }
                    finally { fast.Connected=true; }
                    Check(refused && fastStation.Current==null,"idle unavailable drive rejects registration without opening hardware");
                    Check(new Catalog(state).List().Count==3,"all three disc records survive local catalog readback");
                    Console.WriteLine("Artifacts: "+root);
                }
                finally
                {
                    x.Hold=y.Hold=false; x.Release.Set(); y.Release.Set();
                    if(sx!=null) sx.Stop(); if(sy!=null) sy.Stop();
                    try { Spin(()=>(sx==null || !sx.Busy) && (sy==null || !sy.Busy),"fixture shutdown",5); }
                    finally { form.Dispose(); }
                }
            }
        }
        private static void IntakeBinding(string root)
        {
            string target=Directory.CreateDirectory(Path.Combine(root,"target")).FullName;
            string photo=Path.Combine(root,"label.png"); Photo(photo,Color.DarkGreen);
            var x=Fixture(root,@"X:\","X",target); var y=Fixture(root,@"Y:\","Y",target);
            using(var f=new MainForm(Path.Combine(root,"state"),new Readers(x,y)))
            {
                f.ShowInTaskbar=false; f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-18000,-18000); f.Show();
                Field<TextBox>(f,"target").Text=target;
                Workstation sx=Select(f,x.Drive);
                Entry selected=Register(f,photo);
                Workstation sy=Select(f,y.Drive);
                Check(sx.Current==selected && selected.Drive==x.Drive && sy.Current==null,"manual photo stays on its selected drive when selection changes");
                Original(selected,photo,"manual selected photo");
                sx.Stop();
                var camera=Field<CameraPanel>(f,"camera");
                camera.BeforeShutter();
                Select(f,x.Drive);
                var delivery=(Action<string>)typeof(CameraPanel).GetField("onCapture",Hidden).GetValue(camera);
                delivery(photo);
                Check(sy.Current!=null && sy.Current.Drive==y.Drive && sx.Current==null,"shutter captures its drive even if selection changes before photo delivery");
                Original(sy.Current,photo,"delayed shutter photo");
                Register(f,photo);
                Check(sx.Current!=null && sy.Current!=null,"manual photo on idle selected drive is independent of pending shutter peer");
                Entry cameraEntry=sy.Current;
                bool busyRefused=false;
                try { Register(f,photo); }
                catch(TargetInvocationException e) { busyRefused=e.InnerException is InvalidOperationException; }
                Check(busyRefused && sy.Current==cameraEntry,"busy manual target rejects a second photo and preserves peer camera record");
                sx.Stop(); sy.Stop(); f.Close();
            }
        }
        [STAThread]
        public static int Main()
        {
            string root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"DiscArchive-MultiDrive-"+Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                Application.EnableVisualStyles();
                Round(Directory.CreateDirectory(Path.Combine(root,"X-mounts-first")).FullName,false);
                Round(Directory.CreateDirectory(Path.Combine(root,"Y-mounts-first")).FullName,true);
                IntakeBinding(Directory.CreateDirectory(Path.Combine(root,"manual-and-camera")).FullName);
                Console.WriteLine("PASS "+checks+" multi-drive checks; generated artifacts retained: "+root); return 0;
            }
            catch(Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Generated artifacts: "+root); return 1; }
        }
    }
}
