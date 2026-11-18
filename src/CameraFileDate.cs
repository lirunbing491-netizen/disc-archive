using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DiscArchive
{
    // Reads directory metadata only: never follows image strips, tiles, previews or RawData.
    // EXIF 0x9003 is capture time; 0x9004 is digitization and 0x0132 modification.
    // Tag semantics: https://developer.adobe.com/xmp/docs/xmp-namespaces/exif/
    // Phase One 0x0112 layout: https://github.com/exiftool/exiftool/blob/master/lib/Image/ExifTool/PhaseOne.pm
    // The latter two deliberately do not qualify. Returned values keep camera wall time,
    // DateTimeKind.Unspecified; no workstation timezone conversion is performed.
    internal static class CameraFileDate
    {
        internal static DateTime? Read(string path, CancellationToken cancel)
        {
            DateTime? digitized;
            return Read(path, cancel, out digitized, null);
        }

        internal static DateTime? Read(string path, CancellationToken cancel, out DateTime? digitized)
        { return Read(path, cancel, out digitized, null); }

        // Evidence callback is optional and emits only metadata locations and the chosen tag.
        internal static DateTime? Read(string path, CancellationToken cancel, Action<string> evidence)
        {
            DateTime? digitized;
            return Read(path, cancel, out digitized, evidence);
        }

        private static DateTime? Read(string path, CancellationToken cancel, out DateTime? digitized, Action<string> evidence)
        {
            digitized = null;
            cancel.ThrowIfCancellationRequested();
            try
            {
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
                {
                    Reader reader = new Reader(input, cancel, evidence);
                    DateTime? capture = reader.Run();
                    digitized = reader.Digitized;
                    return capture;
                }
            }
            catch (BudgetException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private sealed class BudgetException : Exception { }
        private sealed class Node
        {
            internal long Offset;
            internal int Depth;
            internal Node(long offset, int depth) { Offset = offset; Depth = depth; }
        }
        private sealed class Reader
        {
            private readonly Stream input;
            private readonly CancellationToken cancel;
            private readonly Action<string> evidence;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly HashSet<long> visited = new HashSet<long>();
            private readonly Queue<Node> queue = new Queue<Node>();
            private long bytes, operations, basis, end;
            private bool little;
            private DateTime? phaseDate;
            private string phaseSource;
            internal DateTime? Digitized;
            internal Reader(Stream stream, CancellationToken token, Action<string> log) { input = stream; cancel = token; evidence = log; }

            private void Check()
            {
                cancel.ThrowIfCancellationRequested();
                // The time bound is checked between reads; a stalled operating-system
                // optical read cannot be interrupted by a synchronous FileStream API.
                if (clock.ElapsedMilliseconds > 2500 || bytes > 262144 || operations > 1024) throw new BudgetException();
            }
            private byte[] At(long position, int size)
            {
                Check();
                if (position < 0 || size < 0 || size > 262144 || position > input.Length - size || bytes + size > 262144) throw new BudgetException();
                operations++; bytes += size;
                byte[] data = new byte[size]; input.Position = position;
                for (int have = 0; have < size; ) { Check(); int n = input.Read(data, have, size - have); if (n == 0) throw new EndOfStreamException(); have += n; }
                return data;
            }
            private byte[] Meta(long offset, int size)
            { if (offset < 0 || basis + offset > end - size) throw new BudgetException(); return At(basis + offset, size); }
            private uint U32(byte[] data, int offset)
            {
                return little ? (uint)(data[offset] | data[offset+1]<<8 | data[offset+2]<<16 | data[offset+3]<<24)
                    : (uint)(data[offset]<<24 | data[offset+1]<<16 | data[offset+2]<<8 | data[offset+3]);
            }
            private ushort U16(byte[] data, int offset)
            { return little ? (ushort)(data[offset] | data[offset+1]<<8) : (ushort)(data[offset]<<8 | data[offset+1]); }
            private void Log(string value) { if (evidence != null) evidence(value + "; metadataBytes=" + bytes); }

            internal DateTime? Run()
            {
                end = input.Length;
                if (end < 8) return null;
                byte[] head = At(0, 8);
                if (head[0] == 0xff && head[1] == 0xd8)
                {
                    long pos = 2; bool found = false;
                    for (int marker = 0; marker < 256 && pos < input.Length - 4; marker++)
                    {
                        byte[] m = At(pos, 2);
                        if (m[0] != 0xff) return null;
                        if (m[1] == 0xff) { pos++; continue; }
                        if (m[1] == 0xda || m[1] == 0xd9) return null; // stop before compressed pixels
                        if (m[1] == 0x01 || (m[1] >= 0xd0 && m[1] <= 0xd7)) { pos += 2; continue; }
                        byte[] length = At(pos+2, 2); int n = length[0]*256 + length[1];
                        if (n < 2 || pos + 2 + n > input.Length) return null;
                        if (m[1] == 0xe1 && n >= 16)
                        {
                            byte[] signature = At(pos+4, 6);
                            if (Encoding.ASCII.GetString(signature) == "Exif\0\0")
                            { basis = pos+10; end = pos+2+n; head = At(basis, 8); found = true; break; }
                        }
                        pos += 2+n;
                    }
                    if (!found) return null;
                }
                if (head[0] == 'I' && head[1] == 'I') little = true;
                else if (head[0] == 'M' && head[1] == 'M') little = false;
                else return null;
                if (U16(head,2) != 42) return null; // BigTIFF is intentionally unsupported.
                long first = U32(head,4);
                if (first >= 8) queue.Enqueue(new Node(first,0));
                // IIQ stores a separate Phase One public directory after its TIFF header.
                if (basis == 0 && end >= 20) Phase(8);
                while (queue.Count > 0 && visited.Count < 128)
                {
                    Check(); Node node = queue.Dequeue();
                    if (node.Depth > 16 || node.Offset < 8 || !visited.Add(node.Offset)) continue;
                    byte[] countBytes;
                    try { countBytes = Meta(node.Offset,2); } catch (BudgetException) { continue; }
                    int count = U16(countBytes,0); if (count > 2048) continue;
                    byte[] entries;
                    try { entries = Meta(node.Offset+2,count*12+4); } catch (BudgetException) { continue; }
                    for (int i=0;i<count;i++)
                    {
                        Check(); int o=i*12; int tag=U16(entries,o), type=U16(entries,o+2); uint n=U32(entries,o+4), value=U32(entries,o+8);
                        if ((tag==0x9003 || tag==0x9004) && type==2 && n>=19 && n<=64)
                        {
                            byte[] date;
                            try { date=Meta(value,(int)n); } catch (BudgetException) { continue; }
                            DateTime parsed;
                            if (DateTime.TryParseExact(Encoding.ASCII.GetString(date,0,19),"yyyy:MM:dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed) && parsed.Year>=1900)
                            {
                                parsed=DateTime.SpecifyKind(parsed,DateTimeKind.Unspecified);
                                if(tag==0x9003) { Log("EXIF DateTimeOriginal tag=0x9003 IFD="+(basis+node.Offset)+" valueOffset="+(basis+value)+" value="+parsed.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)); return parsed; }
                                if(!Digitized.HasValue) Digitized=parsed;
                            }
                        }
                        if ((tag==0x8769 || tag==0x014a) && (type==4 || type==13) && n>=1 && n<=64)
                        {
                            if (n==1) queue.Enqueue(new Node(value,node.Depth+1));
                            else
                            {
                                byte[] pointers; try { pointers=Meta(value,(int)n*4); } catch(BudgetException) { continue; }
                                for(int j=0;j<n;j++) queue.Enqueue(new Node(U32(pointers,j*4),node.Depth+1));
                            }
                        }
                        if (tag==0x927c && (type==7 || type==1) && n>=12) Phase(value);
                    }
                    uint next=U32(entries,count*12); if(next>=8) queue.Enqueue(new Node(next,node.Depth+1));
                }
                if (phaseDate.HasValue) Log(phaseSource);
                return phaseDate;
            }

            private void Phase(long offset)
            {
                bool savedLittle=little;
                try
                {
                    byte[] h=Meta(offset,12);
                    bool isLittle=h[0]=='I' && h[1]=='I' && h[2]=='I' && h[3]=='I' && h[5]=='w' && h[6]=='a' && h[7]=='R';
                    bool isBig=h[0]=='M' && h[1]=='M' && h[2]=='M' && h[3]=='M' && h[4]=='R' && h[5]=='a' && h[6]=='w';
                    if(!isLittle && !isBig) return;
                    little=isLittle; long dir=offset+U32(h,8); byte[] c=Meta(dir,8); uint count=U32(c,0); if(count<2 || count>300) return;
                    byte[] entries=Meta(dir+8,(int)count*16);
                    for(int i=0;i<count;i++)
                    {
                        int o=i*16;
                        if(U32(entries,o)!=0x0112 || U32(entries,o+4)!=4 || U32(entries,o+8)!=4) continue;
                        uint seconds=U32(entries,o+12); if(seconds==0) continue;
                        phaseDate=DateTime.SpecifyKind(new DateTime(1970,1,1).AddSeconds(seconds),DateTimeKind.Unspecified);
                        phaseSource="PhaseOne DateTimeOriginal tag=0x0112 directory="+(basis+dir)+" valueOffset="+(basis+dir+8+o+12)+" unixSeconds="+seconds+" value="+phaseDate.Value.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
                    }
                }
                catch(BudgetException) { }
                finally { little=savedLittle; }
            }
        }
    }
}
