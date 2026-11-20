using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiscArchive
{
    // Match and Project must be called on the owner's UI thread. They never run a modal loop.
    internal static class ArchiveChoices
    {
        public static Task<DiscMatch> Match(Form owner, string drive, string currentId, List<DiscMatch> candidates, CancellationToken token, string currentPhoto = null)
        {
            ChoiceWindow<DiscMatch> dialog = new ChoiceWindow<DiscMatch>("核对旧盘 · " + drive);
            dialog.Introduction.Text = "光驱：" + drive + "    本次来源盘号：" + currentId + "\r\n请选择要核对的旧盘。仅卷标相同，仍可能是不同光盘；可信程度不能代替你的确认。";
            ListBox list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            List<DiscMatch> available = candidates == null ? new List<DiscMatch>() : candidates.FindAll(delegate(DiscMatch item) { return item != null; });
            foreach (DiscMatch item in available)
                list.Items.Add((item.Id ?? "（无盘号）") + "  |  " + (item.Project ?? "（未分项目）") + "  |  " + (item.Status ?? "未知状态") + "  |  " + (item.Strong ? "较强匹配" : "需人工核实"));
            TextBox detail = Details();
            PhotoPreview currentPreview = new PhotoPreview("本次盘面", "CurrentDiscPhoto");
            PhotoPreview oldPreview = new PhotoPreview("所选旧盘面", "SelectedOldDiscPhoto");
            TableLayoutPanel photos = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            photos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); photos.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            photos.Controls.Add(currentPreview,0,0); photos.Controls.Add(oldPreview,1,0);
            TableLayoutPanel evidence = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            evidence.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); evidence.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            evidence.Controls.Add(photos,0,0); evidence.Controls.Add(detail,0,1);
            currentPreview.LoadPhoto(currentPhoto,"未保存本次盘面照片，仍可核对盘号和结构。");
            oldPreview.LoadPhoto(null,"请选择旧盘，查看其盘面照片。");
            Button same = dialog.AddButton("确认同一张：核对并补缺", delegate { if (list.SelectedIndex >= 0) dialog.Choose(available[list.SelectedIndex]); });
            same.Enabled = false;
            dialog.AddButton("确认另作新盘", delegate { dialog.Choose(null); });
            dialog.AddCancel("暂不导入");
            list.SelectedIndexChanged += delegate
            {
                same.Enabled = list.SelectedIndex >= 0;
                if (list.SelectedIndex < 0) { oldPreview.LoadPhoto(null,"请选择旧盘，查看其盘面照片。"); detail.Text = "请先选择一条旧盘记录，再查看匹配依据。"; return; }
                DiscMatch item = available[list.SelectedIndex];
                string photoProblem;
                oldPreview.LoadPhoto(OldPhoto(item.JobDir,out photoProblem),photoProblem);
                detail.Text = "旧盘来源盘号：" + item.Id + "\r\n既有状态：" + item.Status + "\r\n项目：" + item.Project
                    + "\r\n可信程度：" + (item.Strong ? "较强匹配（仍须明确确认）" : "有限匹配（请人工核对）")
                    + "\r\n匹配依据：" + item.Reason + "\r\n旧任务位置：" + item.JobDir
                    + "\r\n\r\n确认同一张后，将核对既有文件并补缺。同卷标也可能是不同盘；无法确认时，请另作新盘或暂不导入。";
            };
            detail.Text = available.Count == 0 ? "没有可选旧盘。你仍可明确选择另作新盘，或暂不导入。" : "请先选择一条旧盘记录，再查看匹配依据；不会自动认定同一张盘。";
            dialog.SetContent(list, evidence, 28F);
            return dialog.Open(owner, token);
        }

        private static string OldPhoto(string job,out string problem)
        {
            problem="旧记录未保存可用盘面图，可按原盘号、结构和匹配依据判断。";
            if(String.IsNullOrWhiteSpace(job))return null;
            try {
                string full=Path.GetFullPath(job); NoPhotoLinks(full);
                string selected=null;
                foreach(string extension in new string[]{".jpg",".jpeg",".png",".bmp",".tif",".tiff",".gif"}) {
                    string path=Path.Combine(full,"photo"+extension);
                    if(!File.Exists(path))continue;
                    NoPhotoLinks(path);
                    if(selected!=null) {problem="旧盘有多张原图，无法确定；可按原盘号和结构判断。";return null;}
                    selected=path;
                }
                return selected;
            } catch(IOException) {problem="旧盘面图不可访问，仍可按原盘号和结构判断。";}
            catch(UnauthorizedAccessException) {problem="旧盘面图无读取权限，仍可按原盘号和结构判断。";}
            catch(ArgumentException) { } catch(NotSupportedException) { }
            return null;
        }
        private static void NoPhotoLinks(string path)
        {
            for(string part=Path.GetFullPath(path);!String.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
                if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("盘面图拒绝目录或文件链接。");
        }
        private sealed class PhotoPreview : TableLayoutPanel
        {
            private static readonly SemaphoreSlim Decoders=new SemaphoreSlim(2,2);
            private readonly PictureBox picture;
            private readonly Label status;
            private int version;
            internal PhotoPreview(string caption,string name)
            {
                Dock=DockStyle.Fill;ColumnCount=1;RowCount=3;Margin=new Padding(2);
                RowStyles.Add(new RowStyle(SizeType.Absolute,24));RowStyles.Add(new RowStyle(SizeType.Percent,100));RowStyles.Add(new RowStyle(SizeType.Absolute,36));
                Controls.Add(new Label {Text=caption,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter},0,0);
                picture=new PictureBox {Name=name,Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(239,241,241)};
                status=new Label {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,AutoEllipsis=true,Font=new Font(SystemFonts.MessageBoxFont.FontFamily,8.5F)};
                Controls.Add(picture,0,1);Controls.Add(status,0,2);
            }
            internal async void LoadPhoto(string path,string missing)
            {
                int request=++version;Image previous=picture.Image;picture.Image=null;if(previous!=null)previous.Dispose();
                status.Text=String.IsNullOrEmpty(path)?missing:"正在读取缩略图…";
                if(String.IsNullOrEmpty(path))return;
                Bitmap image=null;
                try {
                    image=await Task.Run(delegate {
                        Decoders.Wait();
                        try {
                            if(request!=Volatile.Read(ref version))return null;
                            NoPhotoLinks(path);
                            if(new FileInfo(path).Length>32*1024*1024)throw new IOException("原图过大");
                            using(FileStream stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                            using(Image source=Image.FromStream(stream,false,true)) {
                                if((long)source.Width*source.Height>80000000)throw new IOException("原图像素过多");
                                double scale=Math.Min(1.0,Math.Min(320.0/source.Width,200.0/source.Height));
                                Bitmap thumb=new Bitmap(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)));
                                try {using(Graphics g=Graphics.FromImage(thumb))g.DrawImage(source,new Rectangle(0,0,thumb.Width,thumb.Height));return thumb;}catch {thumb.Dispose();throw;}
                            }
                        }finally {Decoders.Release();}
                    });
                    if(IsDisposed || Disposing || request!=version) {if(image!=null)image.Dispose();return;}
                    picture.Image=image;image=null;status.Text=picture.Image==null?"照片未加载，可按盘号和结构判断。":"缩略预览，仅辅助核对盘面。";
                } catch(Exception ex) {
                    if(image!=null)image.Dispose();
                    if(!IsDisposed && !Disposing && request==version)status.Text="照片不可预览，仍可按原盘号和结构判断。";
                    System.Diagnostics.Debug.WriteLine("Disc photo preview: "+ex.GetType().Name);
                }
            }
            protected override void Dispose(bool disposing)
            {
                if(disposing) {Interlocked.Increment(ref version);if(picture!=null && picture.Image!=null){Image image=picture.Image;picture.Image=null;image.Dispose();}}
                base.Dispose(disposing);
            }
        }

        public static Task<string> Project(Form owner, string drive, string proposed, string[] options, CancellationToken token)
        {
            ChoiceWindow<string> dialog = new ChoiceWindow<string>("核对项目名称 · " + drive);
            dialog.Introduction.Text = "光驱：" + drive + "\r\n本次名称：" + proposed + "\r\n相似名称可能属于不同项目。只有明确选择，才会归入已有项目。";
            ListBox list;
            Control chooser = ProjectList(options, null, out list);
            TextBox detail = Details();
            detail.Text = "选择已有项目后，请核对完整名称。仍按本次名称会保留：" + proposed + "\r\n关闭窗口或暂缓，均取消本次导入。";
            Button existing = dialog.AddButton("确认归入所选已有项目", delegate { if (list.SelectedItem != null) dialog.Choose((string)list.SelectedItem); });
            existing.Enabled = false;
            list.SelectedIndexChanged += delegate { existing.Enabled = list.SelectedItem != null; if (list.SelectedItem != null) detail.Text = "将归入已有项目：" + (string)list.SelectedItem + "\r\n本次识别名称：" + proposed + "\r\n请确认这两个名称指的是同一项目。"; };
            dialog.AddButton("仍按本次名称", delegate { dialog.Choose(proposed); });
            dialog.AddCancel("暂缓导入");
            dialog.SetContent(chooser, detail);
            return dialog.Open(owner, token);
        }

        // This optional picker is used before importing; it does not make a disc-match decision.
        public static string PickExisting(Form owner, string[] projects, string initial)
        {
            using (Form dialog = NewForm("拍照前选择已有项目"))
            {
                ListBox list;
                Control chooser = ProjectList(projects, initial, out list);
                chooser.Dock = DockStyle.Fill;
                FlowLayoutPanel buttons = ButtonPanel();
                Button confirm = new Button { Text = "使用所选项目", AutoSize = true, MinimumSize = new Size(140, 38), Enabled = list.SelectedItem != null };
                Button cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(100, 38), DialogResult = DialogResult.Cancel };
                string result = null;
                confirm.Click += delegate { if (list.SelectedItem != null) { result = (string)list.SelectedItem; dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
                list.SelectedIndexChanged += delegate { confirm.Enabled = list.SelectedItem != null; };
                buttons.Controls.Add(confirm); buttons.Controls.Add(cancel);
                dialog.Controls.Add(chooser); dialog.Controls.Add(buttons); dialog.CancelButton = cancel;
                return dialog.ShowDialog(owner) == DialogResult.OK ? result : null;
            }
        }

        private static Control ProjectList(string[] projects, string initial, out ListBox list)
        {
            ListBox items = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            TextBox search = new TextBox { Dock = DockStyle.Fill };
            List<string> names = new List<string>();
            foreach (string project in projects ?? new string[0])
                if (!String.IsNullOrWhiteSpace(project) && !names.Contains(project)) names.Add(project);
            Action refill = delegate
            {
                items.BeginUpdate();
                items.Items.Clear();
                foreach (string name in names)
                    if (name.IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0) items.Items.Add(name);
                items.SelectedIndex = -1;
                items.EndUpdate();
            };
            search.TextChanged += delegate { refill(); };
            refill();
            if (initial != null && items.Items.Contains(initial)) items.SelectedItem = initial;
            TableLayoutPanel pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pane.Controls.Add(new Label { Text = "搜索已有项目（不会自动选择搜索结果）", AutoSize = true }, 0, 0);
            pane.Controls.Add(search, 0, 1); pane.Controls.Add(items, 0, 2);
            list = items;
            return pane;
        }

        private static TextBox Details()
        {
            return new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        }

        private static Form NewForm(string title)
        {
            return new Form { Text = title, Font = new Font("Microsoft YaHei UI", 10F), AutoScaleMode = AutoScaleMode.Font,
                ClientSize = new Size(820, 590), MinimumSize = new Size(680, 520), StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = true, ShowInTaskbar = false, Padding = new Padding(14) };
        }

        private static FlowLayoutPanel ButtonPanel()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 8, 0, 0) };
        }

        private sealed class ChoiceWindow<T>
        {
            private readonly Form window;
            private readonly TaskCompletionSource<T> result = new TaskCompletionSource<T>();
            private readonly TableLayoutPanel layout;
            private readonly FlowLayoutPanel buttons;
            private Form owner;
            private CancellationToken token;
            private CancellationTokenRegistration registration;
            private bool finished;
            internal readonly Label Introduction;

            internal ChoiceWindow(string title)
            {
                window = NewForm(title);
                Introduction = new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
                layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
                layout.Controls.Add(Introduction, 0, 0);
                buttons = ButtonPanel();
                window.Controls.Add(layout); window.Controls.Add(buttons);
                window.FormClosed += delegate { Finish(false, default(T)); };
                window.Disposed += delegate { Finish(false, default(T)); };
            }

            internal void SetContent(Control choices, Control detail, float choicesPercent = 48F)
            {
                layout.RowStyles[1].Height=choicesPercent;layout.RowStyles[2].Height=100F-choicesPercent;
                layout.Controls.Add(choices, 0, 1); layout.Controls.Add(detail, 0, 2);
            }

            internal Button AddButton(string text, Action action)
            {
                Button button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(120, 40), Margin = new Padding(0, 4, 10, 4) };
                button.Click += delegate { if (!finished) action(); };
                buttons.Controls.Add(button);
                return button;
            }

            internal void AddCancel(string text)
            {
                window.CancelButton = AddButton(text, delegate { Finish(false, default(T)); });
            }

            internal Task<T> Open(Form parent, CancellationToken cancellation)
            {
                if (parent == null || parent.IsDisposed || parent.Disposing || cancellation.IsCancellationRequested)
                { Finish(false, default(T)); return result.Task; }
                if (parent.InvokeRequired)
                { window.Dispose(); throw new InvalidOperationException("请在主窗口的界面线程调用 ArchiveChoices。"); }
                owner = parent; token = cancellation;
                owner.FormClosed += OwnerClosed; owner.Disposed += OwnerDisposed;
                try
                {
                    window.Show(owner);
                    registration = token.Register(delegate
                    {
                        try { if (window.IsHandleCreated && !window.IsDisposed) window.BeginInvoke(new Action(delegate { Finish(false, default(T)); })); }
                        catch (InvalidOperationException) { result.TrySetCanceled(); }
                    });
                    if (finished) registration.Dispose();
                    else if (token.IsCancellationRequested) Finish(false, default(T));
                }
                catch (Exception error)
                {
                    Finish(false, default(T));
                    // Do not hide a programming or window-creation failure behind a pending task.
                    return Failed(error);
                }
                return result.Task;
            }

            private static Task<T> Failed(Exception error)
            {
                TaskCompletionSource<T> failure = new TaskCompletionSource<T>(); failure.SetException(error); return failure.Task;
            }

            internal void Choose(T value) { Finish(!token.IsCancellationRequested, value); }
            private void OwnerClosed(object sender, FormClosedEventArgs args) { Finish(false, default(T)); }
            private void OwnerDisposed(object sender, EventArgs args) { Finish(false, default(T)); }

            private void Finish(bool chosen, T value)
            {
                if (finished) return;
                finished = true;
                if (owner != null) { owner.FormClosed -= OwnerClosed; owner.Disposed -= OwnerDisposed; }
                registration.Dispose();
                if (!window.IsDisposed) { window.Close(); window.Dispose(); }
                // Close and detach before resuming the caller, whose continuation may open another choice.
                if (chosen) result.TrySetResult(value); else result.TrySetCanceled();
            }
        }
    }
}
