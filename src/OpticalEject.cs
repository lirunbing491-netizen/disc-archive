using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive
{
    // Injectable boundary; tests never need to eject a physical drive.
    internal interface IOpticalEjectDevice : IDisposable
    {
        void Verify(uint baseline);
        bool Eject(out int error);
    }

    internal sealed class OpticalEjectTicket : IOpticalEjectTicket
    {
        private readonly object sync=new object();
        private IOpticalEjectDevice device;
        private readonly uint baseline;
        private Action verifySource,releaseSource;
        private string unavailable;
        private bool attempted,disposed;

        internal static OpticalEjectTicket Unavailable(string reason) {return new OpticalEjectTicket(reason);}
        private OpticalEjectTicket(string reason) {unavailable=reason;}
        internal OpticalEjectTicket(IOpticalEjectDevice device,uint baseline,Action verifySource,Action releaseSource)
        {this.device=device;this.baseline=baseline;this.verifySource=verifySource;this.releaseSource=releaseSource;}

        public bool TryEject(out string reason)
        {
            lock(sync)
            {
                if(disposed || attempted) {reason="安全弹盘凭据已使用或关闭；不会再次发送弹盘命令。";return false;}
                attempted=true;
                if(unavailable!=null) {reason=unavailable;return false;}
                try
                {
                    verifySource();
                    device.Verify(baseline);
                    // All readers were closed by the caller. Now release the
                    // retained original lock/counter handle, preserving only
                    // this already-bound physical CDROM command handle.
                    Action release=releaseSource;releaseSource=null;release();
                    device.Verify(baseline);
                    int error;
                    if(!device.Eject(out error))
                    {
                        reason="Windows 未接受弹盘请求（错误 "+error+"）；光盘保留，请手动取出。";
                        return false;
                    }
                    reason="Windows 已接受本次原盘的弹盘请求。";
                    return true;
                }
                catch(Exception error)
                {
                    reason="未自动弹盘："+error.Message+" 光盘保留，请手动取出。";
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock(sync)
            {
                if(disposed)return;
                disposed=true;
                try {if(releaseSource!=null){Action release=releaseSource;releaseSource=null;release();}}
                finally {if(device!=null){device.Dispose();device=null;}}
            }
        }
    }

    internal sealed class OpticalEjectDevice : IOpticalEjectDevice
    {
        // ntddcdrm.h: CTL_CODE(FILE_DEVICE_CD_ROM=2, 0x0202,
        // METHOD_BUFFERED=0, FILE_READ_ACCESS=1). Never sends FSCTL_DISMOUNT_VOLUME.
        internal const uint CdromEjectMedia=0x00024808;
        private readonly SafeFileHandle handle;
        private readonly string root;
        internal readonly string Identity;

        internal OpticalEjectDevice(string root)
        {
            this.root=root;
            handle=OpticalNative.CreateFile(@"\\.\"+root.Substring(0,2),0x80000000u,7,IntPtr.Zero,3,0,IntPtr.Zero);
            try
            {
                if(handle.IsInvalid)throw OpticalNative.Error("普通用户无法取得光驱弹盘句柄");
                Identity=OpticalNative.DeviceIdentity(root,handle);
            }
            catch {handle.Dispose();throw;}
        }

        internal uint Count()
        {
            uint count;int error;
            if(OpticalNative.Count(handle,out count,out error))return count;
            if(OpticalNative.Unsupported(error) && OpticalNative.ReadCount(handle,out count,out error))return count;
            throw new IOException("无法取得可靠换盘计数（Windows 错误 "+error+"）；仅凭卷标、盘符或锁盘不足以安全弹盘。");
        }

        public void Verify(uint baseline)
        {
            using(new OpticalNative.ErrorMode())
            {
                if(handle.IsClosed || handle.IsInvalid || OpticalNative.GetDriveType(root)!=5 ||
                    !String.Equals(Identity,OpticalNative.DeviceIdentity(root,handle),StringComparison.Ordinal))
                    throw new IOException("光驱已断开或盘符映射已变化，无法确认原设备。");
                if(Count()!=baseline)throw new IOException("检测到换盘，无法确认仍为本次归档的光盘。");
            }
        }

        public bool Eject(out int error)
        {
            using(new OpticalNative.ErrorMode())
            {
                uint returned;
                bool ok=DeviceIoControl(handle,CdromEjectMedia,null,0,null,0,out returned,IntPtr.Zero);
                error=ok?0:Marshal.GetLastWin32Error();
                return ok;
            }
        }
        public void Dispose(){handle.Dispose();}
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputSize,
            byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
    }
}
