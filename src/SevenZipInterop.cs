using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DiscArchive
{
    // ABI follows the official 7-Zip IDecl.h, IStream.h and Archive/IArchive.h.
    // These are IUnknown interfaces, NOT System.Runtime.InteropServices.ComTypes.IStream.
    internal static class SevenZipInterop
    {
        internal const int Fail = unchecked((int)0x80004005);
        internal static void Check(int hr, string action)
        {
            if (hr != 0) throw new IOException(action + " (7-Zip HRESULT 0x" + hr.ToString("X8") + ").");
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct PropVariant
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal long Number;
            [FieldOffset(8)] internal IntPtr Pointer;
            internal object Value
            {
                get
                {
                    switch (Type)
                    {
                        case 0: return null;
                        case 8: return Marshal.PtrToStringBSTR(Pointer);
                        case 11: return (short)Number != 0;
                        case 19: return (uint)Number;
                        case 20: return Number;
                        case 21: return (ulong)Number;
                        case 64: try { return DateTime.FromFileTimeUtc(Number); } catch (ArgumentOutOfRangeException) { return null; }
                        default: throw new InvalidDataException("Unsupported 7-Zip property type: " + Type);
                    }
                }
            }
            internal void Clear() { PropVariantClear(ref this); }
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISequentialIn
        {
            [PreserveSig] int Read(IntPtr data, uint size, IntPtr processed);
        }
        [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IIn
        {
            [PreserveSig] int Read(IntPtr data, uint size, IntPtr processed);
            [PreserveSig] int Seek(long offset, uint origin, IntPtr position);
        }
        [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IOut
        {
            [PreserveSig] int Write(IntPtr data, uint size, IntPtr processed);
        }
        [ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IArchive
        {
            [PreserveSig] int Open(IIn stream, ref ulong maxStart, IntPtr callback);
            [PreserveSig] int Close();
            [PreserveSig] int GetNumberOfItems(out uint count);
            [PreserveSig] int GetProperty(uint index, uint property, ref PropVariant value);
            [PreserveSig] int Extract([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] indices, uint count, int test, IExtract callback);
            [PreserveSig] int GetArchiveProperty(uint property, ref PropVariant value);
            [PreserveSig] int GetNumberOfProperties(out uint count);
            [PreserveSig] int GetPropertyInfo(uint index, out IntPtr name, out uint property, out ushort type);
            [PreserveSig] int GetNumberOfArchiveProperties(out uint count);
            [PreserveSig] int GetArchivePropertyInfo(uint index, out IntPtr name, out uint property, out ushort type);
        }
        [ComImport, Guid("23170F69-40C1-278A-0000-000600400000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IGetStream
        {
            [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialIn stream);
        }
        [ComImport, Guid("23170F69-40C1-278A-0000-000600700000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IRawProps
        {
            [PreserveSig] int GetParent(uint index, out uint parent, out uint type);
            [PreserveSig] int GetRawProp(uint index, uint property, out IntPtr data, out uint size, out uint type);
            [PreserveSig] int GetNumRawProps(out uint count);
            [PreserveSig] int GetRawPropInfo(uint index, out IntPtr name, out uint property);
        }
        [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IExtract
        {
            [PreserveSig] int SetTotal(ulong total);
            [PreserveSig] int SetCompleted(IntPtr completed);
            [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out IOut stream, int mode);
            [PreserveSig] int PrepareOperation(int mode);
            [PreserveSig] int SetOperationResult(int result);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        internal sealed class Input : IIn
        {
            private readonly Stream stream;
            private readonly byte[] buffer = new byte[128 * 1024];
            private long position;
            internal long Position { get { return position; } set { position = value; } }
            internal Exception Error;
            internal Input(Stream stream) { this.stream = stream; }
            public int Read(IntPtr data, uint size, IntPtr processed)
            {
                if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, 0);
                try
                {
                    int count = (int)Math.Min(size, (uint)buffer.Length);
                    stream.Position = position;
                    int n = stream.Read(buffer, 0, count);
                    if (n == 0 && count != 0 && position < stream.Length) throw new EndOfStreamException("Partition returned EOF before its declared end.");
                    Marshal.Copy(buffer, 0, data, n);
                    position = checked(position + n);
                    if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, n);
                    return 0;
                }
                catch (Exception ex) { Error = ex; return Fail; }
            }
            public int Seek(long offset, uint origin, IntPtr result)
            {
                try
                {
                    long next = checked((origin == 0 ? 0 : origin == 1 ? position : origin == 2 ? stream.Length : throwOrigin()) + offset);
                    if (next < 0) return unchecked((int)0x80030001);
                    position = next;
                    if (result != IntPtr.Zero) Marshal.WriteInt64(result, position);
                    return 0;
                }
                catch (Exception ex) { Error = ex; return Fail; }
            }
            private static long throwOrigin() { throw new ArgumentOutOfRangeException("origin"); }
            internal void CheckError()
            {
                if (Error != null) { Exception ex = Error; Error = null; throw new IOException("Reading the source partition failed.", ex); }
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        internal sealed class Extraction : IExtract, IOut
        {
            private readonly uint index;
            private readonly Stream output;
            private readonly long limit;
            internal Exception Error;
            internal int Result = -1;
            internal bool Requested;
            internal Extraction(uint index, Stream output, long limit) { this.index = index; this.output = output; this.limit = limit; }
            public int SetTotal(ulong total) { return 0; }
            public int SetCompleted(IntPtr value) { return 0; }
            public int PrepareOperation(int mode) { return mode == 0 ? 0 : Fail; }
            public int SetOperationResult(int result) { Result = result; return 0; }
            public int GetStream(uint requested, out IOut stream, int mode)
            {
                stream = null;
                if (requested != index || mode != 0 || Requested) return Fail;
                Requested = true; stream = this; return 0;
            }
            public int Write(IntPtr data, uint size, IntPtr processed)
            {
                if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, 0);
                try
                {
                    if (size > int.MaxValue || output.Position > limit - size) throw new InvalidDataException("Decoded item exceeds its declared size.");
                    byte[] bytes = new byte[Math.Min((int)size, 128 * 1024)];
                    uint done = 0;
                    while (done < size)
                    {
                        int n = (int)Math.Min((uint)bytes.Length, size - done);
                        Marshal.Copy(IntPtr.Add(data, (int)done), bytes, 0, n);
                        output.Write(bytes, 0, n); done += (uint)n;
                    }
                    if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, (int)done);
                    return 0;
                }
                catch (Exception ex) { Error = ex; return Fail; }
            }
        }
    }
}
