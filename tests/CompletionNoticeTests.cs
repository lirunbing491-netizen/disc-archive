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
    private static Form DriveWindow(CompletionNotice notice)
    {
        return (Form)typeof(CompletionNotice).GetField("driveWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(notice);
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
        using (Form owner = new SyntheticOwner())
        using (CompletionNotice notice = new CompletionNotice(delegate { return offscreen; }, 500))
        {
            owner.StartPosition = FormStartPosition.Manual;
            owner.Location = offscreen.Location;
            owner.ShowInTaskbar = false;
            owner.Show();
            notice.ShowDrive(owner, "D:\\", "本张已拷出，可以换盘", "项目：成功项目　DISC-001\r\n完整拷出 12 个文件，失败条目 0。", false);
            Form combined = DriveWindow(notice);
            Check(combined.AccessibleName == "本张已拷出，可以换盘" && ((Label)combined.Controls[0]).Text == "本张已拷出，可以换盘",
                "one drive retains the original actionable result title");
            Check(((Label)combined.Controls[1]).Text.Contains("D:") && ((Label)combined.Controls[1]).Text.Contains("成功项目") &&
                ((Label)combined.Controls[1]).Text.Contains("DISC-001") && ((Label)combined.Controls[1]).Text.Contains("失败条目 0"),
                "one drive shows drive, project, disc and counts without hover");
            Pump(70);
            notice.ShowDrive(owner, "E:", "文件缺失或中断", "项目：失败项目　DISC-002\r\n完整拷出 10 个文件，失败数：2", true);
            Check(Object.ReferenceEquals(combined, DriveWindow(notice)) && combined.Visible,
                "near-simultaneous completions share a persistent visible summary");
            Label summary = (Label)combined.Controls[1];
            Check(summary.Text.Contains("D:  本张已拷出") && summary.Text.Contains("E:  文件缺失或中断"),
                "success and failure remain visibly distinguishable by drive");
            Check(summary.Text.Contains("12 个文件") && summary.Text.Contains("10 个文件") && summary.Text.Contains("失败数：2"),
                "near-simultaneous completion counts remain directly visible");
            Check(combined.AccessibleDescription.Contains("成功项目") && combined.AccessibleDescription.Contains("失败数：2"),
                "summary preserves full details for both drives");
            Check(!combined.Bounds.IntersectsWith(screen) && !combined.Modal && owner.Enabled,
                "multi-drive summary stays offscreen and nonblocking in synthetic test");
            PropertyInfo cp = combined.GetType().GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic);
            Check((((CreateParams)cp.GetValue(combined, null)).ExStyle & 0x08000080) == 0x08000080,
                "multi-drive summary retains no-activation tool-window styles");
            notice.ShowDrive(owner, "d:", "再次完成", "同一光驱的新结果", false);
            Check(Object.ReferenceEquals(combined, DriveWindow(notice)) && !summary.Text.Contains("D:  已拷出") && summary.Text.Contains("D:  再次完成"),
                "same drive normalizes its key and replaces only its own result");
            Pump(290);
            notice.ShowDrive(owner, "F:", "已拷出", "第三台", false);
            Pump(440);
            Check(DriveWindow(notice) != null && summary.Text.Contains("F:") && !summary.Text.Contains("D:") && !summary.Text.Contains("E:"),
                "older drives expire independently without removing the newer result");
            Check(DriveWindow(notice).AccessibleName == "已拷出", "expiry back to one drive restores its original title");
            Pump(320);
            Check(combined.IsDisposed && DriveWindow(notice) == null,
                "last expiry destroys the summary and clears native resources");
            notice.ShowDrive(owner, "D:", "已拷出", "重新完成", false);
            Form reopened = DriveWindow(notice);
            Check(reopened != null && reopened.Visible && !Object.ReferenceEquals(combined, reopened),
                "later completion creates a fresh summary after cleanup");
            notice.Show(owner, "单盘通知", "兼容入口", false);
            Form legacy = Window(notice);
            owner.Close();
            Pump(40);
            Check(reopened.IsDisposed && legacy.IsDisposed && DriveWindow(notice) == null,
                "owner close cleans both legacy and multi-drive windows");
            notice.ShowDrive(owner, "G:", "ignored", "ignored", false);
            Check(DriveWindow(notice) == null, "late drive completion cannot reopen a closed owner");
        }
        using (Form owner = new SyntheticOwner())
        using (CompletionNotice notice = new CompletionNotice(delegate { return offscreen; }, 1000))
        {
            owner.StartPosition = FormStartPosition.Manual;
            owner.Location = offscreen.Location;
            owner.ShowInTaskbar = false;
            owner.Show();
            for (char drive = 'D'; drive <= 'Z'; drive++) notice.ShowDrive(owner, drive + ":", "已拷出", "合成项目", false);
            Form compact = DriveWindow(notice);
            Check(((Label)compact.Controls[1]).Text.Contains("另有") && ((Label)compact.Controls[2]).Text.Contains("空间有限"),
                "limited space explicitly groups overflow drives");
            Check(compact.AccessibleDescription.Contains("D:") && compact.AccessibleDescription.Contains("Z:"),
                "overflow retains full results in hover and accessible details");
            ((Label)compact.Controls[3]).GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(compact.Controls[3], new object[] { EventArgs.Empty });
            Check(compact.IsDisposed && DriveWindow(notice) == null, "manual dismissal clears summary and pending expirations");
        }
        Console.WriteLine("PASS " + checks + " checks; synthetic UI only, notices off-screen, no real files or jobs.");
    }
}
