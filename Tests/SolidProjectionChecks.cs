using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class SolidProjectionChecks
{
    private static string TriangleSat => string.Join('\n', new[]
    {
        "700 0 1 0", "4 test 4 ACIS 4 date", "1 1e-6 1e-10",
        "body $-1 -1 $-1 $1 $-1 $21 #",
        "lump $-1 -1 $-1 $-1 $2 $0 #",
        "shell $-1 -1 $-1 $-1 $-1 $3 $-1 $1 #",
        "face $-1 -1 $-1 $-1 $4 $2 $-1 $20 forward single #",
        "loop $-1 -1 $-1 $-1 $5 $3 #",
        "coedge $-1 -1 $-1 $6 $7 $-1 $8 forward $4 $-1 #",
        "coedge $-1 -1 $-1 $7 $5 $-1 $9 forward $4 $-1 #",
        "coedge $-1 -1 $-1 $5 $6 $-1 $10 forward $4 $-1 #",
        "edge $-1 -1 $-1 $11 0 $12 5 $5 $17 forward @7 unknown #",
        "edge $-1 -1 $-1 $12 0 $13 4 $6 $18 forward @7 unknown #",
        $"edge $-1 -1 $-1 $13 0 $11 {Math.Sqrt(41).ToString("R",CultureInfo.InvariantCulture)} $7 $19 forward @7 unknown #",
        "vertex $-1 -1 $-1 $8 $14 #", "vertex $-1 -1 $-1 $8 $15 #", "vertex $-1 -1 $-1 $9 $16 #",
        "point $-1 -1 $-1 0 0 0 #", "point $-1 -1 $-1 3 0 4 #", "point $-1 -1 $-1 3 4 4 #",
        "straight-curve $-1 -1 $-1 0 0 0 0.6 0 0.8 I I #",
        "straight-curve $-1 -1 $-1 3 0 4 0 1 0 I I #",
        $"straight-curve $-1 -1 $-1 3 4 4 {(-3/Math.Sqrt(41)).ToString("R",CultureInfo.InvariantCulture)} {(-4/Math.Sqrt(41)).ToString("R",CultureInfo.InvariantCulture)} {(-4/Math.Sqrt(41)).ToString("R",CultureInfo.InvariantCulture)} I I #",
        "plane-surface $-1 -1 $-1 0 0 0 -0.8 0 0.6 0.6 0 0.8 forward I I I I #",
        "transform $-1 -1 0 0 -1 0 1 0 1 0 0 10 20 30 1 rotate no_reflect no_shear #",
        "End-of-ACIS-data"
    });

    private static string Inline(string sat, bool block=false)
    {
        string solid="0\n3DSOLID\n5\nS\n8\n0\n100\nAcDbModelerGeometry\n70\n1\n"+
            string.Join('\n',sat.Split('\n').Select(l=>"1\n"+l))+"\n100\nAcDb3dSolid\n";
        if(!block)return "0\nSECTION\n2\nENTITIES\n"+solid+"0\nLINE\n5\nBASE\n10\n0\n20\n0\n11\n1\n21\n0\n0\nENDSEC\n0\nEOF\n";
        return "0\nSECTION\n2\nBLOCKS\n0\nBLOCK\n2\nB\n"+solid+"0\nENDBLK\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nINSERT\n5\nI\n8\nPLACED\n2\nB\n10\n100\n20\n200\n41\n2\n42\n3\n62\n1\n0\nENDSEC\n0\nEOF\n";
    }
    private static DxfDocument Load(string dxf)
    {
        using var source=new MemoryStream(Encoding.UTF8.GetBytes(dxf));return DxfReader.Load(source);
    }
    private static IReadOnlyList<(int Code,string Value)> SampleSab()
    {
        var lines=File.ReadAllLines(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        var fields=new List<(int Code,string Value)>();bool inRecord=false,match=false,asm=false;
        for(int i=0;i+1<lines.Length;i+=2)
        {
            int code=int.Parse(lines[i].Trim(),CultureInfo.InvariantCulture);string value=lines[i+1].Trim();
            if(code==0)
            {
                if(inRecord&&match&&fields.Count>0)return fields;
                inRecord=value=="ACDSRECORD";match=false;asm=false;fields=[];
            }
            else if(inRecord)
            {
                if(code==320&&value=="622")match=true;
                if(code==2)asm=value=="ASM_Data";
                else if(asm&&code is 94 or 310)fields.Add((code,value));
            }
        }
        throw new InvalidDataException("Sample solid payload missing.");
    }
    private static byte[] SabBytes()=>Convert.FromHexString(string.Concat(SampleSab().Where(f=>f.Code==310).Select(f=>f.Value)));
    private static IReadOnlyList<(int Code,string Value)> Binary(byte[] data)=>new (int,string)[]{(94,data.Length.ToString(CultureInfo.InvariantCulture)),(310,Convert.ToHexString(data))};
    private static string BinaryDxf(byte[] data,bool duplicate=false)
    {
        string record="0\nACDSRECORD\n90\n0\n2\nAcDbDs::ID\n280\n10\n320\nS\n2\nASM_Data\n280\n15\n94\n"+data.Length+"\n310\n"+Convert.ToHexString(data)+"\n";
        return "0\nSECTION\n2\nENTITIES\n0\n3DSOLID\n5\nS\n8\n0\n100\nAcDbModelerGeometry\n290\n1\n100\nAcDb3dSolid\n0\nLINE\n5\nBASE\n10\n0\n20\n0\n11\n1\n21\n0\n0\nENDSEC\n0\nSECTION\n2\nACDSDATA\n"+record+(duplicate?record:"")+"0\nENDSEC\n0\nEOF\n";
    }

    [Fact]
    public void SatBodyTransformIsAppliedBeforeProjection()=>TestSupport.Run("3DSOLID SAT body transform before XY projection and boundary distances",()=>
    {
        var doc=Load(Inline(TriangleSat));Assert.Empty(doc.Skipped);
        var solid=doc.Entities.Single(e=>e.Kind=="3DSOLID");Assert.Equal(3,solid.Curves.Count);
        Assert.Equal(new Rect(10,20,4,4),solid.Bounds);Assert.False(solid.IsApproximate);
        Assert.Empty(solid.FillContours);Assert.False(solid.Contains(new(13,21)));
        var other=new DxfEntity("POINT","0","",[Curve.Line(new(15,22),new(15,22))]);
        TestSupport.Near(1,solid.Distance(other).Distance);
        Assert.Contains(doc.RenderingWarnings.Keys,k=>k.Contains("projected, not 3D"));
        Assert.NotNull(new MidpointIndex([solid]).Find(new(14,22),1e-9));
    });

    [Fact]
    public void SatEncryptedAndChunkedPayload()=>TestSupport.Run("3DSOLID AutoCAD encoded SAT with continuation chunks",()=>
    {
        string Encode(string line)
        {
            var b=new StringBuilder();foreach(char c in line)
            {
                b.Append(c switch{' '=>' ','_'=>'@','@'=>'_',>='A' and <='^'=>(char)(159-c),_=>(char)(c^95)});
                if(c=='A')b.Append(' ');
            }return b.ToString();
        }
        string data=Inline(string.Join('\n',TriangleSat.Split('\n').Select(Encode)));
        // Split a modeler line into the DXF continuation field without adding a space.
        string encoded=Encode("body $-1 -1 $-1 $1 $-1 $21 #");
        data=data.Replace("1\n"+encoded+"\n","1\n"+encoded[..7]+"\n3\n"+encoded[7..]+"\n");
        var doc=Load(data);Assert.Empty(doc.Skipped);Assert.Equal(new Rect(10,20,4,4),doc.Entities.Single(e=>e.Kind=="3DSOLID").Bounds);
    });

    [Fact]
    public void SatBlockPlacementAndColor()=>TestSupport.Run("3DSOLID block placement preserves layer, color and projected geometry",()=>
    {
        var entity=Assert.Single(Load(Inline(TriangleSat,true)).Entities);
        Assert.Equal("3DSOLID",entity.Kind);Assert.Equal("PLACED",entity.Layer);Assert.StartsWith("I[0,0]/",entity.Handle);
        Assert.Equal(new Rect(120,260,8,12),entity.Bounds);
    });

    [Theory]
    [InlineData("pointer")][InlineData("nonfinite")][InlineData("truncated")][InlineData("curve")][InlineData("topology")][InlineData("offcurve")]
    [InlineData("record-count")][InlineData("body-count")]
    public void InvalidSatIsNeverPartiallyDrawn(string kind)=>TestSupport.Run("3DSOLID invalid SAT: "+kind,()=>
    {
        string data=kind switch
        {
            "pointer"=>TriangleSat.Replace("$11 0 $12 5","$999999 0 $12 5"),
            "nonfinite"=>TriangleSat.Replace("3 0 4 #","NaN 0 4 #"),
            "truncated"=>TriangleSat.Replace("End-of-ACIS-data",""),
            "curve"=>TriangleSat.Replace("straight-curve","ellipse-curve"),
            "topology"=>TriangleSat.Replace("$-1 $1 $-1 $21 #","$-1 $8 $-1 $21 #"),
            "record-count"=>TriangleSat.Replace("700 0 1 0","700 999999999 1 0"),
            "body-count"=>TriangleSat.Replace("700 0 1 0","700 0 2 0"),
            _=>TriangleSat.Replace("point $-1 -1 $-1 3 0 4 #","point $-1 -1 $-1 300 0 4 #")
        };
        var doc=Load(Inline(data));Assert.DoesNotContain(doc.Entities,e=>e.Kind=="3DSOLID");Assert.Single(doc.Skipped);
        Assert.StartsWith("3DSOLID (",Assert.Single(doc.Skipped).Key);
    });

    [Fact]
    public void SabPayloadFromUncommonLoads()=>TestSupport.Run("3DSOLID SAB uncommon payload body transform, projected edges and markers",()=>
    {
        var projected=DxfSolidProjection.Read([],SampleSab());Assert.NotEmpty(projected);Assert.All(projected,c=>Assert.True(c.IsLine));
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        var solid=Assert.Single(doc.Entities,e=>e.Kind=="3DSOLID");Assert.Equal("622",solid.SourceHandle);
        Assert.Equal(projected.Count,solid.Curves.Count);Assert.NotEmpty(solid.RenderDots);
        Assert.DoesNotContain(doc.Skipped.Keys,k=>k.StartsWith("3DSOLID"));
    });

    [Theory]
    [InlineData("truncated")][InlineData("signature")][InlineData("count")][InlineData("version")][InlineData("nan")][InlineData("pointer")]
    public void InvalidSabIsSkipped(string kind)=>TestSupport.Run("3DSOLID invalid SAB: "+kind,()=>
    {
        var data=SabBytes();
        int Find(byte[] pattern)
        {
            for(int i=0;i+pattern.Length<=data.Length;i++)if(data.AsSpan(i,pattern.Length).SequenceEqual(pattern))return i;
            throw new InvalidDataException("Test marker not found.");
        }
        switch(kind)
        {
            case "truncated":data=data[..^10];break;
            case "signature":data[0]=0;break;
            case "count":BitConverter.GetBytes(123).CopyTo(data,19);break;
            case "version":BitConverter.GetBytes(1).CopyTo(data,15);break;
            case "nan":
                int point=Find(Convert.FromHexString("0D05706F696E740CFFFFFFFF04FFFFFFFF0CFFFFFFFF13"));
                BitConverter.GetBytes(double.NaN).CopyTo(data,point+22);break;
            case "pointer":
                int edge=Find(Convert.FromHexString("0D04656467650C"));
                BitConverter.GetBytes(999999).CopyTo(data,edge+22);break;
        }
        var doc=Load(BinaryDxf(data));Assert.DoesNotContain(doc.Entities,e=>e.Kind=="3DSOLID");Assert.Single(doc.Skipped);
    });

    [Fact]
    public void DuplicatePayloadAndWrongSizeAreRejected()=>TestSupport.Run("3DSOLID duplicate owners and incorrect binary payload size",()=>
    {
        Assert.DoesNotContain(Load(BinaryDxf(SabBytes(),true)).Entities,e=>e.Kind=="3DSOLID");
        var fields=Binary(SabBytes()).ToArray();fields[0]=(94,"1");
        Assert.Throws<InvalidDataException>(()=>DxfSolidProjection.Read([],fields));
    });

    [Fact]
    public void UnreachableGeometryIsNotProjected()=>TestSupport.Run("3DSOLID ignores unreachable ACIS geometry records",()=>
    {
        string orphan="point $-1 -1 $-1 999 999 999 #\nstraight-curve $-1 -1 $-1 999 999 999 1 0 0 I I #\n";
        var doc=Load(Inline(TriangleSat.Replace("End-of-ACIS-data",orphan+"End-of-ACIS-data")));
        Assert.Empty(doc.Skipped);Assert.Equal(new Rect(10,20,4,4),Assert.Single(doc.Entities,e=>e.Kind=="3DSOLID").Bounds);
    });

    [Fact]
    public void DecoderHonorsCancellation()=>TestSupport.Run("3DSOLID decoding honors loader cancellation",()=>
    {
        using var cts=new CancellationTokenSource();cts.Cancel();LoadingContext.Token=cts.Token;
        try{Assert.Throws<OperationCanceledException>(()=>DxfSolidProjection.Read([],SampleSab()));}
        finally{LoadingContext.Token=default;}
    });
}
