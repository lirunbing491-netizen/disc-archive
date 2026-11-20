using System.Threading;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Forms;
using DiscArchive;

internal static class CameraKeysTests
{
    private static int checks;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
    private static bool Key(CameraShutterKeys keys, Control control, Keys key, bool up, bool repeat)
    {
        Message m = Message.Create(control.Handle, up ? 0x101 : 0x100, (IntPtr)(int)key,
            new IntPtr(1L | (repeat ? (1L << 30) : 0)));
        return keys.PreFilterMessage(ref m);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        // Explicit opt-in only: this mode displays and activates real windows.
        if (args.Contains("--interactive")) { Run(false); return 0; }
        // Never switch the user's input desktop. Native focus can be assigned on
        // this desktop, but real foreground/WinForms automatic focus needs input.
        IntPtr desktop = CreateDesktop("DiscArchive-CameraKeys-" + Guid.NewGuid().ToString("N"),
            IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) { Console.Error.WriteLine("CreateDesktop failed: " + Marshal.GetLastWin32Error()); return 1; }
        Thread worker = new Thread(delegate() {
            int result = 1;
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadDesktop failed");
                Run(true); result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
            }
            // End the dedicated runner after disposing its forms. Returning from an
            // isolated STA can otherwise block in COM apartment teardown.
            Environment.Exit(result);
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        return 1;
    }
    private static void Run(bool isolated)
    {
        int captured = 0, validated = 0;
        using (Form form = new Form())
        using (CameraPanel camera = new CameraPanel(delegate(string path) {
            Check(File.Exists(path), "shared shutter delivers completed JPEG"); captured++;
        }, delegate { return new Bitmap(16,16); }))
        {
            camera.BeforeShutter = delegate { validated++; };
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage page = new TabPage("camera"), other = new TabPage("other");
            tabs.TabPages.AddRange(new TabPage[] { page, other });
            page.Controls.Add(camera); form.Controls.Add(tabs);
            TextBox text = new TextBox { Dock=DockStyle.Bottom }; page.Controls.Add(text);
            form.Show(); SetForegroundWindow(form.Handle); form.Activate(); Application.DoEvents();
            Console.WriteLine("ENV isolated=" + isolated + " foreground=" + GetForegroundWindow() + " owner=" + form.Handle + " active=" + (Form.ActiveForm == form));
            IntPtr foreground = form.Handle;
            CameraShutterKeys filter = isolated ? new CameraShutterKeys(form,tabs,page,camera, delegate { return foreground == form.Handle; }) : new CameraShutterKeys(form,tabs,page,camera);
            camera.FocusPreviewForShutter(); Application.DoEvents();
            Control preview = camera.Controls.OfType<PictureBox>().Single();
            if (isolated) SetFocus(preview.Handle);
            Check(preview.Focused, "preview is keyboard focusable without hardware");
            if (isolated) Check(GetForegroundWindow() != form.Handle &&
                    !Key(new CameraShutterKeys(form,tabs,page,camera),preview,Keys.Enter,false,false) && captured==0,
                    "production foreground query rejects real inactive-desktop window");
            Button opener = camera.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text=="打开预览");
            opener.Enabled=true; opener.Focus(); if (isolated) SetFocus(opener.Handle);
            Check(opener.Focused, "first-frame handoff starts with real focus on preview opener");
            typeof(CameraPanel).GetField("focusPreviewOnFirstFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(camera,true);
            typeof(CameraPanel).GetMethod("UpdatePreview",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(camera,null);
            if (isolated)
            {
                Console.WriteLine("SKIP real foreground acquisition and automatic first-frame focus handoff require --interactive on a dedicated input desktop; not verified by isolated run.");
                SetFocus(preview.Handle);
            }
            else Check(preview.Focused, "first-frame focus handoff allows immediate keyboard capture after opening preview");
            Application.AddMessageFilter(filter);
            PostMessage(preview.Handle, 0x100, (IntPtr)(int)Keys.Enter, (IntPtr)1);
            Application.DoEvents();
            Application.RemoveMessageFilter(filter);
            Check(captured==1 && validated==1,
                "posted Enter is filtered before WinForms and uses shared registration check");
            Check(Key(filter,preview,Keys.Enter,false,true) && captured==1, "held Enter cannot repeat capture");
            Check(Key(filter,preview,Keys.Enter,true,false), "shutter release cannot activate native button");
            Check(Key(filter,preview,Keys.Enter,false,false) && captured==2, "released Enter can capture next disc");
            Key(filter,preview,Keys.Enter,true,false);
            Check(Key(filter,preview,Keys.Space,false,false) && captured==3, "Space captures once");
            Key(filter,preview,Keys.Space,false,true); Key(filter,preview,Keys.Space,true,false);
            Check(captured==3, "held Space cannot repeat capture");
            Check(Key(filter,preview,Keys.Space,false,true) && captured==3, "held key started elsewhere does not capture on return");
            camera.SetCaptureEnabled(false);
            Check(!Key(filter,preview,Keys.Enter,false,false) && captured==3, "registration disabled blocks shutter");
            camera.SetCaptureEnabled(true);
            text.Focus(); if (isolated) SetFocus(text.Handle);
            Check(text.Focused, "text exclusion starts with real textbox focus");
            Check(!Key(filter,text,Keys.Space,false,false), "text editing retains Space");
            foreach (Button button in camera.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>())
            {
                if (button.Text == "拍下这张盘面") continue;
                button.Enabled=true; button.Focus();
                if (isolated) SetFocus(button.Handle);
                Check(button.Focused, "button exclusion starts with real button focus");
                Check(!Key(filter,button,Keys.Enter,false,false), "other camera buttons retain native Enter");
            }
            ComboBox combo = camera.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<ComboBox>().Single();
            combo.Items.Add("synthetic device"); combo.Focus(); combo.DroppedDown=true;
            if (isolated) SetFocus(combo.Handle);
            Check(combo.Focused, "dropdown exclusion starts with real combo focus");
            Check(!Key(filter,combo,Keys.Space,false,false), "expanded device dropdown retains Space");
            combo.DroppedDown=false;
            Check(!Key(filter,combo,Keys.Enter,false,false), "closed dropdown retains Enter selection semantics");
            tabs.SelectedTab=other; Application.DoEvents();
            Check(!Key(filter,preview,Keys.Enter,false,false), "other tab cannot trigger camera shutter");
            tabs.SelectedTab=page; camera.FocusPreviewForShutter(); Application.DoEvents();
            using(Form dialog = new Form())
            {
                dialog.Shown += delegate {
                    Check(!Key(filter,preview,Keys.Enter,false,false), "modal dialog blocks main-window shutter");
                    dialog.BeginInvoke(new Action(dialog.Close));
                };
                dialog.ShowDialog(form);
            }
            using(Form foreign = new Form())
            {
                foreign.Show(); SetForegroundWindow(foreign.Handle); foreign.Activate(); Application.DoEvents();
                foreground = foreign.Handle;
                Check(!Key(filter,preview,Keys.Enter,false,false), "background main window cannot capture");
            }
            Check(captured==3 && validated==3, "excluded focus contexts never enter registration");
            SetForegroundWindow(form.Handle); form.Activate(); Application.DoEvents();
            foreground = form.Handle;
            Button shutter=camera.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text=="拍下这张盘面");
            shutter.Focus(); if (isolated) SetFocus(shutter.Handle);
            Check(shutter.Focused, "shutter test starts with real shutter focus");
            Check(Key(filter,shutter,Keys.Space,false,false) && captured==4, "focused shutter uses same once-only Space route");
            Check(Key(filter,shutter,Keys.Space,true,false) && captured==4, "focused shutter release cannot cause a second photo");
            form.Close();
        }
        using (Form emptyForm = new Form())
        using (CameraPanel empty = new CameraPanel(delegate { throw new Exception("unexpected capture"); }, delegate { return null; }))
        {
            TabControl tabs=new TabControl { Dock=DockStyle.Fill }; TabPage page=new TabPage();
            tabs.TabPages.Add(page); page.Controls.Add(empty); emptyForm.Controls.Add(tabs);
            emptyForm.Show(); SetForegroundWindow(emptyForm.Handle); emptyForm.Activate(); Application.DoEvents();
            Check(!empty.KeyboardCaptureReady, "missing first frame never enables shutter");
            emptyForm.Close();
        }
        Console.WriteLine("PASS " + checks + " checks. Synthetic frames and keyboard messages only; no camera or drive opened.");
    }
}
