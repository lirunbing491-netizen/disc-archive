using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace DiscArchive
{
    // One reusable, non-modal desktop notice. Call on the owner's UI thread.
    internal sealed class CompletionNotice : IDisposable
    {
        private readonly Func<Rectangle> workArea;
        private readonly int duration;
        private NoticeWindow window;
        private NoticeWindow driveWindow;
        private readonly SortedDictionary<string, DriveResult> drives = new SortedDictionary<string, DriveResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer driveTimer = new Timer();
        private Form trackedOwner;
        private bool disposed;

        public CompletionNotice() : this(null, 25000) { }

        // Isolated UI tests can place the real window outside the user's screen.
        internal CompletionNotice(Func<Rectangle> workArea, int duration)
        {
            this.workArea = workArea;
            this.duration = Math.Max(1, duration);
            driveTimer.Interval = Math.Min(250, this.duration);
            driveTimer.Tick += delegate { ExpireDrives(); };
        }

        private sealed class DriveResult
        {
            internal string Heading, Message;
            internal bool Attention;
            internal DateTime Expires;
        }

        // Each drive retains its own result and expiry; a later drive never replaces it.
        public void ShowDrive(Form owner, string drive, string heading, string message, bool attention)
        {
            if (disposed || owner == null || owner.IsDisposed || owner.Disposing) return;
            if (owner.InvokeRequired)
            {
                try { owner.BeginInvoke(new Action(delegate { ShowDrive(owner, drive, heading, message, attention); })); }
                catch (InvalidOperationException) { }
                return;
            }
            TrackOwner(owner);
            string key = (drive ?? "").Trim().TrimEnd('\\', '/').ToUpperInvariant();
            if (key.Length == 1 && Char.IsLetter(key[0])) key += ":";
            if (key.Length == 0) key = "未指定光驱";
            drives[key] = new DriveResult { Heading = String.IsNullOrWhiteSpace(heading) ? "本张处理结束" : heading,
                Message = message ?? "", Attention = attention, Expires = DateTime.UtcNow.AddMilliseconds(duration) };
            PresentDrives();
            driveTimer.Start();
        }

        private void TrackOwner(Form owner)
        {
            if (trackedOwner == owner) return;
            DetachOwner();
            trackedOwner = owner;
            owner.FormClosed += OwnerClosed;
            owner.Disposed += OwnerDisposed;
        }

        private void ExpireDrives()
        {
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, DriveResult> entry in drives)
                if (entry.Value.Expires <= DateTime.UtcNow) expired.Add(entry.Key);
            foreach (string key in expired) drives.Remove(key);
            if (drives.Count == 0) ClearDrives();
            else if (expired.Count > 0) PresentDrives();
        }

        private void ClearDrives()
        {
            driveTimer.Stop();
            drives.Clear();
            if (driveWindow != null) { driveWindow.Dispose(); driveWindow = null; }
        }

        private void PresentDrives()
        {
            if (driveWindow == null)
            {
                driveWindow = new NoticeWindow();
                driveWindow.Dismissed += delegate { ClearDrives(); };
            }
            Rectangle area = workArea == null ? Screen.FromControl(trackedOwner).WorkingArea : workArea();
            StringBuilder summary = new StringBuilder();
            StringBuilder details = new StringBuilder();
            bool attention = false;
            foreach (KeyValuePair<string, DriveResult> entry in drives)
            {
                details.Append(entry.Key).Append("  ").Append(entry.Value.Heading).Append("\r\n").Append(entry.Value.Message).Append("\r\n\r\n");
                attention |= entry.Value.Attention;
            }
            if (drives.Count == 1)
            {
                foreach (KeyValuePair<string, DriveResult> entry in drives)
                    driveWindow.Present(entry.Value.Heading, entry.Key + "\r\n" + entry.Value.Message, attention, area, duration, false);
                driveWindow.SetDriveDetails(details.ToString(), false);
                return;
            }
            driveWindow.Present(drives.Count + " 台光驱处理结束", "", attention, area, duration, false);
            int rows = driveWindow.MessageRows;
            int linesPerDrive = drives.Count * 3 <= rows ? 3 : 2;
            int visibleCount = Math.Min(drives.Count, Math.Max(1, rows / linesPerDrive));
            bool overflow = visibleCount < drives.Count;
            if (overflow) visibleCount = Math.Max(1, (rows - 1) / linesPerDrive);
            int index = 0;
            StringBuilder remaining = new StringBuilder();
            foreach (KeyValuePair<string, DriveResult> entry in drives)
            {
                if (index++ >= visibleCount) { remaining.Append(entry.Key).Append(" "); continue; }
                if (summary.Length > 0) summary.Append("\r\n");
                summary.Append(driveWindow.FitLine(entry.Key + "  " + entry.Value.Heading));
                string[] parts = entry.Value.Message.Replace("\r", "").Split('\n');
                if (linesPerDrive == 3) summary.Append("\r\n").Append(driveWindow.FitLine(parts[0]));
                // MainForm supplies project/disc on the first line and counts on the second.
                string counts = parts.Length > 1 ? parts[1] : parts[0];
                summary.Append("\r\n").Append(driveWindow.FitLine(counts));
            }
            if (overflow) summary.Append("\r\n").Append(driveWindow.FitLine("另有 " + (drives.Count - visibleCount) + " 台：" + remaining));
            driveWindow.SetDriveSummary(summary.ToString());
            driveWindow.SetDriveDetails(details.ToString(), overflow);
        }

        public void Show(Form owner, string heading, string message, bool attention)
        {
            if (disposed || owner == null || owner.IsDisposed || owner.Disposing) return;
            if (owner.InvokeRequired)
            {
                try { owner.BeginInvoke(new Action(delegate { Show(owner, heading, message, attention); })); }
                catch (InvalidOperationException) { /* Owner closed while queued. */ }
                return;
            }
            TrackOwner(owner);
            if (window == null) window = new NoticeWindow();
            Rectangle area = workArea == null ? Screen.FromControl(owner).WorkingArea : workArea();
            window.Present(heading, message, attention, area, duration);
        }

        private void OwnerClosed(object sender, FormClosedEventArgs e) { Dispose(); }
        private void OwnerDisposed(object sender, EventArgs e) { Dispose(); }
        private void DetachOwner()
        {
            if (trackedOwner == null) return;
            trackedOwner.FormClosed -= OwnerClosed;
            trackedOwner.Disposed -= OwnerDisposed;
            trackedOwner = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DetachOwner();
            ClearDrives();
            driveTimer.Dispose();
            if (window != null) { window.Dispose(); window = null; }
        }

        private sealed class NoticeWindow : Form
        {
            internal event EventHandler Dismissed;
            private readonly Label heading = new Label();
            private readonly Label message = new Label();
            private readonly Label footer = new Label();
            private readonly Label close = new Label();
            private readonly Timer timer = new Timer();
            private readonly ToolTip fullText = new ToolTip();
            private readonly Font titleFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
            private readonly Font bodyFont = new Font("Microsoft YaHei UI", 10);

            internal NoticeWindow()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                AutoScaleMode = AutoScaleMode.Dpi;
                BackColor = Color.FromArgb(245, 249, 252);
                Padding = new Padding(16);
                heading.Font = titleFont;
                message.Font = bodyFont;
                footer.Font = bodyFont;
                close.Font = bodyFont;
                heading.AutoEllipsis = message.AutoEllipsis = true;
                heading.UseMnemonic = message.UseMnemonic = footer.UseMnemonic = false;
                close.Text = "×";
                close.TextAlign = ContentAlignment.MiddleCenter;
                close.Cursor = Cursors.Hand;
                close.AccessibleName = "收起完成通知";
                close.Click += delegate { timer.Stop(); Hide(); if (Dismissed != null) Dismissed(this, EventArgs.Empty); };
                footer.Text = "可继续换盘；此通知会自动收起";
                footer.ForeColor = Color.FromArgb(70, 82, 94);
                Controls.AddRange(new Control[] { heading, message, footer, close });
                timer.Tick += delegate { timer.Stop(); Hide(); };
                AccessibleRole = AccessibleRole.Alert;
            }

            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // NOACTIVATE, TOOLWINDOW
                    return cp;
                }
            }

            internal void Present(string title, string text, bool attention, Rectangle area, int milliseconds, bool autoHide = true)
            {
                timer.Stop();
                heading.Text = String.IsNullOrWhiteSpace(title) ? "本张处理结束" : title;
                message.Text = text ?? "";
                heading.ForeColor = attention ? Color.FromArgb(155, 67, 12) : Color.FromArgb(15, 104, 71);
                AccessibleName = heading.Text;
                AccessibleDescription = message.Text + "。" + footer.Text;
                fullText.SetToolTip(heading, heading.Text);
                fullText.SetToolTip(message, message.Text);
                int width = Math.Min(480, Math.Max(240, area.Width - 24));
                int height = Math.Min(286, Math.Max(180, area.Height - 24));
                Size = new Size(width, height);
                heading.SetBounds(18, 14, width - 62, 58);
                close.SetBounds(width - 38, 10, 28, 28);
                message.SetBounds(18, 78, width - 36, height - 126);
                footer.SetBounds(18, height - 40, width - 36, 30);
                Location = new Point(area.Right - width - 12, area.Bottom - height - 12);
                // Deliberately unowned: Windows hides owned forms when their owner minimizes.
                // Lifetime is instead tied to the owner by CompletionNotice's events.
                if (!Visible) Show();
                timer.Interval = milliseconds;
                if (autoHide) timer.Start();
            }

            internal int MessageRows { get { return Math.Max(1, message.Height / message.Font.Height); } }

            internal string FitLine(string text)
            {
                text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
                int length = text.Length;
                while (length > 0 && TextRenderer.MeasureText(text.Substring(0, length) + (length < text.Length ? "…" : ""), message.Font).Width > message.Width - 8) length--;
                return text.Substring(0, length) + (length < text.Length ? "…" : "");
            }

            internal void SetDriveSummary(string text) { message.Text = text; }

            internal void SetDriveDetails(string details, bool overflow)
            {
                footer.Text = overflow ? "空间有限，悬停查看全部结果；各项独立收起" : "可继续换盘；各光驱约25秒后独立收起";
                fullText.SetToolTip(message, details);
                AccessibleDescription = details + footer.Text;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    timer.Dispose();
                    fullText.Dispose();
                    titleFont.Dispose();
                    bodyFont.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
