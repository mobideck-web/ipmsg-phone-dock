using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using IPMsg.Dock;
using QRCoder;

internal static class QuickControlChecks
{
    [STAThread] public static int Main()
    {
        try {
            string payload=QrPairForm.Payload("studio-"+new string('a',20),new string('b',32));
            using(var generator=new QRCodeGenerator())
            using(var data=generator.CreateQrCode(payload,QRCodeGenerator.ECCLevel.M))
            using(var qr=new QRCode(data))
            using(var bitmap=qr.GetGraphic(6)) {
                var decoded=new ZXing.BarcodeReader().Decode(bitmap);
                if(decoded==null || decoded.Text!=payload) throw new Exception("Independent QR decode did not match the pairing payload.");
            }
            Console.WriteLine("PASS: Actual QR bitmap independently decodes to the exact Android ADB pairing payload");
            try {QrPairForm.Payload("studio-other;P:injected",new string('b',32));throw new Exception("Injected service accepted");}catch(ArgumentException){}
            Console.WriteLine("PASS: Pairing payload rejects injected service delimiters");
            try {QrPairForm.Payload("studio-"+new string('a',20),"weak");throw new Exception("Weak secret accepted");}catch(ArgumentException){}
            Console.WriteLine("PASS: Pairing payload rejects malformed secrets");
            using(var bitmap=new Bitmap(34,34)) using(var graphics=Graphics.FromImage(bitmap))
                if(!LucideIcons.Draw(graphics,new Rectangle(0,0,34,34),"sound",Color.White)) throw new Exception("The embedded Lucide font did not load.");
            Console.WriteLine("PASS: The embedded offline Lucide font loads without a font installation");
            Rectangle dock=new Rectangle(1538,0,382,1040),anchor=new Rectangle(1780,8,34,32);
            Size popup=new Size(164,44);Point position=QuickFlyout.Placement(dock,anchor,popup);
            if(!dock.Contains(new Rectangle(position,popup)) || position.Y!=anchor.Bottom+5) throw new Exception("Hover bar escaped its dock or was not below the icon.");
            Console.WriteLine("PASS: Hover bar stays directly below its icon inside a right-pinned dock");
            var hashes=new HashSet<string>();
            foreach(string symbol in new[]{"layout","full","top","pin","collapse","sound","mute","display","plus","minus"}) {
                using(var button=new DockIconButton{Symbol=symbol,Size=new Size(34,34)})
                using(var bitmap=new Bitmap(34,34)) {
                    button.DrawToBitmap(bitmap,new Rectangle(0,0,34,34));string first=Digest(bitmap);
                    button.DrawToBitmap(bitmap,new Rectangle(0,0,34,34));
                    if(first!=Digest(bitmap))throw new Exception("Repeated icon paint accumulated pixels.");
                    if(!hashes.Add(first))throw new Exception("Two icon symbols rendered identically.");
                }
            }
            Console.WriteLine("PASS: Ten vector icons render distinctly and repeated repaint does not accumulate pixels");
            Console.WriteLine("RESULT: 6 checks passed; isolated QR and drawing fixtures only. No phone, pairing, visible UI or gaming measurement."); return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static string Digest(Bitmap bitmap) {using(var stream=new MemoryStream()){bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));}}
}
