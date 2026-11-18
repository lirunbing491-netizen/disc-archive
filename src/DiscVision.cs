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
