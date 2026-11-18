param([Parameter(Mandatory = $true)][string]$PhotoPath, [switch]$Diagnostics)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$watch = [Diagnostics.Stopwatch]::StartNew()
$image = $null
try {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
namespace DiscArchive {
 public static class DiscVision {
  public static void Orient(Bitmap image) {
   try {
    int orientation=BitConverter.ToUInt16(image.GetPropertyItem(0x112).Value,0);
    RotateFlipType[] modes={RotateFlipType.RotateNoneFlipNone,RotateFlipType.RotateNoneFlipNone,RotateFlipType.RotateNoneFlipX,RotateFlipType.Rotate180FlipNone,RotateFlipType.Rotate180FlipX,RotateFlipType.Rotate90FlipX,RotateFlipType.Rotate90FlipNone,RotateFlipType.Rotate270FlipX,RotateFlipType.Rotate270FlipNone};
    if(orientation>=1&&orientation<=8)image.RotateFlip(modes[orientation]);
   } catch(ArgumentException) { }
  }
  public static bool IsDocument(Bitmap source) {
   int total=0,light=0;
   for(int y=0;y<source.Height;y+=Math.Max(1,source.Height/60))for(int x=0;x<source.Width;x+=Math.Max(1,source.Width/80)) {
    Color c=source.GetPixel(x,y);total++;
    if(c.R>200&&c.G>200&&c.B>200)light++;
   }
   return total>0 && light>=total*.80;
  }
  public static double[] Locate(Bitmap source) {
   int w=320,h=Math.Max(80,(int)(source.Height*320.0/source.Width));
   if(h>400) return null;
   using(Bitmap small=new Bitmap(source,w,h)) {
    double[,] g=new double[w,h];
    for(int y=0;y<h;y++) for(int x=0;x<w;x++) {Color c=small.GetPixel(x,y);g[x,y]=c.R*.299+c.G*.587+c.B*.114;}
    double best=0;double[] found=null;
    for(int r=(int)(h*.25);r<h*.49;r+=2)
    for(int cy=(int)(h*.28);cy<h*.76;cy+=3)
    for(int cx=(int)(w*.25);cx<w*.82;cx+=3) {
     double score=0,body=0,hole=0;int n=0,strong=0;
     for(int a=0;a<48;a++) {
      double t=a*Math.PI/24,dx=Math.Cos(t),dy=Math.Sin(t);
      int ix=(int)(cx+dx*(r-3)),iy=(int)(cy+dy*(r-3)),ox=(int)(cx+dx*(r+3)),oy=(int)(cy+dy*(r+3));
      if(ix<0||ix>=w||iy<0||iy>=h||ox<0||ox>=w||oy<0||oy>=h)continue;
      double d=g[ix,iy]-g[ox,oy];score+=Math.Min(65,Math.Max(-25,d));if(d>9)strong++;n++;
      int bx=(int)(cx+dx*r*.55),by=(int)(cy+dy*r*.55),hx=(int)(cx+dx*r*.09),hy=(int)(cy+dy*r*.09);
      if(bx>=0&&bx<w&&by>=0&&by<h&&hx>=0&&hx<w&&hy>=0&&hy<h){body+=g[bx,by];hole+=g[hx,hy];}
     }
     if(n<40||strong<n*.52||body-hole<n*12)continue;
     score=score/n+(body-hole)/n*.10;
     if(score>best){best=score;found=new double[]{cx*source.Width/(double)w,cy*source.Height/(double)h,r*source.Width/(double)w,score};}
    }
    return best>=12?found:null;
   }
  }
  public static Bitmap Make(Bitmap source,double[] d,int band,bool enhance) {
   double r=d[2];Rectangle rect=r<0?new Rectangle(0,0,source.Width,source.Height):new Rectangle((int)(d[0]-.68*r),(int)(d[1]+(band==0?-.81:.30)*r),(int)(1.36*r),(int)(.46*r));
   rect.Intersect(new Rectangle(0,0,source.Width,source.Height));
   int width=Math.Min(2200,rect.Width*3),height=Math.Max(1,rect.Height*width/rect.Width);
   Bitmap result=new Bitmap(width,height,PixelFormat.Format24bppRgb);
   using(Graphics gr=Graphics.FromImage(result)){gr.InterpolationMode=InterpolationMode.HighQualityBicubic;gr.DrawImage(source,new Rectangle(0,0,width,height),rect,GraphicsUnit.Pixel);}
   if(enhance)using(Bitmap tiny=new Bitmap(result,Math.Max(1,width/24),Math.Max(1,height/24)))using(Bitmap back=new Bitmap(tiny,width,height)) {
    BitmapData data=result.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadWrite,PixelFormat.Format24bppRgb);
    byte[] bytes=new byte[data.Stride*height];System.Runtime.InteropServices.Marshal.Copy(data.Scan0,bytes,0,bytes.Length);
    for(int y=0;y<height;y++)for(int x=0;x<width;x++){
     int k=y*data.Stride+x*3;double v=.114*bytes[k]+.587*bytes[k+1]+.299*bytes[k+2];Color b=back.GetPixel(x,y);double bg=.114*b.B+.587*b.G+.299*b.R;
     byte value=(byte)Math.Max(0,Math.Min(255,245+(v-bg)*4));bytes[k]=bytes[k+1]=bytes[k+2]=value;
    }
    System.Runtime.InteropServices.Marshal.Copy(bytes,0,data.Scan0,bytes.Length);result.UnlockBits(data);
   }
   return result;
  }
 }
}
'@
    [void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
    [void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
    [void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
    [void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    [void][Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    function Await($operation,[Type]$type) {
        $task=$asTask.MakeGenericMethod($type).Invoke($null,@($operation))
        if(-not $task.Wait(3000)){throw 'OCR timeout'}
        return $task.Result
    }
    $langs=@([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages)
    $chosen=@($langs | Where-Object {$_.LanguageTag -like 'zh*'} | Select-Object -First 1) + @($langs | Where-Object {$_.LanguageTag -like 'en*'} | Select-Object -First 1)
    if($chosen.Count -eq 0){exit 0}
    $image=New-Object Drawing.Bitmap($PhotoPath)
    [DiscArchive.DiscVision]::Orient($image)
    $disc=[DiscArchive.DiscVision]::Locate($image)
    if($null -eq $disc -and [DiscArchive.DiscVision]::IsDocument($image)){$disc=@(0,0,-1,0)}
    if($null -eq $disc){if($Diagnostics){[Console]::Error.WriteLine('No confident disc boundary.')} ;exit 0}
    if($Diagnostics){[Console]::Error.WriteLine('Disc x,y,r,score: '+($disc -join ', '))}
    $accepted=New-Object 'System.Collections.Generic.List[string]'
    $bands=0,1; if($disc[2] -lt 0){$bands=@(0)}
    foreach($band in $bands) {
        $seen=@{}
        foreach($enhance in $false,$true) {
            $pass=@{}
            $crop=[DiscArchive.DiscVision]::Make($image,$disc,$band,$enhance)
            $memory=New-Object IO.MemoryStream
            $random=$null;$bitmap=$null
            try {
                $crop.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
                $memory.Position=0
                $random=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($memory)
                $decoder=Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($random)) ([Windows.Graphics.Imaging.BitmapDecoder])
                $bitmap=Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
                foreach($lang in $chosen) {
                    if($watch.ElapsedMilliseconds -gt 14000){break}
                    $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
                    $result=Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                    foreach($line in $result.Lines) {
                        $text=($line.Text -replace '(?<=[\u3400-\u9fff]) +(?=[\u3400-\u9fff])','').Trim()
                        if($Diagnostics){[Console]::Error.WriteLine(('band={0} enhanced={1} lang={2}: {3}' -f $band,$enhance,$lang.LanguageTag,$text))}
                        $key=($text -replace '[\s\p{P}]','').ToUpperInvariant()
                        if($key -match 'MITSUBISHI|DVD|CDR|VERBATIM|MAXELL|TDK|SPEED' -or $key -match '^[0-9]+$'){continue}
                        $han=[regex]::Matches($key,'[\u3400-\u9fff]').Count
                        if($han -lt 2 -and ($key -notmatch '[A-Z]{4}' -or $key.Length -lt 6)){continue}
                        $pass[$key]=$text
                    }
                }
            } finally {if($null -ne $bitmap){$bitmap.Dispose()};if($null -ne $random){$random.Dispose()};$memory.Dispose();$crop.Dispose()}
            foreach($key in $pass.Keys){if($seen.ContainsKey($key)){if(-not $accepted.Contains($pass[$key])){$accepted.Add($pass[$key])}}else{$seen[$key]=$pass[$key]}}
        }
    }
    [Console]::Write(($accepted -join [Environment]::NewLine))
} catch {if($Diagnostics){[Console]::Error.WriteLine($_.Exception.ToString())}} finally {if($null -ne $image){$image.Dispose()}}
