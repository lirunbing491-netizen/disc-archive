using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscArchive;

internal static class ChoiceTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static T Find<T>(Control parent, string text) where T : Control
    {
        foreach (Control control in parent.Controls)
        {
            T found = control as T;
            if (found != null && (text == null || found.Text == text)) return found;
            found = Find<T>(control, text); if (found != null) return found;
        }
        return null;
    }
    private static void PumpUntil(Task task)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "decision settles within timeout");
    }
    private static Form Choice(Form owner)
    {
        Check(owner.OwnedForms.Length == 1, "one owned choice window");
        Form choice = owner.OwnedForms[0];
        choice.StartPosition = FormStartPosition.Manual; choice.Location = owner.Location;
        Check(!choice.Modal && owner.Enabled, "choice is non-modal and owner remains enabled");
        return choice;
    }
    private static Form Owner()
    {
        Rectangle screen = SystemInformation.VirtualScreen;
        Form owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(screen.Right + 3000, screen.Bottom + 3000), ShowInTaskbar = false };
        owner.Show(); return owner;
    }
    private static T Named<T>(Control parent,string name) where T:Control
    {
        foreach(Control c in parent.Controls) {if(c is T && c.Name==name)return (T)c;T found=Named<T>(c,name);if(found!=null)return found;}return null;
    }
    private static void Until(Func<bool> condition)
    {
        Stopwatch watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(5);}Check(condition(),"thumbnail update settles within timeout");
    }
    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        DiscMatch old = new DiscMatch { Id = "old-disc", Project = "合成项目", Status = "部分完成", Strong = false, Reason = "仅卷标相同", JobDir = "X:\\synthetic" };
        List<DiscMatch> candidates = new List<DiscMatch> { old };
        using (Form owner = Owner())
        {
            string fixture=Path.Combine(Path.GetTempPath(),"DiscChoicePhotos-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string current=Path.Combine(fixture,"current.png"),job=Path.Combine(fixture,"DiscArchive","OLD");Directory.CreateDirectory(job);
            using(Bitmap color=new Bitmap(640,480)){using(Graphics g=Graphics.FromImage(color))g.Clear(Color.Red);color.Save(current);using(Graphics g=Graphics.FromImage(color))g.Clear(Color.Blue);color.Save(Path.Combine(job,"photo.png"));}
            DiscMatch pictured=new DiscMatch {Id="OLD",Project="合成色块",Status="部分完成",JobDir=job,Reason="合成测试"};
            DiscMatch missing=new DiscMatch {Id="MISSING",Project="无图片",JobDir=Path.Combine(fixture,"no-photo")};
            Task<DiscMatch> photos=ArchiveChoices.Match(owner,"S:","CURRENT",new List<DiscMatch>{pictured,missing},CancellationToken.None,current);
            Form photoWindow=Choice(owner);photoWindow.Size=photoWindow.MinimumSize;photoWindow.PerformLayout();
            PictureBox left=Named<PictureBox>(photoWindow,"CurrentDiscPhoto"),right=Named<PictureBox>(photoWindow,"SelectedOldDiscPhoto");
            Check(left!=null && right!=null && right.Image==null,"two separate named photo previews with no default old selection");
            Find<ListBox>(photoWindow,null).SelectedIndex=0;
            Until(delegate{return left.Image!=null && right.Image!=null;});
            Check(((Bitmap)left.Image).GetPixel(left.Image.Width/2,left.Image.Height/2).R>240 && ((Bitmap)right.Image).GetPixel(right.Image.Width/2,right.Image.Height/2).B>240,"current red and selected old blue never cross sides");
            Check(left.Image.Width<=320 && left.Image.Height<=200 && right.Image.Width<=320 && right.Image.Height<=200,"large source images become detached bounded thumbnails");
            Check(left.ClientSize.Height>=40 && right.ClientSize.Height>=40,"both thumbnails retain visible height in short window");
            Check(left.PointToScreen(Point.Empty).X<right.PointToScreen(Point.Empty).X,"current preview appears left of selected old preview");
            string photoReceipt=Path.Combine(fixture,"paired-photo-short.png");
            using(Bitmap receipt=new Bitmap(photoWindow.Width,photoWindow.Height)){photoWindow.DrawToBitmap(receipt,new Rectangle(Point.Empty,receipt.Size));receipt.Save(photoReceipt);}
            Console.WriteLine("Synthetic photo comparison receipt: "+photoReceipt);
            Find<ListBox>(photoWindow,null).SelectedIndex=1;
            Check(right.Image==null && left.Image!=null,"switching to missing old photo immediately clears old image only");
            Find<ListBox>(photoWindow,null).SelectedIndex=0;Find<ListBox>(photoWindow,null).SelectedIndex=1;
            Application.DoEvents();Thread.Sleep(50);Application.DoEvents();
            Check(right.Image==null,"late old-photo decode cannot overwrite newly selected missing photo");
            Find<ListBox>(photoWindow,null).SelectedIndex=-1;Check(right.Image==null,"deselecting candidate clears old photo");
            photoWindow.Close();PumpUntil(photos);Check(photos.IsCanceled,"photo preview preserves close cancellation");
            using(FileStream released=File.Open(current,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(released.Length>0,"preview releases original photo file after decoding");
            Task<DiscMatch> match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, CancellationToken.None);
            Form window = Choice(owner);
            Button same = Find<Button>(window, "确认同一张：核对并补缺");
            Check(!same.Enabled && !match.IsCompleted, "no default same-disc acceptance");
            window.Size = window.MinimumSize;
            window.PerformLayout();
            foreach (Control control in same.Parent.Controls)
                Check(control.Right <= same.Parent.ClientSize.Width && control.Bottom <= same.Parent.ClientSize.Height, "decision buttons fit at minimum window size");
            Check(window.AutoScaleMode == AutoScaleMode.Font && window.Font.Size >= 10F, "readable font with system font scaling");
            Find<ListBox>(window, null).SelectedIndex = 0;
            Check(same.Enabled, "explicit selection enables same-disc choice");
            same.PerformClick(); PumpUntil(match);
            Check(match.Result == old && owner.OwnedForms.Length == 0, "same disc returns exact candidate and closes window");

            match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, CancellationToken.None);
            window = Choice(owner); Find<Button>(window, "确认另作新盘").PerformClick(); PumpUntil(match);
            Check(!match.IsCanceled && match.Result == null, "new-disc choice is distinct from cancellation");

            match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, CancellationToken.None);
            window = Choice(owner); window.Close(); PumpUntil(match);
            Check(match.IsCanceled, "window close cancels instead of approving merge");
            bool cancellationThrown = false;
            try { match.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancellationThrown = true; }
            Check(cancellationThrown, "awaiting closed decision throws OperationCanceledException");

            match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, CancellationToken.None);
            window = Choice(owner); Find<Button>(window, "暂不导入").PerformClick(); PumpUntil(match);
            Check(match.IsCanceled, "postpone cancels import");

            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, source.Token);
                Choice(owner);
                Task.Run(delegate { source.Cancel(); }); PumpUntil(match);
                Check(match.IsCanceled && owner.OwnedForms.Length == 0, "background cancellation closes window without deadlock");
            }
            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                source.Cancel(); match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, source.Token);
                Check(match.IsCanceled && owner.OwnedForms.Length == 0, "pre-canceled call does not show a window");
            }

            Task<string> project = ArchiveChoices.Project(owner, "Y:", "本次名字", new string[] { "项目一", "项目二" }, CancellationToken.None);
            window = Choice(owner);
            Check(!Find<Button>(window, "确认归入所选已有项目").Enabled, "project never defaults to existing entry");
            // The first TextBox is search; the later multiline TextBox contains the explanation.
            TextBox search = Find<TextBox>(window, null); search.Text = "项目二";
            ListBox list = Find<ListBox>(window, null);
            Check(list.Items.Count == 1 && list.SelectedIndex < 0, "search filters but does not auto-select");
            list.SelectedIndex = 0; Find<Button>(window, "确认归入所选已有项目").PerformClick(); PumpUntil(project);
            Check(project.Result == "项目二", "project choice returns exact known name");

            project = ArchiveChoices.Project(owner, "Y:", "本次名字", new string[] { "项目一" }, CancellationToken.None);
            window = Choice(owner); Find<Button>(window, "仍按本次名称").PerformClick(); PumpUntil(project);
            Check(project.Result == "本次名字", "keep current project returns proposed name");

            project = ArchiveChoices.Project(owner, "Y:", "本次名字", new string[0], CancellationToken.None);
            window = Choice(owner); Find<Button>(window, "暂缓导入").PerformClick(); PumpUntil(project);
            Check(project.IsCanceled, "project postponement cancels");

            match = ArchiveChoices.Match(owner, "X:", "new-disc", candidates, CancellationToken.None);
            Choice(owner);
            project = ArchiveChoices.Project(owner, "Y:", "本次名字", new string[0], CancellationToken.None);
            Check(owner.OwnedForms.Length == 2 && owner.Enabled && !match.IsCompleted && !project.IsCompleted, "two drive decisions coexist without blocking owner");
            owner.Close(); PumpUntil(match); PumpUntil(project);
            Check(match.IsCanceled && project.IsCanceled, "owner exit settles all pending decisions");
        }
        using (Form disposed = new Form())
        {
            disposed.Dispose();
            Check(ArchiveChoices.Project(disposed, "Y:", "项目", null, CancellationToken.None).IsCanceled, "disposed owner cancels immediately");
        }
        Console.WriteLine("Choice tests passed: " + checks);
    }
}
