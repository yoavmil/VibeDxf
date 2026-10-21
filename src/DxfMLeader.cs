using System.Globalization;
using System.IO;
using System.Windows;

namespace VibeDxf;

// MULTILEADER stores its rendered state in CONTEXT_DATA: leader polylines, landing/dogleg and MTEXT.
// Construction follows ezdxf's render engine. The result is one display-only entity.
internal static class DxfMLeader
{
    public static DxfEntity Read(List<(int Code,string Value)> tags,string layer,string handle,Action<string>? warn=null)
    {
        int open=tags.FindIndex(t=>t.Code==300&&t.Value.Trim()=="CONTEXT_DATA{");
        int close=tags.FindLastIndex(t=>t.Code==301&&t.Value.Trim()=="}");
        if(open<0||close<open)throw new InvalidDataException("Missing MULTILEADER context data.");
        var context=tags.GetRange(open+1,close-open-1);var entity=tags.Skip(close+1).ToList();
        static double Parse(string value)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:
            throw new InvalidDataException("Invalid MULTILEADER number.");
        static double Get(List<(int Code,string Value)> list,int code,double fallback=0)=>
            list.FirstOrDefault(t=>t.Code==code).Value is {} value?Parse(value):fallback;
        int firstLeader=context.FindIndex(t=>t.Code==302);
        var head=firstLeader<0?context:context.Take(firstLeader).ToList();
        // A non-Z content plane would need general 3D projection.
        var xAxis=new Vector(Get(head,111,1),Get(head,121));var yAxis=new Vector(Get(head,112),Get(head,122,1));
        if(Math.Abs(Get(head,131))>1e-9||Math.Abs(Get(head,132))>1e-9||Math.Abs(Vector.CrossProduct(xAxis,yAxis))<1-1e-6)
            throw new NotSupportedException("MULTILEADER (3D orientation)");

        var leaderType=(int)Get(entity,170,1);var contentType=(int)Get(entity,172,2);
        if(leaderType==2)warn?.Invoke("MULTILEADER spline leaders are shown as straight segments");
        if(entity.Any(t=>t.Code is 342 or 345&&t.Value.Trim()!="0"))warn?.Invoke("MULTILEADER custom arrowheads are shown as closed filled arrows");
        if(contentType==1||Get(head,296)!=0)warn?.Invoke("MULTILEADER block content is not displayed");
        var arrowSize=Get(head,140,Get(entity,42,0));var hasDogleg=Get(entity,291,1)!=0;
        var curves=new List<Curve>();var fills=new List<Point[]>();
        for(int i=firstLeader;i>=0&&i<context.Count;i=context.FindIndex(i+1,t=>t.Code==302))
        {
            int end=context.FindIndex(i+1,t=>t.Code==303);if(end<0)throw new InvalidDataException("Unterminated MULTILEADER leader.");
            var leader=context.GetRange(i+1,end-i-1);
            int firstLine=leader.FindIndex(t=>t.Code==304);
            var props=firstLine<0?leader:leader.Take(firstLine).ToList();
            var last=new Point(Get(props,10),Get(props,20));
            var dogleg=new Vector(Get(props,11),Get(props,21));var doglegLength=Get(props,40);
            if(dogleg.Length<1e-12)dogleg=new Vector(1,0);dogleg.Normalize();
            for(int j=firstLine;j>=0&&j<leader.Count;j=leader.FindIndex(j+1,t=>t.Code==304))
            {
                int lineEnd=leader.FindIndex(j+1,t=>t.Code==305);if(lineEnd<0)throw new InvalidDataException("Unterminated MULTILEADER line.");
                var vertices=new List<Point>();
                for(int k=j+1;k<lineEnd;k++)
                    if(leader[k].Code==10&&k+1<lineEnd&&leader[k+1].Code==20)vertices.Add(new(Parse(leader[k].Value),Parse(leader[k+1].Value)));
                vertices.Add(last);
                if(hasDogleg&&Get(props,291,1)!=0&&doglegLength>0)vertices.Add(last+dogleg*doglegLength);
                if(leaderType==0||vertices.Count<2)continue;
                // The closed filled arrow covers the first arrowSize of the leader; the line starts at its base.
                var tip=vertices[0];var axis=vertices[1]-tip;
                if(arrowSize>0&&axis.Length>2*arrowSize)
                {
                    axis.Normalize();var normal=new Vector(-axis.Y,axis.X)*arrowSize/6;var foot=tip+axis*arrowSize;
                    fills.Add([tip,foot+normal,foot-normal]);vertices[0]=foot;
                }
                for(int k=0;k+1<vertices.Count;k++)if(vertices[k]!=vertices[k+1])curves.Add(Curve.Line(vertices[k],vertices[k+1]));
            }
            i=end;
        }
        string? text=null;
        if(contentType==2&&Get(head,290)!=0&&head.FirstOrDefault(t=>t.Code==304).Value is {Length:>0} content)
        {
            // ezdxf: MTEXT alignment 1/2/3 maps directly to attachment top left/center/right; rotation is ignored.
            var mtext=new List<(int Code,string Value)>{(10,V(Get(head,12))),(20,V(Get(head,22))),(40,V(Get(head,41))),
                (41,V(Get(head,43)*Get(head,40,1))),(71,V(Math.Clamp(Get(head,171,1),1,3))),(11,V(Get(head,13,1))),(21,V(Get(head,23))),
                (230,V(Get(head,31,1)<0?-1:1)),(1,content)};
            var layout=DxfMText.Read(mtext,layer,handle,warn).Entity;
            curves.AddRange(layout.Curves);fills.AddRange(layout.FillContours);text=layout.Text;
        }
        if(curves.Count==0)throw new NotSupportedException("MULTILEADER (no displayable content)");
        return new DxfEntity("MULTILEADER",layer,handle,curves){FillContours=fills,Text=text,IsApproximate=text!=null,Category=EntityCategory.Multileader};
    }
    private static string V(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
}
