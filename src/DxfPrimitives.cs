using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace VibeDxf;
internal static class DxfPrimitives
{
 public static List<Curve> Outline(IEnumerable<Point[]> contours)=>VibeDxf.Core.DxfPrimitives.Outline(contours.Select(p=>p.Select(CoreDocumentAdapter.Core).ToArray())).Select(CoreDocumentAdapter.Wpf).ToList();
 public static List<Point> Triangulate(IEnumerable<Point[]> contours)=>VibeDxf.Core.DxfPrimitives.Triangulate(contours.Select(p=>p.Select(CoreDocumentAdapter.Core).ToArray())).Select(CoreDocumentAdapter.Wpf).ToList();
 public static string DecodeText(string text)=>VibeDxf.Core.DxfPrimitives.DecodeText(text);
    public static List<Point[]> Text(string value,double height,double width,double oblique,int flags,
        int horizontal,int vertical,Point first,Point second,double rotation)
    {
        if(height<=0||width<=0||Math.Abs(oblique)>=85||horizontal is <0 or >5||vertical is <0 or >3)
            throw new System.IO.InvalidDataException("Invalid text dimensions or alignment.");
        // DXF SHX fonts are not available to WPF. Use a consistent installed TrueType substitute.
        var typeface=new Typeface("Arial");
        var text=new FormattedText(DecodeText(value),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,100,Brushes.White,1);
        var geometry=text.BuildGeometry(default).GetFlattenedPathGeometry(0.02,ToleranceType.Absolute);
        double cap=typeface.TryGetGlyphTypeface(out var glyph)?glyph.CapsHeight*100:72;
        var contours=new List<Point[]>();
        foreach(var figure in geometry.Figures)
        {
            var pts=new List<Point>{figure.StartPoint};
            foreach(var segment in figure.Segments)
                if(segment is PolyLineSegment poly)pts.AddRange(poly.Points);
                else if(segment is LineSegment line)pts.Add(line.Point);
            if(pts.Count>2)contours.Add(pts.Select(p=>new Point(p.X*height/cap,(text.Baseline-p.Y)*height/cap)).ToArray());
        }
        if(contours.Count==0)return [];
        var advance=text.WidthIncludingTrailingWhitespace*height/cap;
        var sx=width;var sy=1d;
        if(horizontal is 3 or 5 && vertical==0)
        {
            var delta=second-first;
            if(advance<=0||delta.Length<=0)throw new System.IO.InvalidDataException("Invalid aligned text.");
            sx=delta.Length/advance;sy=horizontal==3?sx:1;
            rotation=Math.Atan2(delta.Y,delta.X)*180/Math.PI;
        }
        var bounds=contours.SelectMany(p=>p).Aggregate(Rect.Empty,(r,p)=>{r.Union(p);return r;});
        var dx=horizontal switch{1 or 4=>-advance/2,2=>-advance,_=>0};
        var dy=vertical switch{1=>-bounds.Top,2=>-(bounds.Top+bounds.Bottom)/2,3=>-bounds.Bottom,_=>0};
        if(horizontal==4){dy=-(bounds.Top+bounds.Bottom)/2;}
        var anchor=horizontal is 0 or 3 or 5 && vertical==0?first:second;
        var matrix=Matrix.Identity;matrix.Rotate(rotation);matrix.Translate(anchor.X,anchor.Y);
        var shear=Math.Tan(oblique*Math.PI/180);
        return contours.Select(p=>p.Select(v=>
        {
            var x=(v.X+dx)*sx;var y=(v.Y+dy)*sy;
            x+=shear*y;
            if((flags&2)!=0)x=-x;if((flags&4)!=0)y=-y;
            return matrix.Transform(new Point(x,y));
        }).ToArray()).ToList();
    }


 public static DxfEntity Transform(DxfEntity entity,Matrix matrix,string layer,string handle,EntityCategory? category=null,uint? color=null)=>
    CoreDocumentAdapter.Wpf(VibeDxf.Core.DxfPrimitives.Transform(CoreDocumentAdapter.Core(entity),CoreDocumentAdapter.Core(matrix),layer,handle,(VibeDxf.Core.EntityCategory?)category,color))!;
}
