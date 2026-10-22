using System.Globalization;
namespace VibeDxf.Core;
public enum DxfTextKind { SingleLine, Multiline }
public sealed record DxfTextDescriptor
{
 public required DxfTextKind Kind{get;init;}
 public required string RawText{get;init;}
 public required IReadOnlyList<(int Code,string Value)> Tags{get;init;}
 public IReadOnlyList<DxfTextRun> Runs{get;init;}=[];
 public Matrix Transform{get;init;}=Matrix.Identity;
 public Point Insertion{get;init;} public Point AlignmentPoint{get;init;}
 public Vector Direction{get;init;}=new(1,0);
 public double ObliqueDegrees{get;init;} public int GenerationFlags{get;init;}
 public int HorizontalAlignment{get;init;} public int VerticalAlignment{get;init;}
 public double LineSpacingFactor{get;init;}=1;
 public int LineSpacingStyle{get;init;}=1;
 public double Height{get;init;} public double Width{get;init;} public double RotationDegrees{get;init;}
 public int Attachment{get;init;} public string FontStyleName{get;init;}="STANDARD";
 internal static DxfTextDescriptor FromTags(List<(int Code,string Value)> source,DxfTextKind kind)
 {
  var marker=source.FindIndex(t=>t.Code is 101 or 1001);var tags=marker<0?source:source.Take(marker).ToList();
  double N(int code,double fallback=0)
  {
   if(tags.FirstOrDefault(t=>t.Code==code).Value is not {} v)return fallback;
   if(!double.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))throw new System.IO.InvalidDataException("Invalid text number.");
   return n;
  }
  if(kind==DxfTextKind.SingleLine && (N(40)<=0||N(41,1)<=0||Math.Abs(N(51))>=85||N(72) is <0 or >5||N(source.Any(t=>t.Code==74)?74:73) is <0 or >3))
   throw new System.IO.InvalidDataException("Invalid text dimensions or alignment.");
  return new(){Kind=kind,RawText=string.Concat(tags.Where(t=>t.Code is 1 or 3).Select(t=>t.Value)),Tags=Array.AsReadOnly(source.ToArray()),
   Insertion=new(N(10),N(20)),AlignmentPoint=new(N(11),N(21)),Height=N(40),Width=N(41,kind==DxfTextKind.SingleLine?1:0),
   ObliqueDegrees=N(51),GenerationFlags=(int)N(71),HorizontalAlignment=(int)N(72),VerticalAlignment=(int)N(source.Any(t=>t.Code==74)?74:73),
   Direction=new(Math.Cos(N(50)*Math.PI/180),Math.Sin(N(50)*Math.PI/180)),LineSpacingFactor=N(44,1),LineSpacingStyle=(int)N(73,1),
   RotationDegrees=N(50),Attachment=(int)N(71,1),FontStyleName=tags.FirstOrDefault(t=>t.Code==7).Value??"STANDARD"};
 }
}
internal static class DxfMText
{
 internal sealed record Layout(DxfEntity Entity);
 public static Layout Read(List<(int Code,string Value)> tags,string layer,string handle,Action<string>? warn=null)
 {
        var original=tags;
        // R2018 column metadata reuses the entity's group codes with different meanings:
        // embedded 10/20 is a direction, while 11/21 is a repeated insertion point.
        int marker=tags.FindIndex(t=>t.Code is 101 or 1001);
        List<(int Code,string Value)> embedded=[];
        if(marker>=0)
        {
            if(tags[marker].Code==101)embedded=tags.Skip(marker+1).TakeWhile(t=>t.Code is not (101 or 1001)).ToList();
            tags=tags.Take(marker).ToList();
        }
        double E(int code,double fallback=0)=>embedded.FirstOrDefault(t=>t.Code==code).Value is {} value?
            double.Parse(value,CultureInfo.InvariantCulture):fallback;
        double N(int code,double fallback=0)
        {
            var tag=tags.FirstOrDefault(t=>t.Code==code);
            if(tag.Value==null)return fallback;
            if(!double.TryParse(tag.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))
                throw new System.IO.InvalidDataException("Invalid MTEXT number.");
            return value;
        }
        if(N(75)!=0||N(76)>1||E(72)>1)throw new NotSupportedException("MTEXT (columns)");
        if(N(72,1)==3)throw new NotSupportedException("MTEXT (vertical flow)");
        if(Math.Abs(N(210))>1e-9||Math.Abs(N(220))>1e-9||Math.Abs(Math.Abs(N(230,1))-1)>1e-9||Math.Abs(N(31))>1e-9)
            throw new NotSupportedException("MTEXT (3D orientation)");
        if(N(90)!=0)warn?.Invoke("MTEXT background mask/frame is not displayed");
        var height=N(40);var wrap=N(41);var attach=(int)N(71,1);var factor=N(44,1);var spacing=(int)N(73,1);
        if(height<=0||wrap<0||attach is <1 or >9||factor is <.25 or >4||spacing is <1 or >2)
            throw new System.IO.InvalidDataException("Invalid MTEXT layout dimensions.");
        var raw=string.Concat(tags.Where(t=>t.Code is 3 or 1).Select(t=>t.Value));
        if(raw.Length>100000)throw new NotSupportedException("MTEXT (text length limit)");
        var direction=new Vector(Math.Cos(N(50)*Math.PI/180),Math.Sin(N(50)*Math.PI/180));
        if(tags.Any(t=>t.Code==11))direction=new(N(11),N(21));
        else if(!tags.Any(t=>t.Code==50)&&embedded.Any(t=>t.Code==10))
        {
            if(Math.Abs(E(30))>1e-9)throw new NotSupportedException("MTEXT (3D orientation)");
            direction=new(E(10),E(20));
        }
        if(!double.IsFinite(direction.X)||!double.IsFinite(direction.Y)||direction.Length<1e-12)throw new System.IO.InvalidDataException("Invalid MTEXT direction.");
        direction.Normalize();
        var runs=DxfTextFormatting.Parse(raw,height,warn);
        var descriptor=DxfTextDescriptor.FromTags(original,DxfTextKind.Multiline) with { Runs=Array.AsReadOnly(runs.ToArray()),Direction=direction };
        return new(new DxfEntity("MTEXT",layer,handle,[]){Text=raw,DeferredText=[descriptor],IsApproximate=true});
 }
}
