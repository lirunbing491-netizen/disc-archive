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
        private readonly Workstation station;
        private readonly CompletionNotice finishNotice;
        private readonly TextBox target = new TextBox(), project = new TextBox();
        private readonly ComboBox drives = new ComboBox();
        private readonly Button chooseTarget = ButtonOf("选择文件夹"), pick = ButtonOf("选择盘面照片…"), refresh = ButtonOf("刷新光驱"),
            watchButton = ButtonOf("自动接收照片…"), manual = ButtonOf("核对已放入的盘…"), stop = ButtonOf("停止本张");
        private readonly Label headline = LabelOf("", 17), info = LabelOf("", 10), detail = LabelOf("", 10), watchLabel = LabelOf("照片来源：手动选择或拖入", 9);
        private readonly PictureBox picture = Picture();
        private readonly TabControl tabs = new TabControl();
        private CameraPanel camera;
        private VisionSettingsPanel visionPanel;
        private VisionOptions vision;
        private readonly string stateDirectory;
        private readonly Label cameraJob = LabelOf("",11);
        private bool shutterEmpty;
        private readonly Timer timer = new Timer { Interval = 900 };
        private FileSystemWatcher watcher;
        private DateTime settleUntil, armedAt, photoDeadline;
        private bool polling, closeLater, muteSelection;
        private string nextPhoto, targetId = "", lastProgress = "", lastShown = "";
        private readonly object messageLock = new object();
        private List<Entry> entries;

        internal MainForm(string state, IReader reader) : this(state,reader,new CompletionNotice()) { }

        internal MainForm(string state, IReader reader, CompletionNotice completionNotice)
        {
            finishNotice=completionNotice;
            stateDirectory=state;
            settingsFile = Path.Combine(state, "设置.xml");
            vision=VisionOptions.Load(state);
            station = new Workstation(new Catalog(state), reader,ReadPhoto,delegate { return vision.Mode; });
            Text = "导盘助手";
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
            FormClosed += delegate { timer.Dispose(); EndWatch(); finishNotice.Dispose(); SetPhoto(picture, null); SetPhoto(recordPhoto, null); };
        }

        private bool Active { get { return station.Current != null; } }
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
            TableLayoutPanel page = Table(-100); Row(page, 65); Row(page, -100);
            page.Padding = new Padding(18, 4, 18, 14);
            Label top = LabelOf("光盘归档工作台", 20);
            top.ForeColor = Color.FromArgb(20, 84, 76);
            page.Controls.Add(top, 0, 0);
            tabs.Dock = DockStyle.Fill;
            TabPage capture = new TabPage("  导盘  ") { BackColor = Color.White, Padding = new Padding(12) };
            TabPage library = new TabPage("  登记册  ") { BackColor = Color.White, Padding = new Padding(8) };
            tabs.TabPages.AddRange(new TabPage[] { capture, library });
            page.Controls.Add(tabs, 0, 1); Controls.Add(page);
            TableLayoutPanel c = Table(90, -100, 140);
            foreach (int h in new int[] {43,43,43,47,32,-100,47}) Row(c,h);
            capture.Controls.Add(c);
            target.ReadOnly = true; target.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            project.Anchor = AnchorStyles.Left | AnchorStyles.Right; project.MaxLength = 240;
            drives.Anchor = AnchorStyles.Left | AnchorStyles.Right; drives.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Controls.Add(LabelOf("归档位置",10),0,0); c.Controls.Add(target,1,0); c.Controls.Add(chooseTarget,2,0);
            c.Controls.Add(LabelOf("读取光驱",10),0,1); c.Controls.Add(drives,1,1); c.Controls.Add(refresh,2,1);
            c.Controls.Add(LabelOf("本张项目",10),0,2); c.Controls.Add(project,1,2); c.Controls.Add(LabelOf("留空自动判断",9),2,2);
            AddWide(c,Flow(pick, watchButton),3); AddWide(c,watchLabel,4);
            TableLayoutPanel body = Table(-34,-66); Row(body,-100);
            body.Controls.Add(picture,0,0);
            TableLayoutPanel right = Table(-100); right.Padding = new Padding(12,0,4,0);
            Row(right,55); Row(right,58); Row(right,-100);
            right.Controls.Add(headline,0,0); right.Controls.Add(info,0,1); right.Controls.Add(detail,0,2); body.Controls.Add(right,1,0);
            AddWide(c,body,5); AddWide(c,Flow(manual,stop),6);
            BuildLibrary(library);
            TabPage cameraPage = new TabPage("  摄像头  ") { BackColor = Color.White, Padding = new Padding(8) };
            tabs.TabPages.Add(cameraPage);
            TableLayoutPanel cameraLayout=Table(-100); Row(cameraLayout,-100); Row(cameraLayout,54); Row(cameraLayout,44);
            camera=new CameraPanel(delegate(string path) {
                try { Register(path,shutterEmpty); } finally { shutterEmpty=false; }
            });
            camera.BeforeShutter=delegate {
                if(Active || !Directory.Exists(target.Text) || drives.SelectedItem==null)
                    throw new IOException("请先在导盘页选好归档位置和光驱，并结束上一张任务。");
                shutterEmpty=station.Reader.Empty(drives.Text);
            };
            cameraLayout.Controls.Add(camera,0,0); cameraLayout.Controls.Add(cameraJob,0,1);
            Button details=ButtonOf("回导盘页查看 / 停止"); details.Click+=delegate { tabs.SelectedIndex=0; };
            cameraLayout.Controls.Add(details,0,2); cameraPage.Controls.Add(cameraLayout);
            Button snap=ButtonOf("用摄像头拍盘面"); snap.Click+=delegate { tabs.SelectedTab=cameraPage; };
            FlowLayoutPanel photoBar=(FlowLayoutPanel)pick.Parent;
            photoBar.Controls.Add(snap); photoBar.Controls.SetChildIndex(snap,0);
            tabs.SelectedIndexChanged+=delegate { if(tabs.SelectedTab!=cameraPage) camera.StopPreview(); };
            TabPage visionPage=new TabPage("  识图设置  "){ BackColor=Color.White, Padding=new Padding(8) };
            visionPanel=new VisionSettingsPanel(vision,delegate(VisionOptions next) {
                if(Active) throw new InvalidOperationException("请在当前盘结束后更改识图设置。");
                next.Save(stateDirectory); vision=next.Clone();
            });
            visionPanel.Dock=DockStyle.Fill; visionPage.Controls.Add(visionPanel); tabs.TabPages.Add(visionPage);
            chooseTarget.Click += delegate { UserAction(delegate {
                string path = Folder("选择存放光盘资料的硬盘文件夹", target.Text); if (path == null) return;
                if (new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.CDRom) throw new IOException("请选择可写的归档硬盘。");
                target.Text = path; targetId = ""; EndWatch(); SaveSettings(); SyncButtons();
            }); };
            refresh.Click += delegate { RefreshDrives(drives.Text); SyncButtons(); };
            pick.Click += delegate { using (OpenFileDialog dlg = new OpenFileDialog { Title="选择这一张光盘的盘面照片",Filter="盘面照片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" })
                if (dlg.ShowDialog(this) == DialogResult.OK) UserAction(delegate { Register(dlg.FileName); }); };
            watchButton.Click += delegate { UserAction(ToggleWatch); };
            stop.Click += delegate { UserAction(delegate {
                if (!Confirm("结束这张盘的读取？已登记的照片和已读文件会保留。")) return;
                bool wasBusy = station.Busy; station.Stop();
                if (wasBusy) detail.Text = "正在停止，请等待读取操作返回。";
                else { Idle("已停止本张", "可在登记册选回这张照片并重新读取。"); Reload(); }
            }); };
            manual.Click += async delegate {
                if (polling || !Active || station.Busy) return;
                station.BlockAutomatic();
                if (!Confirm("请把屏幕上的照片与光驱里的实物核对。确认是同一张盘，并现在开始读取？")) return;
                await Poll(true);
            };
        }

        private void RefreshDrives(string preferred)
        {
            drives.Items.Clear(); drives.Items.AddRange(station.Reader.Drives());
            if (preferred != null && drives.Items.Contains(preferred)) drives.SelectedItem = preferred;
            else if (drives.Items.Count != 0) drives.SelectedIndex = 0;
        }
        private string Folder(string title, string initial)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog { Description=title, SelectedPath=Directory.Exists(initial) ? initial : "", ShowNewFolderButton=true })
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
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
            chooseTarget.Enabled = refresh.Enabled = drives.Enabled = project.Enabled = pick.Enabled = idle;
            watchButton.Enabled = idle || watcher != null;
            manual.Enabled = Active && !station.Busy; stop.Enabled = Active;
            if(camera!=null) camera.SetCaptureEnabled(idle && Directory.Exists(target.Text) && drives.SelectedItem!=null);
            if(visionPanel!=null) visionPanel.SetEditable(idle);
        }
        private void Idle(string title, string message)
        {
            headline.Text = title; info.Text = message; detail.Text = ""; SyncButtons();
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
        private void Register(string path,bool emptyAtShutter)
        {
            if (Active) throw new InvalidOperationException("上一张盘尚未结束。");
            if (!IsPhoto(path)) throw new IOException("请选择 JPG、PNG、BMP 或 TIFF 盘面照片。");
            if (!Directory.Exists(target.Text)) throw new IOException("请先选择已连接的归档硬盘文件夹。");
            Entry e = station.Register(path, target.Text, targetId, drives.Text, project.Text,emptyAtShutter);
            project.Clear();
            Image preview = Thumbnail(e.Photo);
            SetPhoto(picture, preview); nextPhoto = null; targetId = e.TargetId;
            ShowArmed(e);
            SaveSettings();
        }
        private void ShowArmed(Entry e)
        {
            armedAt = DateTime.UtcNow; headline.Text = e.Id;
            info.Text = station.EmptySeen ? "照片已登记。请把这张盘放入 " + e.Drive : "照片已登记。请取出上一张盘，等窗口提示后再放入本张。";
            detail.Text = (vision.Mode=="metadata" ? "当前按卷标与素材元数据归类。":"盘面识别已开始。")+"若本张已在光驱里，可点下方按钮核对后继续。";
            SyncButtons(); Reload();
        }
        private async Task Tick()
        {
            if (IsDisposed) return;
            TakeNewPhoto();
            if (station.Busy)
            {
                string msg; lock (messageLock) msg = lastProgress;
                if (msg != lastShown) { detail.Text = msg; lastShown = msg; }
            }
            else if (Active) await Poll(false);
            if(!IsDisposed && camera!=null) cameraJob.Text=headline.Text+"　"+(station.Busy ? detail.Text : info.Text);
        }
        private async Task Poll(bool explicitMatch)
        {
            if (polling || !Active || station.Busy) return;
            polling = true;
            Entry armed = station.Current;
            try
            {
                bool ready = await station.CanStart(explicitMatch);
                if (IsDisposed || Disposing || station.Current != armed) return;
                if (ready)
                {
                    headline.Text = "正在归类和抢救"; info.Text = armed.Id + "   ·   " + armed.Drive;
                    detail.Text = "等待盘面和卷标信息，确定归档位置…"; lock(messageLock) lastProgress = lastShown = detail.Text;
                    Task<Entry> task = station.Copy(delegate(string msg) { lock(messageLock) lastProgress = msg; });
                    SyncButtons();
                    Entry result = await task;
                    string heading=result.Status==Workstation.Done ? "本张已拷出，可以换盘" : result.Status+"，请查看本盘记录";
                    string counts="完整拷出 "+result.Files+" 个文件（"+SizeText(result.Bytes)+"），失败条目 "+result.Failed+"。";
                    Idle(heading,result.Id+"　"+counts);
                    detail.Text = result.Project+"\n"+result.Note;
                    string noticeText=result.Project+"　"+result.Id+"\r\n"+counts;
                    if(result.Status!=Workstation.Done) noticeText+="\r\n"+result.Note;
                    if(!closeLater) finishNotice.Show(this,heading,noticeText,result.Status!=Workstation.Done);
                    Reload();
                }
                else if (explicitMatch) Notice("光驱还没有可读取的文件系统。可稍等再试；一直读不出时，请停止本张并保留实盘。");
                else if (station.ManualOnly) info.Text = "自动配对已暂停。请核对当前照片和实物后继续。";
                else if (station.EmptySeen) info.Text = vision.Mode=="metadata" ? "照片已绑定。请放入本张光盘，挂载后按盘上信息归类。" : station.RecognitionFinished ? "盘面判读已结束，正在等待光盘挂载。" : "正在识别盘面，请同时放入本张光盘。";
                if (!ready && (DateTime.UtcNow-armedAt).TotalSeconds > 60)
                    detail.Text = "仍未开始？确认实物与照片后点“核对已放入的盘”。若读不出，请保留原盘并标为待复查。";
            }
            catch (Exception ex) { if (Active) station.BlockAutomatic(); Idle("需要核对本张记录", ex.Message); }
            finally { polling = false; if (closeLater && !station.Busy) { closeLater = false; Close(); } }
        }
        private static string SizeText(long n) { return n >= 1073741824 ? (n/1073741824d).ToString("0.00")+" GB" : (n/1048576d).ToString("0.##")+" MB"; }
        private void UserAction(Action a) { try { a(); } catch (Exception ex) { Notice(ex.Message); } }
        private void Notice(string msg) { MessageBox.Show(this,msg,"导盘助手",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        private bool Confirm(string msg) { return MessageBox.Show(this,msg,"请核对",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes; }
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            if (station.Busy)
            {
                e.Cancel = true;
                if (!closeLater && Confirm("停止这张盘并退出？已读文件会保留在本次记录里。"))
                { closeLater = true; station.Stop(); detail.Text = "正在等待读取停止…"; }
                return;
            }
            if (Active) { try { station.Stop(); } catch (Exception ex) { Notice("本次记录须在下次启动时核对。 " + ex.Message); } }
            timer.Stop();
        }
    }
}
