using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using System.Threading;

namespace DiscArchive
{
    // Uses Windows DirectShow; no external camera runtime or executable is needed.
    internal sealed class CameraPanel : UserControl
    {
        private readonly Action<string> onCapture;
        private readonly ComboBox devices = new ComboBox();
        private readonly Button refresh = new Button(), open = new Button(), shutter = new Button();
        private readonly Label status = new Label();
        private readonly PictureBox preview = new PictureBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private CameraSession session;
        private bool captureEnabled = true;
        private bool taking;

        public CameraPanel(Action<string> onCapture)
        {
            if (onCapture == null) throw new ArgumentNullException("onCapture");
            this.onCapture = onCapture;
            Dock = DockStyle.Fill;
            devices.DropDownStyle = ComboBoxStyle.DropDownList; devices.Width = 260;
            refresh.Text = "刷新设备"; refresh.AutoSize = true;
            open.Text = "打开预览"; open.AutoSize = true;
            shutter.Text = "拍下这张盘面"; shutter.AutoSize = true; shutter.Enabled = false;
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43, AutoSize = true, Padding = new Padding(6) };
            bar.Controls.AddRange(new Control[] { devices, refresh, open, shutter });
            status.Dock = DockStyle.Bottom; status.Height = 56; status.Padding = new Padding(8);
            status.Text = "选择内置或 USB 摄像头后打开预览。只有按快门时才保存照片。";
            preview.Dock = DockStyle.Fill; preview.BackColor = Color.FromArgb(30, 30, 30); preview.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(preview); Controls.Add(status); Controls.Add(bar);
            refresh.Click += delegate { RefreshDevices(); };
            open.Click += delegate { if (session == null) StartPreview(); else StopPreview(); };
            devices.SelectedIndexChanged += delegate { StopPreview(); };
            shutter.Click += delegate { TakePhoto(); };
            timer.Interval = 120;
            timer.Tick += delegate { UpdatePreview(); };
            RefreshDevices();
        }

        public Action BeforeShutter { get; set; }

        public void SetCaptureEnabled(bool enabled)
        {
            captureEnabled = enabled;
            shutter.Enabled = enabled && session != null && preview.Image != null && !taking;
        }

        public void StopPreview()
        {
            timer.Stop();
            if (session != null) { session.Dispose(); session = null; }
            ReplacePreview(null);
            shutter.Enabled = false; open.Text = "打开预览";
            status.Text = "预览已关闭。只有按快门时才保存照片。";
        }

        private void RefreshDevices()
        {
            StopPreview();
            devices.Items.Clear();
            try
            {
                foreach (CameraDevice device in CameraSession.EnumerateDevices()) devices.Items.Add(device);
                if (devices.Items.Count > 0) devices.SelectedIndex = 0;
                status.Text = devices.Items.Count == 0 ? "未发现摄像头。请连接 USB 摄像头，然后刷新设备。" : "请选择摄像头，然后点击“打开预览”。只有按快门时才保存照片。";
            }
            catch (Exception ex) { status.Text = "无法读取摄像头列表：" + ex.Message; }
            open.Enabled = devices.Items.Count > 0;
        }

        private void StartPreview()
        {
            CameraDevice device = devices.SelectedItem as CameraDevice;
            if (device == null) return;
            try
            {
                session = new CameraSession(device);
                open.Text = "关闭预览";
                status.Text = "摄像头正在预览，等待首帧。请将盘面放清晰后按快门；不会自动拍照。";
                timer.Start();
            }
            catch (Exception ex)
            {
                StopPreview();
                status.Text = "无法打开摄像头：" + ex.Message + " 请检查 Windows 摄像头隐私权限、连接，或关闭占用它的其他软件。";
            }
        }

        private void UpdatePreview()
        {
            try
            {
                Bitmap frame = session == null ? null : session.GetFrame();
                if (frame == null) return;
                ReplacePreview(frame);
                shutter.Enabled = captureEnabled && !taking;
                status.Text = "正在预览（" + frame.Width + " × " + frame.Height + "）。请确认盘面文字清晰，再拍下这张盘面。";
            }
            catch (Exception ex)
            {
                StopPreview();
                status.Text = "摄像头预览中断：" + ex.Message + " 请检查连接，然后重新打开预览。";
            }
        }

