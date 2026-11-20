using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DiscArchive;

internal static class CatalogUiTests
{
    private sealed class NoDrive : IReader {
        public string[] Drives() { return new string[0]; }
        public bool Empty(string root) { return true; }
        public bool Ready(string root) { return false; }
        public MountedDisc Open(string root) { throw new InvalidOperationException(); }
    }
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Get<T>(MainForm f,string name) { return (T)typeof(MainForm).GetField(name,Flags).GetValue(f); }
    private static void Call(MainForm f,string name) { typeof(MainForm).GetMethod(name,Flags).Invoke(f,null); }
    private static void Check(bool value,string text) { if(!value)throw new Exception(text); Console.WriteLine("PASS "+text); }
    private static Control Find(Control root,string text) {
        foreach(Control child in root.Controls) { if(child.Text==text)return child; Control found=Find(child,text); if(found!=null)return found; }
        return null;
    }
    private delegate bool WindowVisitor(IntPtr handle,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,WindowVisitor visitor,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,WindowVisitor visitor,IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static string WindowText(IntPtr window) { var text=new StringBuilder(4096); GetWindowText(window,text,text.Capacity); return text.ToString(); }
    private static void CheckImportCounter(MainForm f) {
        var rows=new System.Collections.Generic.List<Entry> {
            new Entry {Id="DA-1",Status="已拷出",Files=2}, // legacy, no CanonicalId
            new Entry {Id="DA-2",CanonicalId="DA-1",Status="部分拷出",Files=3,ReusedFiles=2},
            new Entry {Id="DA-3",Status="暂停待续",Files=1,HiddenFromCatalog=true},
            new Entry {Id="DA-4",Status="已拷出",Files=0}, // completed empty disc
            new Entry {Id="DA-5",Status="已拷出",Files=2,ReusedFiles=2},
            new Entry {Id="DA-6",Status="待复查",Files=0,Failed=1},
            new Entry {Id="DA-7",Status="等待放盘"},
            new Entry {Id="DA-8",ManualOnly=true,ImportedLocally=true,Files=3},
            new Entry {Id="DA-9",Status="已拷出",Files=4,LatestRunId="REMOTE-RUN"},
            new Entry {Id="DA-10",Status="正在归类",ImportedLocally=true},
            new Entry {Id="DA-11",Status="已拷出",Files=5,LatestRunId="REMOTE-RUN",ImportedLocally=true}
        };
        Check(Catalog.LocalImportedCount(rows)==5,"counter deduplicates originals, includes partial paused empty and saved history, excludes manual reuse-only failures and remote summaries");
        var large=new System.Collections.Generic.List<Entry>();
        for(int i=0;i<20000;i++)large.Add(new Entry {Id="DA-"+i,CanonicalId="ORIGINAL-"+(i/2),Files=1});
        Check(Catalog.LocalImportedCount(large)==10000,"large import history deduplicates twenty thousand attempts");
        typeof(MainForm).GetField("entries",Flags).SetValue(f,rows); Call(f,"FillGrid");
        Label counter=Get<Label>(f,"localImported");
        Check(counter.Text=="本机已导 5 张","catalog shows cumulative local import count");
        Get<TextBox>(f,"search").Text="no matching record";
        Check(Get<DataGridView>(f,"grid").Rows.Count==0 && counter.Text=="本机已导 5 张","search does not change cumulative count");
        Get<CheckBox>(f,"showHidden").Checked=true;
        Check(counter.Text=="本机已导 5 张","removed view does not change cumulative count");
        Get<CheckBox>(f,"showHidden").Checked=false; Get<TextBox>(f,"search").Text="";
        Get<TextBox>(f,"editTitle").Text="counter refresh draft";
        rows.Add(new Entry {Id="DA-12",Files=1}); Call(f,"FillGrid");
        Check(counter.Text=="本机已导 6 张" && Get<TextBox>(f,"editTitle").Text=="counter refresh draft","background completion refreshes count while preserving unsaved draft");
        typeof(MainForm).GetField("editing",Flags).SetValue(f,false);
        Check(counter.Visible && counter.Height>=counter.Font.Height && counter.Bottom<=counter.Parent.ClientSize.Height,"minimum window displays count within toolbar");
        Control searchLabel=Find(f,"查找");
        Check(TextRenderer.MeasureText(searchLabel.Text,searchLabel.Font).Width<=searchLabel.Width,"minimum window displays complete search label");
        Check(TextRenderer.MeasureText("本机已导 10000 张",counter.Font).Width<=counter.Width,"minimum window fits five digit cumulative count");
    }
    private static void CheckActualButtons(string root) {
        string state=Path.Combine(root,"actual-buttons"); Catalog book=new Catalog(state);
        using(MainForm f=new MainForm(state,new NoDrive())) {
            f.ShowInTaskbar=false; f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-10000,-10000); f.Show();
            Get<TabControl>(f,"tabs").SelectedIndex=1; Application.DoEvents();
            bool entered=false; Exception inputError=null;
            using(var input=new Timer { Interval=40 }) {
                input.Tick+=delegate {
                    Form dialog=null;
                    foreach(Form candidate in Application.OpenForms) if(candidate.Owner==f && candidate.Text=="补录本机登记") { dialog=candidate; break; }
                    if(dialog==null)return;
                    input.Stop();
                    try {
                        TableLayoutPanel layout=(TableLayoutPanel)dialog.Controls[0];
                        ((TextBox)layout.GetControlFromPosition(1,1)).Text="按钮补录标题";
                        ((TextBox)layout.GetControlFromPosition(1,2)).Text="按钮补录项目";
                        entered=true; ((Button)Find(dialog,"保存补录")).PerformClick();
                    } catch(Exception ex) { inputError=ex; dialog.DialogResult=DialogResult.Cancel; }
                };
                input.Start(); ((Button)Find(f,"补录本机登记…")).PerformClick(); input.Stop();
            }
            if(inputError!=null)throw inputError;
            DataGridView grid=Get<DataGridView>(f,"grid");
            Check(entered && grid.Rows.Count==1,"actual add button opens modal and confirmation creates row without hardware");
            Entry entry=(Entry)grid.Rows[0].Tag;
            Check(entry.ManualOnly && entry.Title=="按钮补录标题" && entry.Project=="按钮补录项目" && book.List().Count==1,"actual add modal persists entered title and project");
            bool confirmed=false; string confirmationText="";
            uint thread=GetCurrentThreadId(), process=(uint)Process.GetCurrentProcess().Id;
            using(var confirm=new Timer { Interval=40 }) {
                confirm.Tick+=delegate {
                    EnumThreadWindows(thread,delegate(IntPtr window,IntPtr unused) {
                        uint ownerProcess; GetWindowThreadProcessId(window,out ownerProcess);
                        // Only this synthetic form's own dialog, in this test process and UI thread.
                        if(ownerProcess!=process || GetWindow(window,4)!=f.Handle || WindowText(window)!="请核对")return true;
                        confirm.Stop();
                        EnumChildWindows(window,delegate(IntPtr child,IntPtr ignored) { confirmationText+=WindowText(child); return true; },IntPtr.Zero);
                        confirmed=true; SendMessage(window,0x0111,new IntPtr(6),IntPtr.Zero); return false;
                    },IntPtr.Zero);
                };
                confirm.Start(); Get<Button>(f,"removeRecord").PerformClick(); confirm.Stop();
            }
            Check(confirmed && confirmationText.Contains("照片") && confirmationText.Contains("素材") && confirmationText.Contains("硬盘回执"),"actual removal confirmation explains retained data and is answered only within synthetic form");
            Check(grid.Rows.Count==0 && book.List().Find(x=>x.Id==entry.Id).HiddenFromCatalog,"actual remove button hides local row");
            Get<CheckBox>(f,"showHidden").Checked=true;
            Check(grid.Rows.Count==1 && ((Entry)grid.Rows[0].Tag).Id==entry.Id,"removed switch reveals actual removed row");
            Get<Button>(f,"restoreRecord").PerformClick();
            Check(grid.Rows.Count==0 && !book.List().Find(x=>x.Id==entry.Id).HiddenFromCatalog,"actual restore button restores local registration");
            Get<CheckBox>(f,"showHidden").Checked=false;
            Check(grid.Rows.Count==1 && ((Entry)grid.Rows[0].Tag).Title=="按钮补录标题" && ((Entry)grid.Rows[0].Tag).Project=="按钮补录项目","restored normal row retains entered title and project");
            f.Close();
        }
    }
    [STAThread] public static int Main() {
        try {
            Application.EnableVisualStyles();
            string root=Path.Combine(Path.GetTempPath(),"DiscArchive-CatalogUi-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Catalog book=new Catalog(root); Entry a=book.AddManual("合成项目","合成项目"), b=book.AddManual("人工补充标题","另一项目");
            using(MainForm f=new MainForm(root,new NoDrive())) {
                f.ShowInTaskbar=false; f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-10000,-10000); f.Size=f.MinimumSize; f.Show();
                Get<TabControl>(f,"tabs").SelectedIndex=1; Application.DoEvents();
                DataGridView grid=Get<DataGridView>(f,"grid");
                Check(grid.Height>=80,"minimum window retains usable catalog row area");
                Check(grid.Columns.Count==4,"catalog omits historical drive and primary disc number columns");
                foreach(DataGridViewRow row in grid.Rows) {
                    Entry e=(Entry)row.Tag;
                    if(e.Id==a.Id) Check((string)row.Cells[0].Value=="合成项目" && (string)row.Cells[1].Value=="","same project and title appear once");
                    if(e.Id==b.Id) Check((string)row.Cells[1].Value=="人工补充标题","different manual title remains visible");
                }
                Get<TextBox>(f,"search").Text=a.Id; Check(grid.Rows.Count==1,"secondary disc number remains searchable");
                Check(!Get<Button>(f,"openFiles").Enabled && !Get<Button>(f,"retry").Enabled && !Get<Button>(f,"recordReceipts").Enabled,"manual entry cannot open archive, retry, or open receipts");
                Check(Get<Label>(f,"note").Text.Contains(a.Id) && Get<Label>(f,"note").Text.Contains("不重命名"),"details show disc number and label-only edit meaning");
                Get<TextBox>(f,"editTitle").Text="未保存草稿"; Call(f,"Reload");
                Check(Get<TextBox>(f,"editTitle").Text=="未保存草稿" && grid.Rows.Count==1,"background refresh preserves draft and selection");
                Call(f,"SaveEdit"); Check(book.List().Find(x=>x.Id==a.Id).Title=="未保存草稿","saving writes manual labels without connected devices");
                book.SetHidden(a.Id,true); Call(f,"Reload"); Check(grid.Rows.Count==0,"normal catalog excludes hidden records");
                Get<CheckBox>(f,"showHidden").Checked=true; Check(grid.Rows.Count==1 && Get<Button>(f,"restoreRecord").Enabled,"removed view exposes restore action");
                Call(f,"SetRecordHidden",false);
                Check(grid.Rows.Count==0 && !book.List().Find(x=>x.Id==a.Id).HiddenFromCatalog,"restoring removes entry from removed view");
                Get<CheckBox>(f,"showHidden").Checked=false; Check(grid.Rows.Count==1,"restored record returns to normal catalog");
                using(Bitmap image=new Bitmap(f.Width,f.Height)) { f.DrawToBitmap(image,new Rectangle(0,0,f.Width,f.Height)); image.Save(Path.Combine(root,"minimum-window.png")); }
                foreach(string name in new[]{"saveTitle","removeRecord","openFiles","recordEvidence","recordReceipts","retry"}) {
                    Control button=Get<Control>(f,name); Check(button.Right<=button.Parent.ClientSize.Width,"minimum window fits "+name);
                }
                Control removedView=Get<CheckBox>(f,"showHidden"); Check(removedView.Right<=removedView.Parent.ClientSize.Width,"minimum window fits removed view switch");
                Get<TextBox>(f,"search").Text="";
                a.Created=b.Created="2026-01-01 12:00:00";
                Entry newerDisc=new Entry { Id="DA-000003-SYNTHETIC", Created=a.Created, Project="后登记的合成实盘", Title="后登记的合成实盘" };
                typeof(MainForm).GetField("entries",Flags).SetValue(f,new System.Collections.Generic.List<Entry>{newerDisc,a,b});
                Call(f,"FillGrid");
                Check(((Entry)grid.Rows[0].Tag).Id==newerDisc.Id && ((Entry)grid.Rows[1].Tag).Id==b.Id,"manual sidecar rows and real rows share registration sequence order within same second");
                a.Created="2026-01-02 12:00:00"; Call(f,"FillGrid");
                Check(((Entry)grid.Rows[0].Tag).Id==a.Id,"registration time takes precedence over storage sidecar and sequence");
                CheckImportCounter(f);
                f.Close();
            }
            CheckActualButtons(root);
            Console.WriteLine("Synthetic UI artifacts: "+root); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Call(MainForm f,string name,bool value) { typeof(MainForm).GetMethod(name,Flags).Invoke(f,new object[]{value}); }
}
