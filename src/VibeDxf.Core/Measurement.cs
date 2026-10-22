using System.Collections.ObjectModel;
namespace VibeDxf.Core;

public enum MeasurementStrategy { Closest, Center }
public enum MeasurementStatus { Success, GeometryNotPrepared, InvalidTarget, CoordinateMismatch, DisplayOnly }
public readonly record struct MeasurementAnchor(int EntityId,int? MidpointSegment=null);
public sealed record MeasurementResult(MeasurementStatus Status,ClosestPair? Pair=null,bool IsApproximate=false)
{
    public double? Distance=>Pair?.Distance;
    public double? DeltaX=>Pair is {} p?p.B.X-p.A.X:null;
    public double? DeltaY=>Pair is {} p?p.B.Y-p.A.Y:null;
}
/// <summary>Independent prepared geometry. Supplied outlines never mutate the source document.</summary>
public sealed class GeometrySnapshot
{
    private readonly IReadOnlyDictionary<int,(DxfEntity Source,DxfEntity Geometry,int Space)> entries;
    public GeometrySnapshot(DxfDocument document,IReadOnlyDictionary<int,DxfEntity>? preparedGeometry=null)
    {
        var map=new Dictionary<int,(DxfEntity,DxfEntity,int)>();
        void Add(IEnumerable<DxfEntity> entities,int space)
        {
            foreach(var e in entities)
            {
                var geometry=preparedGeometry!=null&&preparedGeometry.TryGetValue(e.Id,out var prepared)?prepared:e;
                if(geometry.Kind!=e.Kind)throw new ArgumentException("Prepared geometry must retain the source entity kind.");
                map.Add(e.Id,(e,geometry,space));
            }
        }
        Add(document.Entities,-1);
        for(int i=0;i<document.Layouts.Count;i++)Add(document.Layouts[i].Entities,i);
        if(preparedGeometry!=null&&preparedGeometry.Keys.Any(id=>!map.ContainsKey(id)))throw new ArgumentException("Prepared entity does not belong to the document.");
        entries=new ReadOnlyDictionary<int,(DxfEntity,DxfEntity,int)>(map);
    }
    public MeasurementResult Measure(MeasurementAnchor first,MeasurementAnchor second,
        MeasurementStrategy firstStrategy=MeasurementStrategy.Closest,MeasurementStrategy secondStrategy=MeasurementStrategy.Closest)
    {
        if(!entries.TryGetValue(first.EntityId,out var a)||!entries.TryGetValue(second.EntityId,out var b))return new(MeasurementStatus.InvalidTarget);
        if(a.Space!=b.Space)return new(MeasurementStatus.CoordinateMismatch);
        if(a.Source.IsAnnotation||b.Source.IsAnnotation)return new(MeasurementStatus.DisplayOnly);
        if(!a.Geometry.IsGeometryPrepared||!b.Geometry.IsGeometryPrepared)return new(MeasurementStatus.GeometryNotPrepared);
        if(a.Geometry.Curves.Count==0||b.Geometry.Curves.Count==0)return new(MeasurementStatus.InvalidTarget);
        bool Valid(DxfEntity e,int? midpoint)=>midpoint==null||(e.Kind is not ("TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF" or "HATCH" or "SPLINE" or "POINT")&&midpoint>=0&&midpoint<e.Curves.Count&&e.Curves[midpoint.Value].IsLine&&!e.Curves[midpoint.Value].IsUnbounded&&!e.IsApproximate&&e.Curves[midpoint.Value].Start!=e.Curves[midpoint.Value].End);
        if(!Valid(a.Geometry,first.MidpointSegment)||!Valid(b.Geometry,second.MidpointSegment))return new(MeasurementStatus.InvalidTarget);
        Point? Fixed(DxfEntity e,int? midpoint,MeasurementStrategy strategy)=>midpoint is int i?e.Curves[i].At(.5):
            e.Kind=="POINT"?e.Curves[0].Start:strategy==MeasurementStrategy.Center&&e.Kind is "CIRCLE" or "ARC"&&e.Curves.Count==1&&!e.Curves[0].IsLine?e.Curves[0].Center:null;
        var p=Fixed(a.Geometry,first.MidpointSegment,firstStrategy);var q=Fixed(b.Geometry,second.MidpointSegment,secondStrategy);
        var pair=p is {} x?(q is {} y?new ClosestPair(x,y):new ClosestPair(x,b.Geometry.Closest(x))):
            q is {} z?new ClosestPair(a.Geometry.Closest(z),z):a.Geometry.Distance(b.Geometry);
        return new(MeasurementStatus.Success,pair,a.Source.IsApproximate||b.Source.IsApproximate||a.Geometry.IsApproximate||b.Geometry.IsApproximate);
    }
}
