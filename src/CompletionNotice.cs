using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiscArchive
{
    // One reusable, non-modal desktop notice. Call on the owner's UI thread.
    internal sealed class CompletionNotice : IDisposable
    {
        private readonly Func<Rectangle> workArea;
        private readonly int duration;
        private NoticeWindow window;
        private Form trackedOwner;
        private bool disposed;

        public CompletionNotice() : this(null, 25000) { }

        // Isolated UI tests can place the real window outside the user's screen.
        internal CompletionNotice(Func<Rectangle> workArea, int duration)
        {
            this.workArea = workArea;
            this.duration = Math.Max(1, duration);
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
            if (trackedOwner != owner)
            {
                DetachOwner();
                trackedOwner = owner;
                owner.FormClosed += OwnerClosed;
                owner.Disposed += OwnerDisposed;
            }
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
            if (window != null) { window.Dispose(); window = null; }
        }

        private sealed class NoticeWindow : Form
        {
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
                close.Click += delegate { timer.Stop(); Hide(); };
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

            internal void Present(string title, string text, bool attention, Rectangle area, int milliseconds)
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
                timer.Start();
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
