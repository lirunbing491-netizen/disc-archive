using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DiscArchive;

internal static class CompletionNoticeTests
{
    private sealed class SyntheticOwner : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; }
        }
    }
    private static int checks;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    private static Form Window(CompletionNotice notice)
    {
        return (Form)typeof(CompletionNotice).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(notice);
    }
    private static void Pump(int milliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        // Synthetic forms only, outside all physical screen coordinates; no real job or data.
        Rectangle screen = SystemInformation.VirtualScreen;
        Rectangle offscreen = new Rectangle(screen.Right + 3000, screen.Bottom + 3000, 1000, 800);
        using (Form owner = new SyntheticOwner())
        using (CompletionNotice notice = new CompletionNotice(delegate { return offscreen; }, 180))
        {
            owner.StartPosition = FormStartPosition.Manual;
            owner.Location = offscreen.Location;
            owner.ShowInTaskbar = false;
            owner.Show();
            int ticks = 0;
            using (System.Windows.Forms.Timer heartbeat = new System.Windows.Forms.Timer { Interval = 20 })
            {
                heartbeat.Tick += delegate { ticks++; };
                heartbeat.Start();
                notice.Show(owner, "已拷出", "项目：合成项目\r\n文件数：12；失败数：0", false);
                Form first = Window(notice);
                Check(first != null && first.IsHandleCreated && first.Visible, "real native notification created and shown");
                Check(!first.Modal && owner.Enabled, "show returns without blocking owner or next disc");
                Check(!first.Bounds.IntersectsWith(screen), "test notice stays outside user desktop");
                Check(first.TopMost && !first.ShowInTaskbar && first.Owner == null, "desktop notice is topmost, taskbar-free and independent of owner minimization");
                PropertyInfo cp = first.GetType().GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic);
                CreateParams style = (CreateParams)cp.GetValue(first, null);
                Check((style.ExStyle & 0x08000000) != 0 && (style.ExStyle & 0x80) != 0, "native NOACTIVATE and TOOLWINDOW styles prevent keyboard focus stealing");
                owner.WindowState = FormWindowState.Minimized;
                Pump(40);
                Check(first.Visible, "notification remains visible when synthetic owner is minimized");
                notice.Show(owner, "文件缺失或中断", "项目：" + new string('长', 400) + "\r\n文件数：10；失败数：2", true);
                Check(Object.ReferenceEquals(first, Window(notice)), "consecutive completion reuses one window");
                Check(first.AccessibleName == "文件缺失或中断" && first.AccessibleDescription.Contains("失败数：2"), "latest status and file counts replace previous result");
                Label title = (Label)first.Controls[0], body = (Label)first.Controls[1];
                Check(title.AutoEllipsis && body.AutoEllipsis && body.Text.Length > 400, "long project remains available while layout is bounded");
                Pump(300);
                Check(!first.Visible && !first.IsDisposed, "notice automatically hides without destroying reusable window");
                Check(ticks > 2, "UI message loop continues during notification");
                notice.Show(owner, "已拷出", "项目：下一张；文件数：1；失败数：0", false);
                Check(first.Visible, "next completion reopens the same window after timeout");
                owner.Close();
                Pump(30);
                Check(first.IsDisposed, "closing owner disposes notification and timer");
                notice.Show(owner, "ignored", "ignored", false);
                Check(Window(notice) == null, "late completion cannot reopen after owner closes");
            }
        }
        Console.WriteLine("PASS " + checks + " checks; synthetic UI only, notices off-screen, no real files or jobs.");
    }
}
