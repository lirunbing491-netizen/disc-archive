using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiscArchive
{
    internal sealed partial class MainForm
    {
        private readonly TextBox search = new TextBox(), editTitle = new TextBox(), editProject = new TextBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly PictureBox recordPhoto = Picture();
        private readonly Label note = LabelOf("",9);
        private readonly Button saveTitle = ButtonOf("保存标题和项目"), openFiles = ButtonOf("打开归档"), retry = ButtonOf("用原盘号重读");
        private string editorId;
        private bool editing, filling;

        private void BuildLibrary(TabPage tab)
        {
            TableLayoutPanel l = Table(-100); Row(l,44); Row(l,-100); Row(l,165); Row(l,46); tab.Controls.Add(l);
            TableLayoutPanel tools = Table(70,-100,115,160); Row(tools,-100);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            Button export = ButtonOf("导出本机表…"), sharedExport=ButtonOf("导出归档盘总表…");
            tools.Controls.Add(LabelOf("查找",10),0,0); tools.Controls.Add(search,1,0); tools.Controls.Add(export,2,0);tools.Controls.Add(sharedExport,3,0);
            sharedExport.Click+=async delegate {
                string root=ArchiveFolder("选择各电脑共同使用的归档根目录",target.Text); if(root==null)return;
                using(var dlg=new SaveFileDialog{Title="导出此位置所有工位的登记",Filter="CSV 总表|*.csv",FileName="归档盘总表.csv"}) {
                    if(dlg.ShowDialog(this)!=DialogResult.OK) return;
                    string dest=dlg.FileName; sharedExport.Enabled=false;
                    try {
                        int count=await System.Threading.Tasks.Task.Run(delegate { return book.ExportTarget(root,dest); });
                        if(!IsDisposed) Notice("已导出这个归档位置的 "+count+" 条登记。");
                    }catch(Exception ex){if(!IsDisposed) Notice(ex.Message);}
                    finally {if(!IsDisposed) sharedExport.Enabled=true;}
                }
            };
            l.Controls.Add(tools,0,0);
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (string h in new string[]{"盘号","标题","标题来源","项目","导盘状态","目标硬盘","光驱"})
            { int i = grid.Columns.Add("c"+grid.Columns.Count,h); grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; }
            grid.Columns[0].FillWeight=125; grid.Columns[1].FillWeight=175; grid.Columns[2].FillWeight=66;
            grid.Columns[3].FillWeight=90; grid.Columns[4].FillWeight=82; grid.Columns[5].FillWeight=100;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(33,104,96);
            l.Controls.Add(grid,0,1);
            TableLayoutPanel fields = Table(135,67,-100); Row(fields,42); Row(fields,42); Row(fields,-100);
            fields.Controls.Add(recordPhoto,0,0); fields.SetRowSpan(recordPhoto,3);
            fields.Controls.Add(LabelOf("标题",10),1,0); fields.Controls.Add(LabelOf("项目",10),1,1);
            editTitle.MaxLength=500; editProject.MaxLength=240;
            editTitle.Anchor=editProject.Anchor=AnchorStyles.Left|AnchorStyles.Right;
            fields.Controls.Add(editTitle,2,0); fields.Controls.Add(editProject,2,1); fields.Controls.Add(note,1,2); fields.SetColumnSpan(note,2);
            Button receipts=ButtonOf("本盘记录");
            Button why=ButtonOf("归类依据");
            why.Click+=delegate {
                Entry e=Selected(); if(e==null) return;
                string evidence="卷标　"+(String.IsNullOrWhiteSpace(e.DiscLabel)?"未记录或为空":e.DiscLabel)+
                    "\r\n盘面候选　"+(String.IsNullOrWhiteSpace(e.PhotoText)?"无":e.PhotoText)+
                    "\r\n识图方式及结果　"+(e.VisionNote??"旧记录未保存")+
                    "\r\n\r\n命名依据\r\n"+(e.NamingReason??"旧记录未保存")+
                    "\r\n\r\n实际项目目录\r\n"+String.Join("\r\n",e.ArchiveFolders??new string[0]);
                Notice(evidence);
            };
            receipts.Click+=delegate { UserAction(delegate {
                Entry e=Selected(); if(e==null) return;
                if(!station.Book.IsOriginalTarget(e)) throw new IOException("请先接回登记时的目标硬盘。");
                string dir=Directory.Exists(e.Report) ? e.Report : e.JobDir;
                OpenDirectory(dir);
            }); };
            l.Controls.Add(fields,0,2); l.Controls.Add(Flow(saveTitle,openFiles,why,receipts,retry),0,3);
            grid.SelectionChanged += delegate { if (!muteSelection) ShowRecord(); };
            search.TextChanged += delegate { FillGrid(); };
            editTitle.TextChanged += delegate { if (!filling) editing=true; };
            editProject.TextChanged += delegate { if (!filling) editing=true; };
            saveTitle.Click += delegate { UserAction(SaveEdit); };
            openFiles.Click += delegate { UserAction(delegate {
                Entry e = Selected(); if(e==null) return;
                if(!station.Book.IsOriginalTarget(e)||!Directory.Exists(e.JobDir)) throw new IOException("请连接原归档硬盘并使用登记时的盘符："+e.TargetRoot);
                if(LiveEntry(e.Id)==null) book.Save(e);
                if(e.ArchiveFolders==null || e.ArchiveFolders.Length==0) OpenDirectory(e.JobDir);
                else if(e.ArchiveFolders.Length==1) OpenDirectory(ProjectPhoto.DirectoryFor(e.TargetRoot,e.ArchiveFolders[0]));
                else {
                    ContextMenuStrip menu=new ContextMenuStrip();
                    foreach(string name in e.ArchiveFolders) {
                        string folder=ProjectPhoto.DirectoryFor(e.TargetRoot,name);
                        menu.Items.Add(name,null,delegate { UserAction(delegate { OpenDirectory(folder); }); });
                    }
                    menu.Closed+=delegate { menu.Dispose(); }; menu.Show(openFiles,0,openFiles.Height);
                }
            }); };
            export.Click += delegate { using (SaveFileDialog dlg=new SaveFileDialog {Title="导出所有光盘登记",Filter="CSV 总表|*.csv",FileName="光盘登记册.csv"})
                if(dlg.ShowDialog(this)==DialogResult.OK) UserAction(delegate { station.Book.Export(dlg.FileName); Notice("总表已导出。"); }); };
            retry.Click += delegate { UserAction(delegate {
                Entry e=Selected(); if (e==null || Active || LiveEntry(e.Id)!=null) return;
                DriveSlot slot=selectedSlot;
                if (!Confirm("请准备照片中这张原盘。确认使用 "+slot.Drive+" 和原盘号 "+e.Id+" 重新读取？")) return;
                if(slot.Worker.Current!=null || LiveEntry(e.Id)!=null) throw new IOException("这张盘或指定光驱已有任务。");
                slot.Worker.Retry(e,slot.Drive);
                tabs.SelectedIndex=0; ArmSlot(slot,e);
            }); };
        }
        private Entry Selected() { return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as Entry; }
        private void Reload() { entries = station.Book.List(); FillGrid(); }
        private void FillGrid()
        {
            string keep=editorId;
            muteSelection=true;
            try
            {
                grid.Rows.Clear();
                string term=search.Text.Trim();
                List<DataGridViewRow> rows = new List<DataGridViewRow>();
                int keepIndex=-1;
                if(entries!=null) for(int i=entries.Count-1;i>=0;i--)
                {
                    Entry e=entries[i];
                    string[] cells = {e.Id, e.Title==e.Id ? "" : e.Title, e.TitleState ?? "待确认", e.Project, e.Status, e.TargetName,e.Drive};
                    if(term.Length>0 && !cells.Any(s => s!=null && s.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0)) continue;
                    DataGridViewRow row = new DataGridViewRow();
                    row.CreateCells(grid, cells); row.Tag=e;
                    if(e.Id==keep) keepIndex=rows.Count;
                    rows.Add(row);
                }
                if(rows.Count>0) grid.Rows.AddRange(rows.ToArray());
                if(keepIndex>=0) grid.CurrentCell=grid.Rows[keepIndex].Cells[0];
            }
            finally { muteSelection=false; }
            ShowRecord();
        }
        private void ShowRecord()
        {
            Entry e=Selected();
            bool same=e!=null && e.Id==editorId;
            bool canEdit=e!=null; editTitle.Enabled=editProject.Enabled=saveTitle.Enabled=openFiles.Enabled=canEdit;
            retry.Enabled=canEdit && selectedSlot.Connected && !Active && LiveEntry(e.Id)==null && e.Status!=Workstation.Done && e.Status!="已校验";
            if(!same || !editing)
            {
                filling=true;
                editTitle.Text=e==null || e.Title==e.Id ? "" : e.Title;
                editProject.Text=e==null ? "" : e.Project;
                filling=false; editing=false;
            }
            if(!same)
            {
                try { SetPhoto(recordPhoto,e==null ? null : Thumbnail(e.Photo)); }
                catch { SetPhoto(recordPhoto,null); }
            }
            editorId=e==null ? null : e.Id;
            note.Text=e==null ? "登记后可在这里查看盘面、修订标题和查找硬盘。" : e.TargetRoot+"   ·   "+e.Created+"\n"+(String.IsNullOrEmpty(e.Note) ? e.NamingReason : e.Note);
        }
        private static void OpenDirectory(string dir)
        {
            if(!Directory.Exists(dir)) throw new DirectoryNotFoundException("目录暂时不可用："+dir);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+dir+"\""){UseShellExecute=false});
        }
        private void SaveEdit()
        {
            Entry selected=Selected(); if(selected==null) return;
            Entry e = LiveEntry(selected.Id) ?? book.List().Find(x=>x.Id==selected.Id);
            string title=editTitle.Text.Trim();
            bool online=station.Book.IsOriginalTarget(e);
            station.Book.Edit(e,title,editProject.Text.Trim());
            editing=false; Reload();
            note.Text=online ? "修改已保存到登记册和归档硬盘。" : "修改已保存在本机。接回原硬盘后点“打开归档”可同步。";
        }
    }
}
