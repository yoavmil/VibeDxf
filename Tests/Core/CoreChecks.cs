using System.Text;
using VibeDxf.Core;
using VibeDxf.Core.Geometry;
using Xunit;

public class CoreChecks
{
    private static DxfLoadResult Load(string entities)=>DxfReader.Load(new MemoryStream(Encoding.UTF8.GetBytes("0\nSECTION\n2\nENTITIES\n"+entities+"0\nENDSEC\n0\nEOF\n")));
    private static string Fixture(string file)=>Path.Combine(AppContext.BaseDirectory,"Fixtures",file);
    [Fact] public void CoreHasNoWindowsOrGraphicsReferences()
    {
        var references=typeof(DxfReader).Assembly.GetReferencedAssemblies().Select(a=>a.Name!).ToArray();
        Assert.DoesNotContain(references,n=>n.StartsWith("Presentation")||n.StartsWith("WindowsBase")||n.StartsWith("Vortice")||n.StartsWith("System.Windows"));
        Assert.NotEqual(ApartmentState.STA,Thread.CurrentThread.GetApartmentState());
    }
    [Fact] public void TransformOrderInverseAndMirroring()
    {
        var a=Affine2D.Identity;a.Translate(3,4);var b=Affine2D.Identity;b.Scale(-2,3);b.Rotate(90);
        var p=new Point2D(5,7);var combined=a.Then(b);var expected=b.Transform(a.Transform(p));
        Assert.Equal(expected.X,combined.Transform(p).X,10);Assert.Equal(expected.Y,combined.Transform(p).Y,10);
        combined.Invert();var restored=combined.Transform(expected);Assert.Equal(p.X,restored.X,10);Assert.Equal(p.Y,restored.Y,10);
        Assert.Throws<InvalidOperationException>(()=>{var singular=new Affine2D(0,0,0,0,0,0);singular.Invert();});
    }
    [Fact] public void EmptyBoundsAndRayFitAreFinite()
    {
        Assert.True(Bounds2D.Empty.IsEmpty);Assert.False(new Bounds2D(new(0,0),new(0,0)).IsEmpty);
        var ray=Curve.Ray(new(100,200),new(1,0));Assert.Equal(0,ray.Bounds.Width);Assert.True(ray.IsUnbounded);
        Assert.Equal(5,Curve.Distance(ray,Curve.Line(new(200,205),new(210,205))).Distance,10);
    }
    [Fact] public void GeometryCollectionsAreDefensiveCopies()
    {
        var curves=new List<Curve>{Curve.Line(new(0,0),new(1,0))};var polygon=new[]{new Point2D(0,0),new(1,0),new(0,1)};
        var e=new DxfEntity("SOLID","0","1",curves){FillContours=new[]{polygon}};
        curves.Clear();polygon[0]=new(999,999);Assert.Single(e.Curves);Assert.Equal(new(0,0),e.FillContours[0][0]);
        Assert.Throws<NotSupportedException>(()=>((IList<Curve>)e.Curves).Clear());
        var doc=Load("0\nLINE\n10\n0\n20\n0\n11\n1\n21\n1\n").Document;
        Assert.Throws<NotSupportedException>(()=>((IList<DxfEntity>)doc.Entities).Clear());
    }
    [Fact] public void TextOnlyLoadRetainsTextWithoutInventedBoundsOrDistances()
    {
        var loaded=Load("0\nTEXT\n5\nA1\n1\nHELLO\n10\n7\n20\n9\n40\n2\n");
        var e=Assert.Single(loaded.Document.Entities);var text=Assert.Single(e.DeferredText);
        Assert.Equal("HELLO",text.RawText);Assert.Equal(new(7,9),text.Insertion);Assert.True(loaded.Document.HasIncompleteBounds);
        Assert.True(e.Bounds.IsEmpty);Assert.Contains(loaded.Diagnostics,d=>d.Code=="GeometryNotPrepared"&&d.InstanceHandle=="A1");
        Assert.Equal(MeasurementStatus.GeometryNotPrepared,new GeometrySnapshot(loaded.Document).Measure(new(e.Id),new(e.Id)).Status);
    }
    [Fact] public void PreparedTextSnapshotCanBeMeasuredWithoutMutatingSource()
    {
        var doc=Load("0\nTEXT\n1\nX\n40\n1\n0\nLINE\n10\n5\n20\n0\n11\n5\n21\n2\n").Document;
        var text=doc.Entities[0];var geometry=new DxfEntity("TEXT","0","",[Curve.Line(new(0,0),new(0,2))]){IsApproximate=true};
        var snapshot=new GeometrySnapshot(doc,new Dictionary<int,DxfEntity>{{text.Id,geometry}});
        var measurement=snapshot.Measure(new(text.Id),new(doc.Entities[1].Id));Assert.Equal(MeasurementStatus.Success,measurement.Status);
        Assert.Equal(5,measurement.Distance);Assert.True(measurement.IsApproximate);Assert.False(text.IsGeometryPrepared);
    }
    [Fact] public void MultilineFormattingIsParsedWithoutFonts()
    {
        var doc=Load("0\nMTEXT\n1\nHello \\P{\\H2x;World}\\S1/2;\n40\n1\n41\n20\n71\n1\n").Document;
        var descriptor=Assert.Single(Assert.Single(doc.Entities).DeferredText);
        Assert.Contains(descriptor.Runs,r=>r.Break);Assert.Contains(descriptor.Runs,r=>r.Text=="World"&&r.Style.Height==2);
        Assert.Contains(descriptor.Runs,r=>r.Fraction=='/'&&r.Denominator=="2");
    }
    [Fact] public void StreamOwnershipAndCancellation()
    {
        var bytes=Encoding.UTF8.GetBytes("0\nSECTION\n2\nENTITIES\n0\nLINE\n10\n0\n11\n1\n0\nENDSEC\n0\nEOF\n");
        using var stream=new MemoryStream(bytes);DxfReader.Load(stream);Assert.True(stream.CanRead);
        var owned=new MemoryStream(bytes);DxfReader.Load(owned,false);Assert.False(owned.CanRead);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>DxfReader.Load(new MemoryStream(bytes),true,cancelled.Token));
        Assert.Throws<InvalidDataException>(()=>DxfReader.Load(new MemoryStream(Encoding.UTF8.GetBytes("0\n"))));
    }
    [Fact] public async Task AsyncLoadingUsesTheSameParser()
    {
        var result=await DxfReader.LoadAsync(Fixture("arc-highlight.dxf"));Assert.NotEmpty(result.Document.Entities);
    }
    [Theory]
    [InlineData("Public/api-cw750-details.dxf",4313,4)]
    [InlineData("Public/uncommon.dxf",148,4)]
    public void PublicFixturesRetainGeometryAndDeferredText(string file,int modelCount,int skipped)
    {
        var result=DxfReader.Load(Fixture(file));Assert.Equal(modelCount,result.Document.Entities.Count);
        Assert.Equal(skipped,result.Document.Skipped.Values.Sum());Assert.Contains(result.Document.Entities,e=>e.DeferredText.Count>0);
        Assert.Equal(result.Document.Entities.Count+result.Document.Layouts.Sum(l=>l.Entities.Count),result.Document.Entities.Concat(result.Document.Layouts.SelectMany(l=>l.Entities)).Select(e=>e.Id).Distinct().Count());
    }
    [Fact] public void ColorDataIsNotBrightenedByCore()
    {
        var doc=Load("0\nLINE\n420\n0\n10\n0\n11\n1\n").Document;var line=Assert.Single(doc.Entities);
        Assert.Equal(0xFF000000u,line.Color);Assert.Equal(0,line.ColorSource.TrueColor);
    }
    [Fact] public void CircleStrategiesAndMidpointsUpdateSignedComponents()
    {
        var doc=Load("0\nCIRCLE\n10\n0\n20\n0\n40\n2\n0\nCIRCLE\n10\n10\n20\n3\n40\n1\n").Document;
        var snapshot=new GeometrySnapshot(doc);var a=new MeasurementAnchor(doc.Entities[0].Id);var b=new MeasurementAnchor(doc.Entities[1].Id);
        var centers=snapshot.Measure(a,b,MeasurementStrategy.Center,MeasurementStrategy.Center);
        Assert.Equal(10,centers.DeltaX);Assert.Equal(3,centers.DeltaY);Assert.Equal(Math.Sqrt(109),centers.Distance);
        var closest=snapshot.Measure(a,b);Assert.Equal(Math.Sqrt(109)-3,closest.Distance!.Value,8);
        var reversed=snapshot.Measure(b,a,MeasurementStrategy.Center,MeasurementStrategy.Center);Assert.Equal(-10,reversed.DeltaX);
    }
    [Fact] public void CancellationDuringStreamReadStopsParsing()
    {
        using var cts=new CancellationTokenSource();
        using var stream=new CancellingStream(Encoding.UTF8.GetBytes("0\nLINE\n10\n0\n11\n1\n"),cts);
        Assert.Throws<OperationCanceledException>(()=>DxfReader.Load(stream,true,cts.Token));
        Assert.True(stream.CanRead);
    }
    private sealed class CancellingStream(byte[] bytes,CancellationTokenSource cts):MemoryStream(bytes)
    {
        public override int Read(byte[] buffer,int offset,int count){var n=base.Read(buffer,offset,count);cts.Cancel();return n;}
        public override int Read(Span<byte> buffer){var n=base.Read(buffer);cts.Cancel();return n;}
    }
    [Fact] public void MirroredBlockTextRetainsTheFullTransform()
    {
        const string drawing="0\nSECTION\n2\nBLOCKS\n0\nBLOCK\n2\nLABEL\n10\n1\n20\n2\n0\nTEXT\n5\nT\n1\nLabel\n10\n3\n20\n4\n40\n1\n0\nENDBLK\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nINSERT\n5\nI\n2\nLABEL\n10\n10\n20\n20\n41\n-2\n42\n3\n50\n90\n0\nENDSEC\n0\nEOF\n";
        var result=DxfReader.Load(new MemoryStream(Encoding.UTF8.GetBytes(drawing)));
        var e=Assert.Single(result.Document.Entities);var descriptor=Assert.Single(e.DeferredText);
        Assert.Equal("I[0,0]/T",e.Handle);Assert.Equal("I[0,0]/T",e.InstancePath);
        var placed=descriptor.Transform.Transform(descriptor.Insertion);
        Assert.Equal(4,placed.X,10);Assert.Equal(16,placed.Y,10);Assert.True(descriptor.Transform.Determinant<0);
    }
    [Fact] public void EmbeddedMtextDirectionOverridesMetadataAlignmentPoint()
    {
        var doc=Load("0\nMTEXT\n1\nLabel\n10\n0\n20\n0\n40\n1\n101\nEmbedded Object\n10\n1\n20\n0\n11\n123\n21\n456\n72\n1\n").Document;
        var d=Assert.Single(Assert.Single(doc.Entities).DeferredText);
        Assert.Equal(1,d.Direction.X);Assert.Equal(0,d.Direction.Y);
        Assert.DoesNotContain(d.Tags,t=>t.Code==11&&t.Value=="0");
    }
    [Fact] public void MissingGeometryAndMixedSpacesNeverProduceADistance()
    {
        var doc=DxfReader.Load(Fixture("layout.dxf")).Document;var snapshot=new GeometrySnapshot(doc);
        Assert.Equal(MeasurementStatus.InvalidTarget,snapshot.Measure(new(-999),new(-998)).Status);
        var model=doc.Entities.First(e=>!e.IsAnnotation);var paper=doc.Layouts.SelectMany(l=>l.Entities).First(e=>!e.IsAnnotation);
        Assert.Equal(MeasurementStatus.CoordinateMismatch,snapshot.Measure(new(model.Id),new(paper.Id)).Status);
    }
    [Fact] public void MidpointTargetsAreExactAndDoNotAcceptRayOrArcSegments()
    {
        var doc=Load("0\nLINE\n10\n0\n20\n0\n11\n10\n21\n0\n0\nLINE\n10\n0\n20\n4\n11\n10\n21\n4\n0\nRAY\n10\n0\n20\n0\n11\n1\n21\n0\n").Document;
        var snapshot=new GeometrySnapshot(doc);
        var pair=snapshot.Measure(new(doc.Entities[0].Id,0),new(doc.Entities[1].Id,0));
        Assert.Equal(4,pair.Distance);Assert.Equal(new Point2D(5,0),pair.Pair!.Value.A);Assert.Equal(0,pair.DeltaX);
        Assert.Equal(MeasurementStatus.InvalidTarget,snapshot.Measure(new(doc.Entities[2].Id,0),new(doc.Entities[1].Id)).Status);
    }
    [Fact] public void DoublePrecisionAndLargeOriginPreserveSmallDistances()
    {
        var a=Curve.Line(new(1e12,1e12),new(1e12+100,1e12));var b=Curve.Line(new(1e12,1e12+.125),new(1e12+100,1e12+.125));
        Assert.Equal(.125,Curve.Distance(a,b).Distance);
    }
    [Fact] public void CorePaperCountsIncludeBlankTextRecordsUntilPreparation()
    {
        var doc=DxfReader.Load(Fixture("Public/api-cw750-details.dxf")).Document;
        Assert.Equal(new[]{64,76,77,63},doc.Layouts.Select(l=>l.Entities.Count));
        Assert.Equal(new[]{2,1,2,2},doc.Layouts.Select(l=>l.Viewports.Count));
        Assert.Equal(478,doc.Entities.Count(e=>!e.IsGeometryPrepared));
    }
    [Fact] public void DeferredTextAndViewportListsCannotBeMutated()
    {
        var doc=DxfReader.Load(Fixture("Public/api-cw750-details.dxf")).Document;
        var e=doc.Entities.First(e=>e.DeferredText.Count>0);var d=e.DeferredText[0];
        Assert.Throws<NotSupportedException>(()=>((IList<DxfTextDescriptor>)e.DeferredText).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<(int Code,string Value)>)d.Tags).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<DxfViewportWindow>)doc.Layouts[0].Viewports).Clear());
    }
}
