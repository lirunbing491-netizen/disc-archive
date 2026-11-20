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
        private readonly Label localImported = LabelOf("本机已导 0 张",10);
        private readonly Button saveTitle = ButtonOf("保存标签"), openFiles = ButtonOf("打开归档"), retry = ButtonOf("核对 / 续导原盘");
        private readonly Button removeRecord = ButtonOf("从本机移除…"), restoreRecord = ButtonOf("恢复到登记册"), recordEvidence=ButtonOf("归类依据"), recordReceipts=ButtonOf("本盘记录");
        private readonly CheckBox showHidden = new CheckBox { Text="查看已移除", AutoSize=true, Anchor=AnchorStyles.Left };
        private string lastSearch="";
        private string editorId;
        private bool editing, filling;

        private void BuildLibrary(TabPage tab)
        {
            TableLayoutPanel l = Table(-100); Row(l,44); Row(l,-100); Row(l,125); Row(l,84); tab.Controls.Add(l);
            TableLayoutPanel tools = Table(170,60,-100,115,160); Row(tools,-100);
            search.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            search.AccessibleName="查找登记";
            Button export = ButtonOf("导出本机表…"), sharedExport=ButtonOf("导出归档盘总表…");
            tools.Controls.Add(localImported,0,0); tools.Controls.Add(LabelOf("查找",10),1,0); tools.Controls.Add(search,2,0); tools.Controls.Add(export,3,0);tools.Controls.Add(sharedExport,4,0);
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
            Button add=ButtonOf("补录本机登记…");
            add.Click+=delegate { UserAction(AddManualRecord); };
            showHidden.CheckedChanged+=delegate { if(muteSelection)return; if(!ResolveLibraryEdits()) { muteSelection=true; showHidden.Checked=!showHidden.Checked; muteSelection=false; return; } FillGrid(); };
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (string h in new string[]{"项目 / 标题","人工标题","导盘状态","目标硬盘"})
            { int i = grid.Columns.Add("c"+grid.Columns.Count,h); grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; }
            grid.Columns[0].FillWeight=160; grid.Columns[1].FillWeight=110; grid.Columns[2].FillWeight=80; grid.Columns[3].FillWeight=100;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(33,104,96);
            l.Controls.Add(grid,0,1);
            TableLayoutPanel fields = Table(120,80,-100); Row(fields,36); Row(fields,36); Row(fields,-100);
            fields.Controls.Add(recordPhoto,0,0); fields.SetRowSpan(recordPhoto,3);
            fields.Controls.Add(LabelOf("标题标签",10),1,0); fields.Controls.Add(LabelOf("项目标签",10),1,1);
            editTitle.MaxLength=500; editProject.MaxLength=240;
            editTitle.Anchor=editProject.Anchor=AnchorStyles.Left|AnchorStyles.Right;
            fields.Controls.Add(editTitle,2,0); fields.Controls.Add(editProject,2,1); fields.Controls.Add(note,1,2); fields.SetColumnSpan(note,2);
            Button receipts=recordReceipts;
            Button why=recordEvidence;
            why.Click+=delegate {
                Entry e=Selected(); if(e==null) return;
                string evidence="卷标　"+(String.IsNullOrWhiteSpace(e.DiscLabel)?"未记录或为空":e.DiscLabel)+
                    "\r\n盘面候选　"+(String.IsNullOrWhiteSpace(e.PhotoText)?"无":e.PhotoText)+
                    "\r\n读取格式　"+(e.SourceFormat??"旧记录未保存")+(String.IsNullOrEmpty(e.CloseWarning)?"":"\r\n"+e.CloseWarning)+"\r\n识图方式及结果　"+(e.VisionNote??"旧记录未保存")+
                    "\r\n\r\n命名依据\r\n"+(e.NamingReason??"旧记录未保存")+
                    "\r\n\r\n实际项目目录\r\n"+String.Join("\r\n",e.ArchiveFolders??new string[0])+
                    "\r\n原盘号　"+(e.CanonicalId??e.Id)+"\r\n已核对沿用　"+e.ReusedFiles+" 个\r\n识盘依据　"+(e.IdentityEvidence??"旧记录未保存");
                Notice(evidence);
            };
            receipts.Click+=delegate { UserAction(delegate {
                Entry e=Selected(); if(e==null) return;
                if(!station.Book.IsOriginalTarget(e)) throw new IOException("请先接回登记时的目标硬盘。");
                string dir=Directory.Exists(e.Report) ? e.Report : e.JobDir;
                OpenDirectory(dir);
            }); };
            l.Controls.Add(fields,0,2);
            TableLayoutPanel actions=Table(-100); Row(actions,42); Row(actions,42);
            actions.Controls.Add(Flow(saveTitle,add,removeRecord,restoreRecord,showHidden),0,0);
            actions.Controls.Add(Flow(openFiles,why,receipts,retry),0,1); l.Controls.Add(actions,0,3);
            removeRecord.Click+=delegate { UserAction(delegate { SetRecordHidden(true); }); };
            restoreRecord.Click+=delegate { UserAction(delegate { SetRecordHidden(false); }); };
            grid.SelectionChanged += delegate {
                if(muteSelection)return;
                Entry next=Selected(); string nextId=next==null ? null : next.Id;
                if(editing && nextId!=editorId && !ResolveLibraryEdits()) { SelectLibraryRecord(editorId); return; }
                if(nextId!=null)SelectLibraryRecord(nextId); else ShowRecord();
            };
            search.TextChanged += delegate {
                if(muteSelection)return;
                if(!ResolveLibraryEdits()) { muteSelection=true; search.Text=lastSearch; muteSelection=false; return; }
                lastSearch=search.Text; FillGrid();
            };
            editTitle.TextChanged += delegate { if (!filling) editing=true; };
            editProject.TextChanged += delegate { if (!filling) editing=true; };
            saveTitle.Click += delegate { UserAction(SaveEdit); };
            openFiles.Click += delegate { UserAction(delegate {
                Entry e = Selected(); if(e==null) return;
                if(e.ManualOnly) return;
                if(!station.Book.IsOriginalTarget(e)||!Directory.Exists(e.JobDir)) throw new IOException("请连接原归档硬盘并使用登记时的盘符："+e.TargetRoot);
                if(LiveEntry(e.Id)==null) book.SyncLabels(e);
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
                Entry e=Selected(); if (e==null || e.ManualOnly || e.HiddenFromCatalog || Active || LiveEntry(e.Id)!=null) return;
                DriveSlot slot=selectedSlot;
                if (!Confirm("请准备照片中同一张原盘。使用 "+slot.Drive+" 核对已归档文件并只补缺失或损坏项？旧版无摘要记录需额外读取原盘。")) return;
                if(slot.Worker.Current!=null || LiveEntry(e.Id)!=null) throw new IOException("这张盘或指定光驱已有任务。");
                if(book.IsOriginalTarget(e)) slot.Worker.Retry(e,slot.Drive);
                else {
                    if(!book.IsOriginalTarget(new Entry{TargetRoot=target.Text,TargetId=e.TargetId}))
                        throw new IOException("请先在导盘页选择这份登记原来的备份硬盘或共享目录。");
                    slot.Worker.ResumeKnown(target.Text,e.Id,slot.Drive);
                }
                tabs.SelectedIndex=0; ArmSlot(slot,slot.Worker.Current);
            }); };
        }
        private Entry Selected() { return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as Entry; }
        private void ResumeNumber()
        {
            DriveSlot slot=selectedSlot;
            if(!slot.Connected || slot.Worker.Current!=null || !Directory.Exists(target.Text))
                throw new IOException("请先选择原备份位置和一台空闲光驱。");
            using(var d=new Form{Text="用备份盘上的旧盘号续导",Font=Font,ClientSize=new Size(585,148),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false})
            {
                var l=Table(-100);l.Padding=new Padding(10);Row(l,45);Row(l,34);Row(l,-100);
                l.Controls.Add(LabelOf("输入旧盘号（DA- 开头），确认准备放入 "+slot.Drive+" 的是该盘。",10),0,0);
                var id=new TextBox{Dock=DockStyle.Fill}; l.Controls.Add(id,0,1);
                var ok=ButtonOf("核对并补缺");ok.DialogResult=DialogResult.OK;
                var no=ButtonOf("取消");no.DialogResult=DialogResult.Cancel;
                l.Controls.Add(Flow(ok,no),0,2);d.Controls.Add(l);d.AcceptButton=ok;d.CancelButton=no;
                if(d.ShowDialog(this)!=DialogResult.OK || slot.Worker.Current!=null)return;
                var e=slot.Worker.ResumeKnown(target.Text,id.Text.Trim().ToUpperInvariant(),slot.Drive);
                tabs.SelectedIndex=0; ArmSlot(slot,e);
            }
        }
        private void Reload() { entries = station.Book.List(); FillGrid(); }
        private void FillGrid()
        {
            localImported.Text="本机已导 "+Catalog.LocalImportedCount(entries)+" 张";
            // Background task completions may refresh the catalog while labels are being typed.
            if(editing) return;
            string keep=editorId;
            muteSelection=true;
            try
            {
                grid.Rows.Clear();
                string term=search.Text.Trim();
                List<DataGridViewRow> rows = new List<DataGridViewRow>();
                int keepIndex=-1;
                if(entries!=null) foreach(Entry e in entries.OrderByDescending(x=>x.Created??"",StringComparer.Ordinal)
                    .ThenByDescending(x=>CatalogSequence(x.Id)).ThenByDescending(x=>x.Id??"",StringComparer.Ordinal))
                {
                    if(e.HiddenFromCatalog!=showHidden.Checked)continue;
                    string title=e.Title==e.Id ? "" : e.Title;
                    string primary=String.IsNullOrWhiteSpace(e.Project) ? title : e.Project;
                    string alternate=String.Equals(title,primary,StringComparison.OrdinalIgnoreCase) ? "" : title;
                    string[] cells = {primary, alternate, e.ManualOnly ? "手工补录" : e.Status, e.ManualOnly ? "仅本机登记" : e.TargetName};
                    if(term.Length>0 && !cells.Concat(new[]{e.Id,e.TitleState}).Any(s => s!=null && s.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0)) continue;
                    DataGridViewRow row = new DataGridViewRow();
                    row.CreateCells(grid, cells); row.Tag=e;
                    if(e.Id==keep) keepIndex=rows.Count;
                    rows.Add(row);
                }
                if(rows.Count>0) grid.Rows.AddRange(rows.ToArray());
                if(rows.Count>0) grid.CurrentCell=grid.Rows[keepIndex>=0 ? keepIndex : 0].Cells[0];
            }
            finally { muteSelection=false; }
            ShowRecord();
        }
        private void ShowRecord()
        {
            Entry e=Selected();
            bool same=e!=null && e.Id==editorId;
            bool canEdit=e!=null && !e.HiddenFromCatalog;
            editTitle.Enabled=editProject.Enabled=saveTitle.Enabled=canEdit;
            bool archived=e!=null && !e.ManualOnly && !e.HiddenFromCatalog;
            openFiles.Enabled=recordEvidence.Enabled=recordReceipts.Enabled=archived;
            retry.Enabled=archived && selectedSlot.Connected && !Active && LiveEntry(e.Id)==null;
            removeRecord.Visible=!showHidden.Checked; restoreRecord.Visible=showHidden.Checked;
            removeRecord.Enabled=canEdit && LiveEntry(e.Id)==null;
            restoreRecord.Enabled=e!=null && e.HiddenFromCatalog;
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
            note.Text=e==null ? "可直接补录本机登记；编辑标签不会重命名磁盘目录。" :
                "盘号："+e.Id+"   ·   "+(e.ManualOnly ? "仅本机补录，无归档文件" : e.TargetRoot)+"\n"+
                (e.HiddenFromCatalog ? "已从本机列表移除；可恢复。照片、素材和硬盘回执均保留。" : "修改标题、项目仅更新标签，不重命名磁盘目录。")+
                (String.IsNullOrWhiteSpace(e.Created) ? "" : "  ·  "+e.Created);
        }
        private static long CatalogSequence(string id)
        {
            string[] parts=(id??"").Split('-'); long sequence;
            return parts.Length>1 && Int64.TryParse(parts[1],out sequence) ? sequence : 0;
        }
        private static void OpenDirectory(string dir)
        {
            if(!Directory.Exists(dir)) throw new DirectoryNotFoundException("目录暂时不可用："+dir);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+dir+"\""){UseShellExecute=false});
        }
        private void SaveEdit()
        {
            if(editorId==null) return;
            Entry e = LiveEntry(editorId) ?? book.List().Find(x=>x.Id==editorId);
            if(e==null || e.HiddenFromCatalog)return;
            string title=editTitle.Text.Trim();
            bool online=!e.ManualOnly && station.Book.IsOriginalTarget(e);
            station.Book.Edit(e,title,editProject.Text.Trim());
            editing=false; Reload();
            note.Text="盘号："+e.Id+"\n"+(e.ManualOnly ? "标签已保存在本机。此项为手工补录，无归档文件，不重命名磁盘目录。" : online ? "标签已保存到登记册和归档硬盘，磁盘目录名称未改变。" : "标签已保存在本机，磁盘目录名称未改变。接回原硬盘后点“打开归档”可同步。");
        }
        private bool ResolveLibraryEdits()
        {
            if(!editing)return true;
            DialogResult answer=MessageBox.Show(this,"当前标签尚未保存。是否先保存？\r\n选“否”放弃本次修改，选“取消”继续编辑。","保存标签",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
            if(answer==DialogResult.Cancel)return false;
            if(answer==DialogResult.Yes) {
                try { SaveEdit(); } catch(Exception ex) { Notice(ex.Message); return false; }
            } else editing=false;
            return true;
        }
        private void SelectLibraryRecord(string id)
        {
            muteSelection=true;
            try { foreach(DataGridViewRow row in grid.Rows) { Entry e=row.Tag as Entry; if(e!=null && e.Id==id) { grid.CurrentCell=row.Cells[0]; break; } } }
            finally { muteSelection=false; }
            ShowRecord();
        }
        private void SetRecordHidden(bool hidden)
        {
            Entry e=Selected(); if(e==null)return;
            string id=e.Id;
            if(LiveEntry(id)!=null)throw new IOException("这张盘仍在处理中，请先完成或停止任务。");
            if(!ResolveLibraryEdits())return;
            if(hidden && !Confirm("从本机登记册移除这条记录？\r\n只隐藏本机条目，照片、归档素材及硬盘回执均保留。\r\n以后勾选“查看已移除”，可恢复。"))return;
            if(LiveEntry(id)!=null)throw new IOException("这张盘仍在处理中，请先完成或停止任务。");
            book.SetHidden(id,hidden); editing=false; editorId=null; Reload();
        }
        private void AddManualRecord()
        {
            if(!ResolveLibraryEdits())return;
            using(var dialog=new Form { Text="补录本机登记", Font=Font, ClientSize=new Size(540,220), FormBorderStyle=FormBorderStyle.FixedDialog, StartPosition=FormStartPosition.CenterParent, MinimizeBox=false, MaximizeBox=false }) {
                var layout=Table(80,-100); layout.Padding=new Padding(12); Row(layout,44); Row(layout,40); Row(layout,40); Row(layout,54);
                var title=new TextBox { Dock=DockStyle.Fill, MaxLength=500 }; var projectName=new TextBox { Dock=DockStyle.Fill, MaxLength=240 };
                var hint=LabelOf("仅登记本机标签，不需要照片、光驱或归档硬盘。",10); layout.Controls.Add(hint,0,0); layout.SetColumnSpan(hint,2);
                layout.Controls.Add(LabelOf("标题",10),0,1); layout.Controls.Add(title,1,1);
                layout.Controls.Add(LabelOf("项目",10),0,2); layout.Controls.Add(projectName,1,2);
                var ok=ButtonOf("保存补录"); var cancel=ButtonOf("取消"); cancel.DialogResult=DialogResult.Cancel;
                ok.Click+=delegate { if(String.IsNullOrWhiteSpace(title.Text) && String.IsNullOrWhiteSpace(projectName.Text)) { MessageBox.Show(dialog,"请填写标题或项目。"); return; } dialog.DialogResult=DialogResult.OK; };
                layout.Controls.Add(Flow(ok,cancel),1,3); dialog.Controls.Add(layout); dialog.AcceptButton=ok; dialog.CancelButton=cancel;
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                Entry e=book.AddManual(title.Text.Trim(),projectName.Text.Trim());
                muteSelection=true; showHidden.Checked=false; search.Text=""; lastSearch=""; muteSelection=false;
                editorId=e.Id; Reload();
            }
        }
    }
}
