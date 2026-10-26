using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

internal static class DxfSavedViewReader
{
    internal static DxfModelView Read(string name,DxfModelViewSource source,IReadOnlyList<(int Code,string Value)> fields,bool named=false,bool paper=false)
    {
        double N(int code,double fallback=0)
        {
            var value=fields.FirstOrDefault(f=>f.Code==code).Value;
            if(value==null)return fallback;
            if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))throw new InvalidDataException("Nonfinite camera parameter.");
            return n;
        }
        int direction=named?11:16,target=named?12:17,center=named?10:12;
        var d=new Vector3D(N(direction),N(direction+10),N(direction+20,1));
        var t=new Point3D(N(target),N(target+10),N(target+20));
        var c=new Point(N(center),N(center+10));
        double h=N(paper?45:40,1),aspect=paper?N(40)/N(41):named?N(41,h)/h:N(41,1);
        long flags=(long)N(paper?90:71);
        if(d.Length<1e-20||!double.IsFinite(d.Length)||h<=0||aspect<=0||!double.IsFinite(aspect)||!double.IsFinite(h*aspect)||N(42,50)<=0)
            throw new InvalidDataException("Invalid camera framing or direction.");
        return new(name,source,d,t,c,h,aspect,N(named?50:51),(flags&1)!=0,N(42,50),(flags&2)!=0,(flags&4)!=0,(flags&16)==0,N(43),N(44));
    }
}
