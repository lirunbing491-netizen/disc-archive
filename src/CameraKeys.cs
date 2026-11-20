using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DiscArchive
{
    internal sealed partial class MainForm
    {
        private void InstallCameraKeys(TabPage cameraPage)
        {
            CameraShutterKeys keys = new CameraShutterKeys(this, tabs, cameraPage, camera);
            Application.AddMessageFilter(keys);
            Disposed += delegate { Application.RemoveMessageFilter(keys); };
        }
    }

    // Filtering before WinForms key preprocessing covers both Space input keys and
    // Enter dialog keys. lParam's previous-key-state bit rejects OS auto-repeat.
    internal sealed class CameraShutterKeys : IMessageFilter
    {
        private readonly Form owner;
        private readonly TabControl tabs;
        private readonly TabPage page;
        private readonly CameraPanel camera;
        private readonly Func<bool> ownerHasForeground;
        private readonly HashSet<Keys> consumed = new HashSet<Keys>();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        internal CameraShutterKeys(Form owner, TabControl tabs, TabPage page, CameraPanel camera)
            : this(owner, tabs, page, camera,
                delegate { return Form.ActiveForm == owner && GetForegroundWindow() == owner.Handle; }) { }

        // Isolated desktop tests supply only the OS foreground decision. Focus,
        // enabled state, key messages, and capture all retain their real routes.
        internal CameraShutterKeys(Form owner, TabControl tabs, TabPage page, CameraPanel camera, Func<bool> ownerHasForeground)
        { this.owner = owner; this.tabs = tabs; this.page = page; this.camera = camera;
          this.ownerHasForeground = ownerHasForeground; }

        public bool PreFilterMessage(ref Message message)
        {
            const int KeyDown = 0x100, KeyUp = 0x101;
            if (message.Msg != KeyDown && message.Msg != KeyUp) return false;
            Keys key = (Keys)message.WParam.ToInt32();
            if (key != Keys.Enter && key != Keys.Space) return false;
            // Consume the matching release even if capture opened a modal dialog.
            if (message.Msg == KeyUp) return consumed.Remove(key);
            bool repeat = (message.LParam.ToInt64() & (1L << 30)) != 0;
            if (repeat && consumed.Contains(key)) return true;
            if (owner.IsDisposed || !owner.Visible || !owner.Enabled || !ownerHasForeground() || tabs.SelectedTab != page ||
                !camera.KeyboardCaptureReady || Control.ModifierKeys != Keys.None) return false;
            Control source = Control.FromHandle(message.HWnd);
            if (source == null || source.FindForm() != owner || !source.Focused ||
                !camera.AllowsShutterKeyFocus(source)) return false;
            if (!repeat)
            {
                consumed.Add(key);
                camera.CaptureFromKeyboard();
            }
            // A held key that started elsewhere must not become a shutter press.
            return true;
        }
    }
}
