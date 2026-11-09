using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IPMsg.Dock
{
    // Pinned Lucide 0.468.0 font, embedded locally. No font installation, webfont or HTTP request.
    internal static class LucideIcons
    {
        private static readonly PrivateFontCollection fonts = new PrivateFontCollection();
        private static IntPtr memory;
        private static FontFamily family;
        private static readonly Dictionary<string,char> glyphs = new Dictionary<string,char> {
            {"layout",'\ue12f'},{"full",'\ue115'},{"top",'\ue4f3'},{"pin",'\ue258'},
            {"collapse",'\ue436'},{"sound",'\ue1aa'},{"mute",'\ue1ab'},{"display",'\ue166'},
            {"plus",'\ue140'},{"minus",'\ue11f'},{"more",'\ue0ba'},{"close",'\ue1b1'},
            {"back",'\ue072'},{"home",'\ue07a'},{"camera",'\ue068'},{"clear",'\ue18d'},{"files",'\ue0d3'} };
        static LucideIcons()
        {
            try {
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("IPMsg.Dock.Lucide.ttf")) {
                    if(stream==null || stream.Length>2*1024*1024) return;
                    byte[] data=new byte[(int)stream.Length];int position=0,count;
                    while(position<data.Length && (count=stream.Read(data,position,data.Length-position))>0)position+=count;
                    if(position!=data.Length)return;
                    memory=Marshal.AllocHGlobal(data.Length);Marshal.Copy(data,0,memory,data.Length);
                    fonts.AddMemoryFont(memory,data.Length);family=fonts.Families[0];
                }
                System.Windows.Forms.Application.ApplicationExit += delegate { fonts.Dispose();if(memory!=IntPtr.Zero){Marshal.FreeHGlobal(memory);memory=IntPtr.Zero;} };
            } catch(Exception) { family=null; } // Simple vector fallback remains available.
        }
        public static bool Draw(Graphics graphics,Rectangle rectangle,string name,Color color)
        {
            char glyph;if(family==null || !glyphs.TryGetValue(name,out glyph))return false;
            var hint=graphics.TextRenderingHint;
            try {
                graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                using(var font=new Font(family,Math.Max(12,Math.Min(rectangle.Width,rectangle.Height)*.57f),FontStyle.Regular,GraphicsUnit.Pixel))
                using(var brush=new SolidBrush(color))
                using(var format=new StringFormat(StringFormat.GenericTypographic){Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
                    graphics.DrawString(glyph.ToString(),font,brush,rectangle,format);
                return true;
            } finally {graphics.TextRenderingHint=hint;}
        }
    }
}
