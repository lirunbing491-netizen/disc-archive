using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace DiscArchive
{
    internal sealed partial class MainForm : Form
    {
        private readonly string settingsFile;
        // The controls display the selected drive. Each drive keeps its own worker and result.
        private Workstation station;
        private readonly Catalog book;
        private readonly IReader reader;
        private readonly Dictionary<string,DriveSlot> slots = new Dictionary<string,DriveSlot>(StringComparer.OrdinalIgnoreCase);
        private DriveSlot selectedSlot, shutterSlot;
        private readonly FlowLayoutPanel driveOverview = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, AutoScroll=true, Margin=new Padding(0) };
        private bool rebuildingDrives;
        private sealed class DriveSlot
        {
            internal string Drive, Photo, ProjectHint="", ExistingProject="", Heading="等待盘面照片", Info="请选择或拍摄这张盘的盘面。", Detail="", LastResult="";
            internal volatile string Progress;
            internal DateTime ArmedAt;
            internal bool Polling, Connected;
            internal readonly Workstation Worker;
            internal Button Indicator;
            internal Entry RetryEntry;
            internal bool RetryChecking;
            internal DriveSlot(string drive, Workstation worker) { Drive=drive; Worker=worker; }
        }
        private readonly CompletionNotice finishNotice;
        private readonly TextBox target = new TextBox(), project = new TextBox();
        private readonly ComboBox drives = new ComboBox();
        private readonly ComboBox filesystemView = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Anchor=AnchorStyles.Left|AnchorStyles.Right };
        private readonly Button inspectDisc=ButtonOf("检测盘格式…");
        private Button useExisting;
        private readonly Button chooseTarget = ButtonOf("选择文件夹"), pick = ButtonOf("选择盘面照片…"), refresh = ButtonOf("刷新光驱"),
            manual = ButtonOf("核对已放入的盘…"), stop = ButtonOf("暂停本张…"), retryDisc=ButtonOf("重试这张盘");
        private readonly Label headline = LabelOf("", 17), info = LabelOf("", 10), detail = LabelOf("", 10);
        private readonly PictureBox picture = Picture();
        private readonly TabControl tabs = new TabControl();
        private CameraPanel camera;
        private VisionSettingsPanel visionPanel;
        private VisionOptions vision;
        private readonly string stateDirectory;
        private readonly Label cameraJob = LabelOf("",11);
        private bool shutterEmpty;
        private readonly Timer timer = new Timer { Interval = 900 };
        private bool closeLater, confirmingExit, muteSelection;
        private string targetId = "";
        private List<Entry> entries;

        internal MainForm(string state, IReader reader) : this(state,reader,new CompletionNotice()) { }

        internal MainForm(string state, IReader reader, CompletionNotice completionNotice)
        {
            finishNotice=completionNotice;
            stateDirectory=state;
            settingsFile = Path.Combine(state, "设置.xml");
            vision=VisionOptions.Load(state);
            book=new Catalog(state);
            this.reader=reader;
            Workstation.RecoverInterrupted(book);
            selectedSlot=NewSlot(""); station=selectedSlot.Worker;
            Text = "导盘助手 · 登记册试用版";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(930, 635);
            MinimumSize = new Size(740, 565);
            BackColor = Color.White;
            Build();
            LoadSettings();
            RefreshDrives(null);
            Reload();
            Idle("等待盘面照片", "先选归档位置，再点“用摄像头拍盘面”。也可直接拖入已有照片。");
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs a) { a.Effect = !Active && a.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            DragDrop += delegate(object s, DragEventArgs a) {
                string[] paths = a.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length != 1) { Notice("一次只接收一张照片，请选择本张盘面。"); return; }
                UserAction(delegate { Register(paths[0]); });
            };
            timer.Tick += async delegate { await Tick(); };
            Shown += delegate { timer.Start(); };
            FormClosing += ClosingForm;
            FormClosed += delegate { timer.Dispose(); finishNotice.Dispose(); SetPhoto(picture, null); SetPhoto(recordPhoto, null); };
        }

        private bool Active { get { return station.Current != null; } }
        private bool AnyActive { get { return slots.Values.Any(s=>s.Worker.Current!=null || s.Worker.Busy); } }
        private Entry LiveEntry(string id)
        {
            return slots.Values.Select(s=>s.Worker.Current).FirstOrDefault(e=>e!=null && e.Id==id);
        }
        private DriveSlot NewSlot(string drive)
        {
            var worker=new Workstation(book,reader,ReadPhoto,delegate { return vision.Mode; },false);
            worker.ChooseMatch=(e,items,ct)=>ArchiveChoices.Match(this,e.Drive,e.Id,items,ct,e.Photo);
            worker.ChooseProject=(e,proposed,items,ct)=>ArchiveChoices.Project(this,e.Drive,proposed,items,ct);
            return new DriveSlot(drive,worker);
        }
        private static Label LabelOf(string text, float size)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Font = new Font("Microsoft YaHei UI", size), ForeColor = Color.FromArgb(42, 56, 62), Margin = new Padding(4) };
        }
        private static Button ButtonOf(string text)
        {
            return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(7, 3, 7, 3),
                FlatStyle = FlatStyle.System, Margin = new Padding(3), Anchor = AnchorStyles.Left };
        }
        private static PictureBox Picture() { return new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(235, 240, 239), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(4) }; }
        private static TableLayoutPanel Table(params float[] widths)
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = widths.Length, Margin = new Padding(0), Padding = new Padding(0) };
            foreach (float w in widths) t.ColumnStyles.Add(w < 0 ? new ColumnStyle(SizeType.Percent, -w) : new ColumnStyle(SizeType.Absolute, w));
            return t;
        }
        private static void Row(TableLayoutPanel t, float height) { t.RowCount++; t.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, -height) : new RowStyle(SizeType.Absolute, height)); }
        private static FlowLayoutPanel Flow(params Control[] items)
        {
            FlowLayoutPanel p = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), AutoScroll = true };
            p.Controls.AddRange(items); return p;
        }
        private static void AddWide(TableLayoutPanel t, Control c, int row) { t.Controls.Add(c, 0, row); t.SetColumnSpan(c, t.ColumnCount); }
        private void Build()
        {
            TableLayoutPanel page = Table(-100); Row(page, 55); Row(page, 45); Row(page, -100);
            page.Padding = new Padding(18, 4, 18, 14);
            TableLayoutPanel header=Table(-100,100,95,115); Row(header,-100);
            Label top = LabelOf("光盘归档工作台", 20);
            top.ForeColor = Color.FromArgb(20, 84, 76);
            header.Controls.Add(top,0,0); header.Controls.Add(LabelOf("本次操作光驱",10),1,0);
            header.Controls.Add(drives,2,0); header.Controls.Add(refresh,3,0);
            page.Controls.Add(header,0,0); page.Controls.Add(driveOverview,0,1);
            tabs.Dock = DockStyle.Fill;
            TabPage capture = new TabPage("  导盘  ") { BackColor = Color.White, Padding = new Padding(12) };
            TabPage library = new TabPage("  登记册  ") { BackColor = Color.White, Padding = new Padding(8) };
            tabs.TabPages.AddRange(new TabPage[] { capture, library });
            page.Controls.Add(tabs, 0, 2); Controls.Add(page);
            TableLayoutPanel c = Table(90, -100, 140);
            foreach (int h in new int[] {43,43,36,47,32,-100,47}) Row(c,h);
            capture.Controls.Add(c);
            target.ReadOnly = true; target.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            project.Anchor = AnchorStyles.Left | AnchorStyles.Right; project.MaxLength = 240;
            drives.Anchor = AnchorStyles.Left | AnchorStyles.Right; drives.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Controls.Add(LabelOf("新盘存到",10),0,0); c.Controls.Add(target,1,0); c.Controls.Add(chooseTarget,2,0);
            c.Controls.Add(LabelOf("本张项目",10),0,1); c.Controls.Add(project,1,1);
            useExisting=ButtonOf("选已有项目…"); c.Controls.Add(useExisting,2,1);
            useExisting.Click+=async delegate {
                DriveSlot lane=selectedSlot; string root=target.Text;
                try {
                    if(!Directory.Exists(root)) throw new IOException("请先选择归档位置。");
                    var choices=await Task.Run(()=>ProjectAliases.ListKnownProjects(root,System.Threading.CancellationToken.None));
                    if(IsDisposed || lane!=selectedSlot || Active || root!=target.Text) return;
                    if(choices.Length==0) { Notice("此归档位置还没有可选的项目目录。"); return; }
                    string chosen=ArchiveChoices.PickExisting(this,choices,project.Text);
                    if(chosen!=null && lane==selectedSlot && !Active && root==target.Text) { project.Text=chosen; lane.ExistingProject=chosen; }
                }catch(Exception ex){ if(!IsDisposed) Notice(ex.Message); }
            };
            c.Controls.Add(LabelOf("读取方式",10),0,2);
            filesystemView.Items.AddRange(new object[]{"自动：优先 Windows，挂载失败时读取扇区","Mac 视图（HFS+ / HFSX 等）","UDF 视图","ISO / Joliet 视图"});
            filesystemView.SelectedIndex=0;
            c.Controls.Add(filesystemView,1,2); c.Controls.Add(inspectDisc,2,2);
            filesystemView.SelectedIndexChanged+=delegate {
                var sys=reader as SystemReader;
                if(sys!=null && selectedSlot.Connected && !Active && filesystemView.SelectedIndex>=0)
                    sys.SetView(selectedSlot.Drive,(OpticalView)filesystemView.SelectedIndex);
            };
            inspectDisc.Click+=async delegate {
                DriveSlot slot=selectedSlot;
                if(!slot.Connected || slot.Worker.Current!=null || slot.Polling) return;
                slot.Polling=true; SyncButtons();
                try {
                    string text=await Task.Run(delegate {
                        var tracks=OpticalTracks.Inspect(slot.Drive);
                        try {
                            using(var raw=RawOptical.Open(slot.Drive)) { var d=OpticalLayout.Inspect(raw.Data);raw.Verify();return OpticalMount.Describe(d)+(tracks.HasUncoveredTracks ? "\r\n"+tracks.Note : ""); }
                        }catch(IOException){ if(tracks.HasUncoveredTracks)return tracks.Note;throw; }
                    });
                    if(!IsDisposed)Notice(slot.Drive+" 格式检测结果\r\n"+text);
                }catch(Exception ex){if(!IsDisposed)Notice(slot.Drive+" 格式检测未完成："+ex.Message);}
                finally { slot.Polling=false; if(!IsDisposed && !Disposing) SyncButtons(); }
            };
            AddWide(c,Flow(pick),3); AddWide(c,LabelOf("可用摄像头拍盘面，也可手选或拖入一张已有照片。",9),4);
            TableLayoutPanel body = Table(-34,-66); Row(body,-100);
            body.Controls.Add(picture,0,0);
            TableLayoutPanel right = Table(-100); right.Padding = new Padding(12,0,4,0);
            Row(right,55); Row(right,58); Row(right,-100);
            right.Controls.Add(headline,0,0); right.Controls.Add(info,0,1); right.Controls.Add(detail,0,2); body.Controls.Add(right,1,0);
            var handbook=ButtonOf("使用说明");
            handbook.Click+=delegate { UserAction(OpenHandbook); };
            HelpRequested+=delegate(object sender,HelpEventArgs e) { e.Handled=true; UserAction(OpenHandbook); };
            AddWide(c,body,5); AddWide(c,Flow(manual,stop,retryDisc,handbook),6);
            retryDisc.Click+=async delegate { await RetrySlot(selectedSlot,selectedSlot.RetryEntry); };
            BuildLibrary(library);
            TabPage cameraPage = new TabPage("  摄像头  ") { BackColor = Color.White, Padding = new Padding(8) };
            tabs.TabPages.Add(cameraPage);
            TableLayoutPanel cameraLayout=Table(-100); Row(cameraLayout,-100); Row(cameraLayout,54); Row(cameraLayout,44);
            camera=new CameraPanel(delegate(string path) {
                if(shutterSlot==null) throw new IOException("请重新选择本次拍摄的光驱。");
                try { RegisterFor(shutterSlot,path,shutterEmpty); } finally { shutterEmpty=false; shutterSlot=null; }
            });
            camera.BeforeShutter=delegate {
                shutterSlot=null;
                if(Active || !selectedSlot.Connected || !Directory.Exists(target.Text))
                    throw new IOException("请选择归档位置和一台空闲光驱。");
                shutterEmpty=reader.Empty(selectedSlot.Drive);
                shutterSlot=selectedSlot;
            };
            cameraLayout.Controls.Add(camera,0,0); cameraLayout.Controls.Add(cameraJob,0,1);
            Button details=ButtonOf("回导盘页查看 / 停止"); details.Click+=delegate { tabs.SelectedIndex=0; };
            cameraLayout.Controls.Add(details,0,2); cameraPage.Controls.Add(cameraLayout);
            InstallCameraKeys(cameraPage);
            Button snap=ButtonOf("用摄像头拍盘面"); snap.Click+=delegate { tabs.SelectedTab=cameraPage; };
            FlowLayoutPanel photoBar=(FlowLayoutPanel)pick.Parent;
            photoBar.Controls.Add(snap); photoBar.Controls.SetChildIndex(snap,0);
            Button byId=ButtonOf("按旧盘号续导…");
            byId.Click+=delegate { UserAction(ResumeNumber); };
            photoBar.Controls.Add(byId);
            tabs.SelectedIndexChanged+=delegate { if(tabs.SelectedTab!=cameraPage) camera.StopPreview(); };
            TabPage visionPage=new TabPage("  识图设置  "){ BackColor=Color.White, Padding=new Padding(8) };
            visionPanel=new VisionSettingsPanel(vision,delegate(VisionOptions next) {
                if(AnyActive) throw new InvalidOperationException("请在各光驱的任务结束后更改识图设置。");
                next.Save(stateDirectory); vision=next.Clone();
            });
            visionPanel.Dock=DockStyle.Fill; visionPage.Controls.Add(visionPanel); tabs.TabPages.Add(visionPage);
            chooseTarget.Click += delegate { UserAction(delegate {
                string path = ArchiveFolder("选择存放光盘资料的位置", target.Text); if (path == null) return;
                if (!path.StartsWith(@"\\") && new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.CDRom) throw new IOException("请选择可写的硬盘或共享文件夹。");
                target.Text = path; targetId = ""; SaveSettings(); SyncButtons();
            }); };
            refresh.Click += delegate { RefreshDrives(drives.Text); SyncButtons(); };
            drives.SelectedIndexChanged+=delegate { if(!rebuildingDrives) SelectDrive(); };
            drives.SelectionChangeCommitted+=delegate {
                BeginInvoke((Action)delegate {
                    if(!IsDisposed && !Disposing && tabs.SelectedTab==cameraPage && drives.Focused && !drives.DroppedDown)
                        camera.FocusPreviewForShutter();
                });
            };
            pick.Click += delegate { using (OpenFileDialog dlg = new OpenFileDialog { Title="选择这一张光盘的盘面照片",Filter="盘面照片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" })
                if (dlg.ShowDialog(this) == DialogResult.OK) UserAction(delegate { Register(dlg.FileName); }); };
            stop.Click += delegate { UserAction(delegate {
                DriveSlot slot=selectedSlot; Entry original=slot.Worker.Current;
                if(original==null || !Confirm("暂停 "+slot.Drive+" 的 "+original.Id+"？已完成的文件保留，下次可换光驱按盘号续导，其他光驱继续读取。")) return;
                if(slot.Worker.Current!=original) return;
                bool wasBusy=slot.Worker.Busy; slot.Worker.Stop();
                if(wasBusy) slot.Detail="正在停止，请等待读取操作返回。";
                else { slot.LastResult="已停止"; slot.Heading="已停止本张"; slot.Info="在登记册可按原盘号重新读取。";slot.Detail="";Reload(); }
                RenderSlot(slot); SyncButtons();
            }); };
            manual.Click += async delegate {
                DriveSlot slot=selectedSlot;
                if (slot.Polling || !Active || station.Busy) return;
                Entry e=slot.Worker.Current; slot.Worker.BlockAutomatic();
                if (!Confirm("确认 "+slot.Drive+" 内的实物与当前照片（"+e.Id+"）一致，并开始读取？")) return;
                if(slot.Worker.Current==e) await Poll(slot,true);
            };
        }

        private void RefreshDrives(string preferred)
        {
            var connected=new HashSet<string>(reader.Drives(),StringComparer.OrdinalIgnoreCase);
            foreach(var slot in slots.Values) {
                bool online=connected.Contains(slot.Drive);
                if(slot.Connected && !online) {
                    slot.Worker.BlockAutomatic();
                }
                slot.Connected=online;
            }
            foreach(string drive in connected)
            {
                if(!slots.ContainsKey(drive)) slots.Add(drive,NewSlot(drive));
                slots[drive].Connected=true;
            }
            var visible=slots.Values.Where(s=>s.Connected || s.Worker.Current!=null || s.Polling || s.Drive==preferred)
                .OrderBy(s=>s.Drive,StringComparer.OrdinalIgnoreCase).ToArray();
            rebuildingDrives=true;
            try
            {
                drives.Items.Clear();
                while(driveOverview.Controls.Count>0) driveOverview.Controls[0].Dispose();
                foreach(var slot in slots.Values) slot.Indicator=null;
                foreach(var slot in visible)
                {
                    string drive=slot.Drive;
                    drives.Items.Add(drive);
                    slot.Indicator=ButtonOf(drive);
                    slot.Indicator.Click+=delegate {
                        drives.SelectedItem=drive;
                        if(camera!=null && camera.Visible) camera.FocusPreviewForShutter();
                    };
                    driveOverview.Controls.Add(slot.Indicator);
                }
                if(preferred!=null && drives.Items.Contains(preferred)) drives.SelectedItem=preferred;
                else if(drives.Items.Count!=0) drives.SelectedIndex=0;
            }
            finally { rebuildingDrives=false; }
            SelectDrive();
        }
        private void SelectDrive()
        {
            selectedSlot.ProjectHint=project.Text;
            DriveSlot slot;
            if(!slots.TryGetValue(drives.Text,out slot)) slot=NewSlot("");
            selectedSlot=slot; station=slot.Worker;
            var sys=reader as SystemReader;
            filesystemView.SelectedIndex=sys==null ? 0 : (int)sys.View(slot.Drive);
            project.Text=slot.ProjectHint;
            try { SetPhoto(picture,String.IsNullOrEmpty(slot.Photo) ? null : Thumbnail(slot.Photo)); }
            catch(IOException) { SetPhoto(picture,null); }
            RenderSlot(slot); SyncButtons(); ShowRecord();
        }
        private void RenderSlot(DriveSlot slot)
        {
            string state=slot.Worker.Current!=null ? slot.Worker.Current.Status : (slot.LastResult.Length>0 ? slot.LastResult : "空闲");
            if(!slot.Connected) state="未连接";
            if(slot.Indicator!=null)
            {
                string caption=slot.Drive.TrimEnd('\\')+"  "+state;
                if(slot.Indicator.Text!=caption) slot.Indicator.Text=caption;
            }
            if(slot!=selectedSlot) return;
            headline.Text=slot.Heading; info.Text=slot.Info;
            detail.Text=slot.Worker.Busy && !String.IsNullOrEmpty(slot.Progress) ? slot.Progress : slot.Detail;
            cameraJob.Text=(slot.Drive.Length==0 ? "请选择光驱" : slot.Drive+"　"+slot.Heading)+"　"+(slot.Worker.Busy ? detail.Text : info.Text);
        }
        private string Folder(string title, string initial)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog { Description=title, SelectedPath=Directory.Exists(initial) ? initial : "", ShowNewFolderButton=true })
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
        }
        private string ArchiveFolder(string title,string initial)
        {
            using(Form dialog=new Form { Text=title, StartPosition=FormStartPosition.CenterParent, Font=Font,
                FormBorderStyle=FormBorderStyle.FixedDialog, MinimizeBox=false, MaximizeBox=false, ClientSize=new Size(590,146) })
            {
                var layout=Table(-100,86); layout.Padding=new Padding(12); Row(layout,33); Row(layout,42); Row(layout,-100);
                AddWide(layout,LabelOf(@"选择已有文件夹，也可以粘贴共享路径，例如 \\文件服务器\素材归档。",10),0);
                var path=new TextBox { Text=initial, Anchor=AnchorStyles.Left|AnchorStyles.Right };
                var browse=ButtonOf("浏览…"); layout.Controls.Add(path,0,1); layout.Controls.Add(browse,1,1);
                browse.Click+=delegate { string p=Folder(title,path.Text); if(p!=null) path.Text=p; };
                var ok=ButtonOf("使用此位置"); var back=ButtonOf("取消"); back.DialogResult=DialogResult.Cancel;
                ok.Click+=delegate {
                    string p=path.Text.Trim();
                    if(!Path.IsPathRooted(p) || !Directory.Exists(p)) {
                        MessageBox.Show(dialog,"请填写已连接、可以访问的完整文件夹路径。",title); return;
                    }
                    path.Text=Path.GetFullPath(p); dialog.DialogResult=DialogResult.OK;
                };
                AddWide(layout,Flow(ok,back),2); dialog.Controls.Add(layout);
                dialog.AcceptButton=ok; dialog.CancelButton=back;
                return dialog.ShowDialog(this)==DialogResult.OK ? path.Text : null;
            }
        }
        private void SaveSettings()
        {
            new XDocument(new XElement("settings", new XElement("target",target.Text),new XElement("id",targetId))).Save(settingsFile);
        }
        private void LoadSettings()
        {
            if (!File.Exists(settingsFile)) return;
            try { XElement e = XDocument.Load(settingsFile).Root; target.Text = (string)e.Element("target") ?? ""; targetId = (string)e.Element("id") ?? ""; }
            catch (Exception ex) { Notice("上次设置无法读取，请重新选择归档位置。\n" + ex.Message); }
        }
        private void SyncButtons()
        {
            bool idle = !Active;
            chooseTarget.Enabled = project.Enabled = useExisting.Enabled = idle && !closeLater;
            refresh.Enabled = drives.Enabled = !closeLater;
            pick.Enabled=idle && selectedSlot.Connected && !closeLater;
            inspectDisc.Enabled=filesystemView.Enabled=pick.Enabled && !selectedSlot.Polling && reader is SystemReader;
            manual.Enabled = Active && !station.Busy; stop.Enabled = Active;
            retryDisc.Visible=selectedSlot.RetryEntry!=null;
            retryDisc.Enabled=CanRetry(selectedSlot,selectedSlot.RetryEntry);
            if(camera!=null) camera.SetCaptureEnabled(pick.Enabled && !String.IsNullOrEmpty(target.Text));
            if(visionPanel!=null) visionPanel.SetEditable(!AnyActive && !closeLater);
        }
        private void Idle(string title, string message)
        {
            selectedSlot.Heading=title; selectedSlot.Info=message; selectedSlot.Detail="";
            RenderSlot(selectedSlot); SyncButtons();
        }
        private static bool IsPhoto(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return new string[]{".jpg",".jpeg",".png",".bmp",".tif",".tiff"}.Contains(ext);
        }
        private string ReadPhoto(string path,System.Threading.CancellationToken cancel)
        {
            VisionOptions settings=vision.Clone();
            if(settings.Mode=="model") return VisionEndpoint.Read(settings,path,cancel);
            if(settings.Mode=="ocr") return LabelOcr.Read(path);
            return "";
        }
        private static Image Thumbnail(string path)
        {
            using (FileStream stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using (Image img = Image.FromStream(stream, true, true))
            {
                const int limit = 900;
                double ratio = Math.Min(1d, limit / (double)Math.Max(img.Width,img.Height));
                return new Bitmap(img, Math.Max(1,(int)(img.Width*ratio)),Math.Max(1,(int)(img.Height*ratio)));
            }
        }
        private static void SetPhoto(PictureBox box, Image img)
        {
            Image old = box.Image; box.Image = img; if (old != null) old.Dispose();
        }
        private void Register(string path) { Register(path,false); }
        private void Register(string path,bool emptyAtShutter) { RegisterFor(selectedSlot,path,emptyAtShutter); }
        private void RegisterFor(DriveSlot slot,string path,bool emptyAtShutter)
        {
            if(closeLater || slot.Worker.Current!=null || slot.Polling) throw new InvalidOperationException(slot.Drive+" 上一张盘尚未结束。");
            if(!slot.Connected) throw new IOException("请选择一台已连接的光驱。");
            if (!IsPhoto(path)) throw new IOException("请选择 JPG、PNG、BMP 或 TIFF 盘面照片。");
            if (!Directory.Exists(target.Text)) throw new IOException("请先选择已连接的归档硬盘文件夹。");
            string hint=slot==selectedSlot ? project.Text : slot.ProjectHint;
            string chosen=hint==slot.ExistingProject ? slot.ExistingProject : null;
            Entry e=slot.Worker.Register(path,target.Text,targetId,slot.Drive,hint,emptyAtShutter,chosen);
            slot.ExistingProject="";
            slot.ProjectHint=""; if(slot==selectedSlot) project.Clear();
            targetId=e.TargetId;
            ArmSlot(slot,e);
            SaveSettings();
        }
        private void ShowArmed(Entry e) { ArmSlot(selectedSlot,e); }
        private void ArmSlot(DriveSlot slot,Entry e)
        {
            if(slot.RetryEntry!=null) finishNotice.ClearDrive(slot.Drive);
            slot.RetryEntry=null;
            slot.ArmedAt=DateTime.UtcNow; slot.Photo=e.Photo; slot.Heading=e.Id; slot.LastResult=""; slot.Progress=null;
            slot.Info=slot.Worker.EmptySeen ? "照片已登记。请把这张盘放入 "+e.Drive : "请取出 "+e.Drive+" 的上一张盘，等提示后放入本张。";
            slot.Detail=(vision.Mode=="metadata" ? "按盘上信息归类。":"正在识别盘面。")+" 若本张已在光驱中，可以手动核对。";
            if(slot==selectedSlot) SetPhoto(picture,Thumbnail(e.Photo));
            RenderSlot(slot); SyncButtons(); Reload();
        }
        private async Task Tick()
        {
            if (IsDisposed || Disposing || closeLater || confirmingExit) return;
            DriveSlot[] active=slots.Values.Where(s=>s.Worker.Current!=null).ToArray();
            foreach(var slot in active) RenderSlot(slot);
            // Start every eligible probe before awaiting any drive's copy operation.
            await Task.WhenAll(active.Where(s=>!s.Polling && !s.Worker.Busy).Select(s=>Poll(s,false)).ToArray());
        }
        private async Task Poll(DriveSlot slot,bool explicitMatch)
        {
            Workstation worker=slot.Worker;
            if (slot.Polling || closeLater || worker.Current==null || worker.Busy) return;
            slot.Polling=true;
            Entry armed=worker.Current;
            try
            {
                bool ready=await worker.CanStart(explicitMatch);
                if (IsDisposed || Disposing || closeLater || confirmingExit || worker.Current!=armed) return;
                if (ready)
                {
                    slot.Heading="正在归类和抢救"; slot.Info=armed.Id+"　"+armed.Drive+" → "+armed.TargetRoot;
                    slot.Progress="等待盘面和卷标信息，确定归档位置…";
                    Task<Entry> task=worker.Copy(delegate(string msg) { slot.Progress=msg; });
                    RenderSlot(slot); SyncButtons();
                    Entry result=await task;
                    string heading=result.Status!=Workstation.Done ? result.Status+"，请查看本盘记录" :
                        result.ReusedFiles>0 && result.ReusedFiles==result.Files ? "已导过，核对完成，可以换盘" :
                        result.ReusedFiles>0 ? "续导完成，可以换盘" : "本张已拷出，可以换盘";
                    string counts="完整 "+result.Files+" 个文件（"+SizeText(result.Bytes)+"），其中沿用 "+result.ReusedFiles+" 个，本次新拷 "+(result.Files-result.ReusedFiles)+" 个，失败条目 "+result.Failed+"。";
                    slot.LastResult=result.Status; slot.Heading=heading; slot.Info=result.Id+"　"+counts;
                    slot.Progress=null; slot.Detail=result.Project+"\n"+result.Note;
                    string ejectNote=worker.LastEjectNote;
                    if(result.Status==Workstation.Done && !String.IsNullOrEmpty(ejectNote) && !worker.LastEjectAccepted) {
                        heading="已拷出，未自动弹盘，请手动取盘";
                        slot.Heading=heading;
                    }
                    if(!String.IsNullOrEmpty(ejectNote))slot.Detail+="\n"+ejectNote;
                    slot.RetryEntry=Retryable(result) ? result : null;
                    string noticeText=result.Project+"　"+result.Id+"\r\n"+counts;
                    if(result.Status!=Workstation.Done) noticeText+="\r\n"+result.Note;
                    if(!String.IsNullOrEmpty(ejectNote))noticeText+="\r\n"+ejectNote;
                    Entry retryResult=slot.RetryEntry;
                    Action retryAction=retryResult==null ? (Action)null : async delegate { await RetrySlot(slot,retryResult); };
                    if(!closeLater) finishNotice.ShowDrive(this,slot.Drive,heading,noticeText,result.Status!=Workstation.Done,retryAction);
                    Reload();
                }
                else if (explicitMatch) Notice("光驱还没有可读取的文件系统。可稍等再试；一直读不出时，请停止本张并保留实盘。");
                else if (worker.ManualOnly) slot.Info=slot.Drive+" 自动配对已暂停，请核对本光驱里的盘和照片。";
                else if (worker.EmptySeen) slot.Info="请把照片中这张盘放入 "+slot.Drive+"，就绪后自动归档。";
                if (!ready && (DateTime.UtcNow-slot.ArmedAt).TotalSeconds>60)
                    slot.Detail="仍未开始？确认实物与照片后点“核对已放入的盘”。若读不出，请保留原盘并标为待复查。";
            }
            catch(Exception ex) { worker.BlockAutomatic(); slot.Heading="需要核对本张记录"; slot.Info=ex.Message; slot.Detail=""; }
            finally {
                slot.Polling=false;
                if(!IsDisposed && !Disposing) {
                    RenderSlot(slot); SyncButtons();
                    if(closeLater && !AnyActive) Close();
                }
            }
        }
        private static string SizeText(long n) { return n >= 1073741824 ? (n/1073741824d).ToString("0.00")+" GB" : (n/1048576d).ToString("0.##")+" MB"; }
        private static void OpenHandbook()
        {
            string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"使用说明.txt");
            if(!File.Exists(file)) throw new FileNotFoundException("请完整解压发布包，使用说明与程序在同一个文件夹。");
            Process.Start(new ProcessStartInfo(file) {UseShellExecute=true});
        }
        private void UserAction(Action a) { try { a(); } catch (Exception ex) { Notice(ex.Message); } }
        private void Notice(string msg) { MessageBox.Show(this,msg,"导盘助手",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        private bool Confirm(string msg) { return MessageBox.Show(this,msg,"请核对",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes; }
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            if(confirmingExit) { e.Cancel=true; return; }
            var active=slots.Values.Where(s=>s.Worker.Current!=null || s.Worker.Busy).ToArray();
            if(active.Length>0 && !closeLater) {
                confirmingExit=true;
                bool confirmed;
                try { confirmed=Confirm("仍有任务在 "+String.Join("、",active.Select(s=>s.Drive))+"。停止这些任务并退出？已读文件会保留。"); }
                finally { confirmingExit=false; }
                if(!confirmed) { e.Cancel=true; return; }
                closeLater=true;
                foreach(var item in slots.Values.Where(s=>s.Worker.Current!=null).ToArray())
                {
                    try { item.Worker.Stop(); } catch(Exception ex) { closeLater=false; e.Cancel=true; Notice(ex.Message); return; }
                }
                SyncButtons();
            }
            if(AnyActive) { e.Cancel=true; return; }
            timer.Stop();
        }
    }
}
