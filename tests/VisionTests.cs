using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DiscArchive;
class VisionTests {
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
 static void Main(string[] args) {

  if(args.Length>0 && args[0]=="timeout") {
   var timer=Stopwatch.StartNew();Check(LabelOcr.Read(args[1])=="","timeout returns blank");
   Check(timer.ElapsedMilliseconds>=19000 && timer.ElapsedMilliseconds<23000,"worker deadline "+timer.ElapsedMilliseconds+"ms");return;
  }
  string dir=AppDomain.CurrentDomain.BaseDirectory;
  string disc=Path.Combine(dir,"synthetic-disc.png"), blank=Path.Combine(dir,"room.png"), corrupt=Path.Combine(dir,"broken.jpg");
  using(Bitmap image=new Bitmap(1280,720))using(Graphics g=Graphics.FromImage(image))using(Font font=new Font("Arial",26,FontStyle.Bold)) {
   g.Clear(Color.FromArgb(30,30,30));
   // Distractor text outside the disc must never become a title.
   g.DrawString("BACKGROUND BOOKS",font,Brushes.White,5,30);
   image.Save(blank,ImageFormat.Png);
   Check(DiscVision.Locate(image)==null,"room without disc rejected");
   g.FillEllipse(Brushes.LightGray,400,60,600,600);g.FillEllipse(Brushes.Black,670,330,60,60);
   g.DrawString("FAMILY ALBUM",font,Brushes.Black,540,140);
   g.DrawString("SUMMER TRIP",font,Brushes.Black,540,490);
   double[] found=DiscVision.Locate(image);
   Check(found!=null && Math.Abs(found[0]-700)<20 && Math.Abs(found[1]-360)<20 && Math.Abs(found[2]-300)<20,"disc localisation independent of photo filenames");
   image.Save(disc,ImageFormat.Png);
  }
  Check(LabelOcr.Read(blank)=="","background text ignored");
  string read=LabelOcr.Read(disc);Console.WriteLine("Synthetic candidate: "+read);
  if(args.Length>0 && args[0]=="english")Check(read.Contains("FAMILY ALBUM") && read.Contains("SUMMER TRIP"),"printed disc OCR survives agreement filter");
  File.WriteAllText(corrupt,"invalid");Check(LabelOcr.Read(corrupt)=="","corrupt image blank");Check(LabelOcr.Read(null)=="","null path blank");
  Check(LabelOcr.Read(Path.Combine(dir,"missing.jpg"))=="","missing photo blank");
  Console.WriteLine("Method: "+LabelOcr.Method);
 }
}