        private void TakePhoto()
        {
            if (!captureEnabled || taking || session == null) return;
            taking = true; shutter.Enabled = false;
            string path = null;
            try
            {
                Action beforeShutter = BeforeShutter;
                if (beforeShutter != null) beforeShutter();
                using (Bitmap frame = session.GetFrame())
                {
                    if (frame == null) throw new IOException("摄像头还没有返回画面，请稍后再拍。");
                    path = SaveFrame(frame, Path.Combine(Path.GetTempPath(), "DiscArchive", "摄像头照片"));
                }
                // The callback is synchronous on the WinForms UI thread. Keep the file alive
                // until the caller has copied it; retain it on failure for manual recovery.
                DeliverSavedPhoto(path, onCapture);
                status.Text = "盘面照片已交给当前登记流程。换下一张盘后，可再次按快门。";
            }
            catch (Exception ex)
            {
                status.Text = "拍照或登记失败：" + ex.Message + (path == null ? "" : " 照片已保留：" + path);
                MessageBox.Show(this, status.Text, "摄像头", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { taking = false; SetCaptureEnabled(captureEnabled); }
        }

        internal static string SaveFrame(Bitmap frame, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "盘面-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".jpg");
            // CreateNew and disposal ensure no partial image is exposed to the callback.
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    frame.Save(stream, ImageFormat.Jpeg);
                return path;
            }
            catch { try { File.Delete(path); } catch { } throw; }
        }

        internal static void DeliverSavedPhoto(string path, Action<string> callback)
        {
            callback(path);
            try { File.Delete(path); } catch { }
        }

        private void ReplacePreview(Image image)
        {
            Image old = preview.Image; preview.Image = image;
            if (old != null) old.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopPreview(); timer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class CameraDevice
    {
        internal string Name, Moniker;
        public override string ToString() { return Name; }
    }

    internal sealed class CameraSession : IDisposable
    {
        private object graphObject, sourceObject, grabberObject, sinkObject;
        private ICameraGraph graph;
        private ICameraGrabber grabber;
        private ICameraControl control;
        private readonly DateTime startedAt = DateTime.UtcNow;
        private readonly CameraFrameClock frameClock = new CameraFrameClock();
        private int width, height, stride;
        private bool bottomUp;
        private IntPtr buffer;
        private int bufferCapacity;
        private static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid Rgb24 = new Guid("e436eb7d-524f-11ce-9f53-0020af0ba770");
        private static readonly Guid VideoInfo = new Guid("05589f80-c356-11ce-bf01-00aa0055595a");

        // Device capability inspection binds its filter only: no graph is run and no frame is read.
        internal static List<string> DescribeDevice(CameraDevice device)
        {
            List<string> result=new List<string>(); IBindCtx context=null; IMoniker moniker=null; object source=null; ICameraEnumPins pins=null; ICameraPin pin=null;
            try {Check(CreateBindCtx(0,out context));int eaten;Check(MkParseDisplayName(context,device.Moniker,out eaten,out moniker));Guid iid=typeof(ICameraFilter).GUID;moniker.BindToObject(context,null,ref iid,out source);Check(((ICameraFilter)source).EnumPins(out pins));
                for(int i=0;i<16 && pins.Next(1,out pin,IntPtr.Zero)==0;i++) {try {int direction;Check(pin.QueryDirection(out direction));if(direction!=1)continue;IntPtr id;Check(pin.QueryId(out id));string name;try{name=Marshal.PtrToStringUni(id);}finally{Marshal.FreeCoTaskMem(id);}string category="未提供";try {ICameraKsPropertySet ks=(ICameraKsPropertySet)pin;Guid set=new Guid("9b00f101-1567-11d1-b3f1-00aa003761c5");IntPtr data=Marshal.AllocCoTaskMem(16);try{int got;if(ks.Get(ref set,0,IntPtr.Zero,0,data,16,out got)>=0 && got==16)category=((Guid)Marshal.PtrToStructure(data,typeof(Guid))).ToString();}finally{Marshal.FreeCoTaskMem(data);}}catch(InvalidCastException){}
                    result.Add("PIN "+name+" category="+category);ICameraEnumMediaTypes types=null;try{Check(pin.EnumMediaTypes(out types));for(int j=0;j<16;j++){IntPtr p;if(types.Next(1,out p,IntPtr.Zero)!=0)break;try{CameraMediaType type=(CameraMediaType)Marshal.PtrToStructure(p,typeof(CameraMediaType));try{string format="major="+type.majorType+" subtype="+type.subType;if(type.majorType==Video && type.formatType==VideoInfo && type.formatSize>=Marshal.SizeOf(typeof(CameraVideoInfo))){CameraVideoInfo info=(CameraVideoInfo)Marshal.PtrToStructure(type.formatPtr,typeof(CameraVideoInfo));format+=" "+info.bitmap.width+"x"+Math.Abs(info.bitmap.height)+" "+info.bitmap.bitCount+"bit";}result.Add(format);}finally{FreeMediaType(type);}}finally{Marshal.FreeCoTaskMem(p);}}}finally{Release(types);}
                }finally{Release(pin);pin=null;}}
                return result;
            }finally{Release(pin);Release(pins);Release(source);Release(moniker);Release(context);}
        }

        internal static List<CameraDevice> EnumerateDevices()
        {
            List<CameraDevice> result = new List<CameraDevice>();
            object deviceEnum = null; IEnumMoniker enumerator = null;
            try
            {
                deviceEnum = NewCom("62be5d10-60eb-11d0-bd3b-00a0c911ce86");
                Guid category = new Guid("860bb310-5d01-11d0-bd3b-00a0c911ce86");
                int hr = ((ICameraDeviceEnum)deviceEnum).CreateClassEnumerator(ref category, out enumerator, 0);
                Check(hr); if (enumerator == null) return result;
                IMoniker[] item = new IMoniker[1];
                while (enumerator.Next(1, item, IntPtr.Zero) == 0)
                {
                    object bag = null;
                    try
                    {
                        Guid iid = typeof(ICameraPropertyBag).GUID;
                        item[0].BindToStorage(null, null, ref iid, out bag);
                        object name; Check(((ICameraPropertyBag)bag).Read("FriendlyName", out name, IntPtr.Zero));
                        string display; item[0].GetDisplayName(null, null, out display);
                        result.Add(new CameraDevice { Name = Convert.ToString(name), Moniker = display });
                    }
                    finally { Release(bag); Release(item[0]); item[0] = null; }
                }
                return result;
            }
            finally { Release(enumerator); Release(deviceEnum); }
        }

        internal CameraSession(CameraDevice device) : this(device,null) { }
        internal CameraSession(string syntheticVideo) : this(null,syntheticVideo) { }
        private CameraSession(CameraDevice device,string syntheticVideo)
        {
            string stage = "创建采集图";
            try
            {
                graphObject = NewCom("e436ebb3-524f-11ce-9f53-0020af0ba770"); graph = (ICameraGraph)graphObject;
                if(syntheticVideo!=null) { ICameraFilter source; Check(graph.AddSourceFilter(syntheticVideo,"Synthetic test source",out source));sourceObject=source; }
                else {
                IBindCtx context; Check(CreateBindCtx(0, out context));
                IMoniker moniker = null;
                try
                {
                    int eaten; Check(MkParseDisplayName(context, device.Moniker, out eaten, out moniker));
                    Guid iid = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
                    moniker.BindToObject(context, null, ref iid, out sourceObject);
                }
                finally { Release(moniker); Release(context); }
                stage = "添加摄像头"; Check(graph.AddFilter((ICameraFilter)sourceObject, "Camera"));
                }
                grabberObject = NewCom("c1f400a0-3f08-11d3-9f0b-006008039e37"); grabber = (ICameraGrabber)grabberObject;
                CameraMediaType requested = new CameraMediaType();
                requested.majorType = Video; requested.subType = Rgb24; requested.formatType = VideoInfo;
                stage = "设置RGB格式"; Check(grabber.SetMediaType(requested));
                stage = "设置缓冲"; Check(grabber.SetBufferSamples(true)); Check(grabber.SetOneShot(false));
                stage = "设置帧回调"; Check(grabber.SetCallback(frameClock, 1));
                stage = "添加帧缓冲"; Check(graph.AddFilter((ICameraFilter)grabberObject, "Frame buffer"));
                sinkObject = NewCom("c1f400a4-3f08-11d3-9f0b-006008039e37");
                stage = "添加预览终点"; Check(graph.AddFilter((ICameraFilter)sinkObject, "Preview sink"));
                // Enumerate real output media types before connecting. A registered device
                // can expose a capture-category pin yet advertise no video formats.
                // Connect preserves DirectShow's decoder/color-converter negotiation.
                stage = "连接视频采集链路"; ConnectFilters((ICameraFilter)sourceObject,(ICameraFilter)grabberObject,syntheticVideo!=null); ConnectFilters((ICameraFilter)grabberObject,(ICameraFilter)sinkObject,false);
                CameraMediaType connected = new CameraMediaType();
                try
                {
                    stage = "读取已连接格式"; Check(grabber.GetConnectedMediaType(connected));
                    if (connected.formatType != VideoInfo || connected.formatSize < Marshal.SizeOf(typeof(CameraVideoInfo)))
                        throw new NotSupportedException("此摄像头未提供可用的 RGB 视频格式。");
                    CameraVideoInfo info = (CameraVideoInfo)Marshal.PtrToStructure(connected.formatPtr, typeof(CameraVideoInfo));
                    width = info.bitmap.width; height = Math.Abs(info.bitmap.height); bottomUp = info.bitmap.height > 0;
                    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || info.bitmap.bitCount != 24)
                        throw new NotSupportedException("摄像头返回的画面尺寸或像素格式不受支持。");
                    stride = checked((width * 3 + 3) & ~3);
                }
                finally { FreeMediaType(connected); }
                stage = "开始视频采集"; control = (ICameraControl)graphObject; Check(control.Run());
            }
            catch(Exception ex) { Dispose(); throw new IOException("摄像头初始化失败（"+stage+"）："+ex.Message,ex); }
        }

        internal Bitmap GetFrame()
        {
            long lastFrame = Interlocked.Read(ref frameClock.LastTicks);
            if (lastFrame == 0)
            {
                if ((DateTime.UtcNow - startedAt).TotalSeconds > 10) throw new IOException("摄像头一直没有返回画面。");
                return null;
            }
            if (DateTime.UtcNow.Ticks - lastFrame > TimeSpan.FromSeconds(5).Ticks)
                throw new IOException("摄像头已超过五秒没有更新画面。请检查连接后重新打开预览。");
            int length = 0; int hr = grabber.GetCurrentBuffer(ref length, IntPtr.Zero);
            // VFW_E_WRONG_STATE: no frame has arrived yet.
            if (hr == unchecked((int)0x80040227)) return null;
            Check(hr);
            if (length == 0) return null;
            if (length < checked(stride * height) || length > 512 * 1024 * 1024)
                throw new IOException("摄像头画面数据不完整。");
            if (bufferCapacity < length)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
                buffer = IntPtr.Zero; bufferCapacity = 0;
                buffer = Marshal.AllocCoTaskMem(length); bufferCapacity = length;
            }
            Check(grabber.GetCurrentBuffer(ref length, buffer));
            return CopyRgb24(buffer, width, height, stride, bottomUp);
        }

        internal static Bitmap CopyRgb24(IntPtr pointer, int width, int height, int stride, bool bottomUp)
        {
            Bitmap image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    int sourceY = bottomUp ? height - 1 - y : y;
                    Marshal.Copy(IntPtr.Add(pointer, sourceY * stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            catch { image.UnlockBits(data); image.Dispose(); throw; }
            image.UnlockBits(data); return image;
        }

        public void Dispose()
        {
            if (control != null) { try { control.Stop(); } catch { } control = null; }
            if (grabber != null) { try { grabber.SetCallback(null, 1); } catch { } }
            grabber = null; graph = null;
            Release(sinkObject); sinkObject = null; Release(grabberObject); grabberObject = null;
            Release(sourceObject); sourceObject = null;
            Release(graphObject); graphObject = null;
            if (buffer != IntPtr.Zero) { Marshal.FreeCoTaskMem(buffer); buffer = IntPtr.Zero; bufferCapacity = 0; }
        }
        private static object NewCom(string clsid) { return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), true)); }
        private void ConnectFilters(ICameraFilter source,ICameraFilter target,bool allowStream)
        {
            ICameraEnumPins outputs=null,inputs=null; ICameraPin output=null,input=null; int last=unchecked((int)0x80040217);
            try { Check(source.EnumPins(out outputs)); Check(target.EnumPins(out inputs));
                while(inputs.Next(1,out input,IntPtr.Zero)==0) {int direction;Check(input.QueryDirection(out direction));if(direction==0)break;Release(input);input=null;}
                if(input==null)throw new IOException("视频终点没有可用输入接口。");
                bool videoFound=false;
                while(outputs.Next(1,out output,IntPtr.Zero)==0) {try {int direction;Check(output.QueryDirection(out direction));if(direction!=1 || !HasVideoMedia(output,allowStream))continue;videoFound=true;last=graph.Connect(output,input);if(last>=0)return;}finally{Release(output);output=null;}}
                if(!videoFound)throw new IOException("设备没有暴露可用的视频输出接口（可能只提供音频或设备驱动未就绪）。");
                throw new IOException("视频输出无法连接到RGB帧缓冲，错误码 0x"+last.ToString("X8")+"。");
            }finally{Release(output);Release(input);Release(outputs);Release(inputs);}
        }
        private static bool HasVideoMedia(ICameraPin pin,bool allowStream)
        {
            ICameraEnumMediaTypes types=null; IntPtr pointer=IntPtr.Zero;
            try {Check(pin.EnumMediaTypes(out types));for(int i=0;i<32 && types.Next(1,out pointer,IntPtr.Zero)==0;i++) {try {CameraMediaType type=(CameraMediaType)Marshal.PtrToStructure(pointer,typeof(CameraMediaType));try {if(type.majorType==Video || (allowStream && type.majorType==new Guid("e436eb83-524f-11ce-9f53-0020af0ba770")))return true;}finally{FreeMediaType(type);}}finally{Marshal.FreeCoTaskMem(pointer);pointer=IntPtr.Zero;}}return false;}
            finally{if(pointer!=IntPtr.Zero)Marshal.FreeCoTaskMem(pointer);Release(types);}
        }
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static void FreeMediaType(CameraMediaType type)
        {
            if (type.formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(type.formatPtr);
            if (type.unknown != IntPtr.Zero) Marshal.Release(type.unknown);
        }
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
        [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int MkParseDisplayName(IBindCtx context, string name, out int eaten, out IMoniker moniker);
    }

    [StructLayout(LayoutKind.Sequential)] internal sealed class CameraMediaType
    {
        public Guid majorType, subType;
        [MarshalAs(UnmanagedType.Bool)] public bool fixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool temporalCompression;
        public int sampleSize; public Guid formatType; public IntPtr unknown; public int formatSize; public IntPtr formatPtr;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct CameraRect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct CameraBitmapInfo
    {
        public int size, width, height; public short planes, bitCount; public int compression, imageSize, xPels, yPels, clrUsed, clrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct CameraVideoInfo
    {
        public CameraRect source, target; public int bitRate, bitErrorRate; public long frameTime; public CameraBitmapInfo bitmap;
    }
    [ComImport, Guid("29840822-5b84-11d0-bd3b-00a0c911ce86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraDeviceEnum { [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker enumerator, int flags); }
    [ComImport, Guid("55272a00-42cb-11ce-8135-00aa004bb851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraFilter
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int Stop(); [PreserveSig] int Pause(); [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int timeout,out int state);
        [PreserveSig] int SetSyncSource(IntPtr clock); [PreserveSig] int GetSyncSource(out IntPtr clock);
        [PreserveSig] int EnumPins(out ICameraEnumPins pins);
    }
    [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraEnumPins { [PreserveSig] int Next(int count,out ICameraPin pin,IntPtr fetched); }
    [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraPin
    {
        [PreserveSig] int Connect(ICameraPin other,IntPtr type);
        [PreserveSig] int ReceiveConnection(ICameraPin other,IntPtr type);
        [PreserveSig] int Disconnect(); [PreserveSig] int ConnectedTo(out ICameraPin other);
        [PreserveSig] int ConnectionMediaType([Out,MarshalAs(UnmanagedType.LPStruct)] CameraMediaType type);
        [PreserveSig] int QueryPinInfo(IntPtr info); [PreserveSig] int QueryDirection(out int direction);
        [PreserveSig] int QueryId(out IntPtr id); [PreserveSig] int QueryAccept(IntPtr type);
        [PreserveSig] int EnumMediaTypes(out ICameraEnumMediaTypes types);
    }
    [ComImport, Guid("89c31040-846b-11ce-97d3-00aa0055595a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraEnumMediaTypes { [PreserveSig] int Next(int count,out IntPtr mediaType,IntPtr fetched); }
    [ComImport, Guid("31efac30-515c-11d0-a9aa-00aa0061be93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraKsPropertySet
    {
        [PreserveSig] int Set(ref Guid set,int property,IntPtr instance,int instanceSize,IntPtr data,int size);
        [PreserveSig] int Get(ref Guid set,int property,IntPtr instance,int instanceSize,IntPtr data,int size,out int returned);
    }
    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraGraph
    {
        [PreserveSig] int AddFilter(ICameraFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int RemoveFilter(IntPtr filter);
        [PreserveSig] int EnumFilters(out IntPtr enumerator);
        [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr filter);
        [PreserveSig] int ConnectDirect(IntPtr output, IntPtr input, IntPtr mediaType);
        [PreserveSig] int Reconnect(IntPtr pin);
        [PreserveSig] int Disconnect(IntPtr pin);
        [PreserveSig] int SetDefaultSyncSource();
        [PreserveSig] int Connect(ICameraPin output,ICameraPin input);
        [PreserveSig] int Render(ICameraPin output);
        [PreserveSig] int RenderFile([MarshalAs(UnmanagedType.LPWStr)] string file,[MarshalAs(UnmanagedType.LPWStr)] string playlist);
        [PreserveSig] int AddSourceFilter([MarshalAs(UnmanagedType.LPWStr)] string file,[MarshalAs(UnmanagedType.LPWStr)] string name,out ICameraFilter filter);
    }
    [ComImport, Guid("6b652fff-11fe-4fce-92ad-0266b5d7c78f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraGrabber
    {
        [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool oneShot);
        [PreserveSig] int SetMediaType([In, MarshalAs(UnmanagedType.LPStruct)] CameraMediaType type);
        [PreserveSig] int GetConnectedMediaType([Out, MarshalAs(UnmanagedType.LPStruct)] CameraMediaType type);
        [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool buffer);
        [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
        [PreserveSig] int GetCurrentSample(out IntPtr sample);
        [PreserveSig] int SetCallback(ICameraGrabberCallback callback, int method);
    }
    [ComVisible(true), Guid("0579154a-2b53-4994-b0d0-e773148eff85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICameraGrabberCallback
    {
        [PreserveSig] int SampleCB(double time, IntPtr sample);
        [PreserveSig] int BufferCB(double time, IntPtr buffer, int length);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class CameraFrameClock : ICameraGrabberCallback
    {
        internal long LastTicks;
        public int SampleCB(double time, IntPtr sample) { return 0; }
        public int BufferCB(double time, IntPtr buffer, int length)
        {
            if (length > 0) Interlocked.Exchange(ref LastTicks, DateTime.UtcNow.Ticks);
            return 0;
        }
    }
    [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface ICameraControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
    }
}
