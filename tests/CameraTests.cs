using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DiscArchive;

internal static class CameraTests
{
    private static int checks;
    private static void Four(BinaryWriter w,string s) { w.Write(System.Text.Encoding.ASCII.GetBytes(s)); }
    private static long Begin(BinaryWriter w,string s) {Four(w,s);w.Write(0);return w.BaseStream.Position;}
    private static void End(BinaryWriter w,long p) {long end=w.BaseStream.Position;w.BaseStream.Position=p-4;w.Write((int)(end-p));w.BaseStream.Position=end;}
    private static void SyntheticAvi(string path)
    {
        using(BinaryWriter w=new BinaryWriter(File.Create(path))) {
            long riff=Begin(w,"RIFF");Four(w,"AVI ");long hdrl=Begin(w,"LIST");Four(w,"hdrl");long avih=Begin(w,"avih");foreach(int n in new int[]{100000,23040,0,16,30,0,1,2304,32,24,0,0,0,0})w.Write(n);End(w,avih);
            long strl=Begin(w,"LIST");Four(w,"strl");long strh=Begin(w,"strh");Four(w,"vids");w.Write(0);w.Write(0);w.Write((short)0);w.Write((short)0);foreach(int n in new int[]{0,1,10,0,30,2304,-1,0})w.Write(n);foreach(short n in new short[]{0,0,32,24})w.Write(n);End(w,strh);
            long strf=Begin(w,"strf");foreach(int n in new int[]{40,32,24})w.Write(n);w.Write((short)1);w.Write((short)24);foreach(int n in new int[]{0,2304,0,0,0,0})w.Write(n);End(w,strf);End(w,strl);End(w,hdrl);
            long movi=Begin(w,"LIST");Four(w,"movi");for(int i=0;i<30;i++){Four(w,"00db");w.Write(2304);w.Write(new byte[2304]);}End(w,movi);long index=Begin(w,"idx1");for(int i=0;i<30;i++){Four(w,"00db");w.Write(16);w.Write(4+i*2312);w.Write(2304);}End(w,index);End(w,riff);
        }
    }
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        checks++; Console.WriteLine("PASS " + description);
    }
    [STAThread]
    public static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "摄像头测试-中文路径-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if(args.Length>0 && args[0]=="--synthetic-graph") {string avi=Path.Combine(root,"generated.avi");SyntheticAvi(avi);using(CameraSession session=new CameraSession(avi)){var watch=System.Diagnostics.Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<5){using(Bitmap frame=session.GetFrame()){if(frame!=null){Check(frame.Width==32&&frame.Height==24,"real DirectShow generated AVI produces a frame; memory disposed without output");return;}}Application.DoEvents();System.Threading.Thread.Sleep(50);}throw new Exception("Synthetic graph returned no frame");}}
            // Three pixels need three padding bytes per row. Input is bottom-up BGR.
            byte[] bytes = new byte[] { 255,0,0, 255,0,0, 255,0,0, 0,0,0,
                                       0,0,255, 0,0,255, 0,0,255, 0,0,0 };
            IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                using (Bitmap frame = CameraSession.CopyRgb24(memory, 3, 2, 12, true))
                {
                    Check(frame.GetPixel(1,0).R == 255 && frame.GetPixel(1,1).B == 255, "RGB24 bottom-up orientation, channel order and row padding");
                    string first = CameraPanel.SaveFrame(frame, root);
                    string second = CameraPanel.SaveFrame(frame, root);
                    Check(first != second, "rapid consecutive captures have unique paths");
                    using (Image saved = Image.FromFile(first)) Check(saved.Width == 3 && saved.Height == 2, "completed JPEG decodes from Chinese path");
                    using (FileStream exclusive = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        Check(exclusive.Length > 0, "save releases file handle before callback");
                }
                using (Bitmap frame = CameraSession.CopyRgb24(memory, 3, 2, 12, false))
                    Check(frame.GetPixel(1,0).B == 255 && frame.GetPixel(1,1).R == 255, "top-down source orientation");
            }
            finally { Marshal.FreeCoTaskMem(memory); }
            using (Bitmap frame = new Bitmap(20,20))
            {
                string shot = CameraPanel.SaveFrame(frame, root);
                string localPhoto = Path.Combine(root, "登记后的照片.jpg");
                int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CameraPanel.DeliverSavedPhoto(shot, delegate(string path)
                {
                    Check(System.Threading.Thread.CurrentThread.ManagedThreadId == uiThread, "capture callback runs synchronously on the calling UI thread");
                    using (Image image = Image.FromFile(path)) Check(image.Width == 20, "callback receives a complete, still-existing JPEG");
                    File.Copy(path, localPhoto);
                });
                Check(!File.Exists(shot) && File.Exists(localPhoto), "successful registration keeps its copied photo and releases temporary capture");
                string failedShot = CameraPanel.SaveFrame(frame, root);
                bool callbackFailed = false;
                try { CameraPanel.DeliverSavedPhoto(failedShot, delegate(string path) { throw new IOException("模拟登记失败"); }); }
                catch (IOException) { callbackFailed = true; }
                Check(callbackFailed && File.Exists(failedShot), "registration failure retains the capture for recovery");
                string blocked = Path.Combine(root, "文件不是文件夹"); File.WriteAllText(blocked, "keep");
                bool failed = false;
                try { CameraPanel.SaveFrame(frame, blocked); } catch (IOException) { failed = true; }
                Check(failed && File.ReadAllText(blocked) == "keep", "failed save preserves existing user file");
            }
            foreach (string clsid in new string[] { "e436ebb3-524f-11ce-9f53-0020af0ba770", "bf87b6e1-8c27-11d0-b3f0-00aa003761c5", "c1f400a0-3f08-11d3-9f0b-006008039e37", "c1f400a4-3f08-11d3-9f0b-006008039e37" })
            {
                object native = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), true));
                Check(native != null, "Windows camera component available: " + clsid);
                Marshal.ReleaseComObject(native);
            }
            CameraFrameClock clock = new CameraFrameClock();
            clock.BufferCB(0, IntPtr.Zero, 0);
            Check(clock.LastTicks == 0, "empty frames do not count as fresh camera data");
            clock.BufferCB(0, IntPtr.Zero, 100);
            Check(clock.LastTicks > 0, "camera callback tracks live frame arrival without storing images");
            IntPtr callback = Marshal.GetComInterfaceForObject(clock, typeof(ICameraGrabberCallback));
            Check(callback != IntPtr.Zero, "frame freshness callback exposes the native COM interface");
            Marshal.Release(callback);
            // Hardware enumeration is opt-in and read-only; no preview or capture starts.
            if (Array.IndexOf(args, "--enumerate-read-only") >= 0)
            {
                var devices = CameraSession.EnumerateDevices();
                Console.WriteLine("READ-ONLY camera enumeration: " + devices.Count);
                foreach (var device in devices) Console.WriteLine("DEVICE " + device.Name);
            }
            int callbacks = 0;
            using (CameraPanel panel = new CameraPanel(delegate(string path) { callbacks++; }))
            { panel.SetCaptureEnabled(false); panel.SetCaptureEnabled(true); panel.StopPreview(); }
            Check(callbacks == 0, "constructing, disabling and stopping panel never captures a photo");
            Console.WriteLine("PASS " + checks + " checks. Live camera preview/capture intentionally not started.");
        }
        finally { Directory.Delete(root, true); }
    }
}
