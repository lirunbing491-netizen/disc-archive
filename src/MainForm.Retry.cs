using System;
using System.Linq;
using System.Threading.Tasks;

namespace DiscArchive
{
    internal sealed partial class MainForm
    {
        // Tests substitute only the explicit human decision; real media and catalog paths are untouched.
        internal Func<string,bool> RetryConfirmation { get; set; }

        private static bool Retryable(Entry entry)
        {
            return entry!=null && (entry.Status==Workstation.Partial || entry.Status==Workstation.Review);
        }

        private bool CanRetry(DriveSlot slot,Entry expected,bool checking=false)
        {
            return !IsDisposed && !Disposing && !closeLater && !confirmingExit && slot!=null &&
                expected!=null && Object.ReferenceEquals(slot.RetryEntry,expected) &&
                slot.Connected && (checking || !slot.RetryChecking) && !slot.Polling && slot.Worker.Current==null && !slot.Worker.Busy && LiveEntry(expected.Id)==null;
        }

        private async Task RetrySlot(DriveSlot slot,Entry expected)
        {
            if(!CanRetry(slot,expected))return;
            slot.RetryChecking=true; SyncButtons();
            try {
                // A tray can have changed without an intervening timer tick. Never use an old
                // completion callback to arm a later registration or silently accept a new disc.
                bool ready=await Task.Run(()=>reader.Drives().Contains(slot.Drive,StringComparer.OrdinalIgnoreCase) && reader.Ready(slot.Drive));
                if(!CanRetry(slot,expected,true))return;
                if(!ready) {
                    slot.Info="请把原盘 "+expected.Id+" 放回 "+slot.Drive+"，然后重试；无需重新拍照。";
                    RenderSlot(slot); return;
                }
                if(selectedSlot!=slot)drives.SelectedItem=slot.Drive;
                tabs.SelectedIndex=0;
                if(WindowState==System.Windows.Forms.FormWindowState.Minimized)WindowState=System.Windows.Forms.FormWindowState.Normal;
                Activate();
                string question="请核对 "+slot.Drive+" 内实物确实是原盘 "+expected.Id+"（"+expected.Project+"），并与原盘面照片一致。\r\n"+
                    "重试将沿用原登记与回执，核对完整文件，并重新读取未完成条目。程序会检查盘的目录及内容抽样；证据不足时仍需要您核对实物。\r\n"+
                    "如已换成另一张盘，请取消并重新登记。确认重试这张原盘？";
                bool confirmed=RetryConfirmation!=null ? RetryConfirmation(question) : Confirm(question);
                if(!confirmed || !CanRetry(slot,expected,true))return;
                slot.Worker.Retry(expected,slot.Drive,readyAlreadyChecked:true);
                Entry armed=slot.Worker.Current;
                ArmSlot(slot,armed);
                slot.Worker.BlockAutomatic();
                await Poll(slot,true);
            }
            catch(Exception ex) {
                slot.Worker.BlockAutomatic(); slot.Info=ex.Message; RenderSlot(slot);
            }
            finally { slot.RetryChecking=false; if(!IsDisposed && !Disposing)SyncButtons(); }
        }
    }
}
