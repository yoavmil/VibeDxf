using VibeDxf;
using Xunit;
using Xunit.Abstractions;

[Collection("Visual checks")]
public sealed class XunitChecks(ITestOutputHelper output)
{
 public static IEnumerable<object[]> Cases => CheckCatalog.Entries.Select(e=>new object[]{e[0],e[1]});

 // Only serializable names are enumerated during discovery. Geometry, temporary
 // DXFs and GPU windows are created during execution, never by Test Explorer discovery.
 [Theory]
 [MemberData(nameof(Cases))]
 public void OfflineCheck(string category,string name) => TestSupport.Run(name,()=>
 {
  var result=Build(category).Single(c=>c.Name==name);
  Assert.True(result.Passed,name);
  output.WriteLine($"Visual report: {VisualReport.DirectoryPath}/index.html");
 });

 [Fact]
 public void EveryCheckIsDiscoverable() => TestSupport.Run("Every check has a Test Explorer case",()=>
 {
  foreach(var category in CheckCatalog.Entries.Select(e=>e[0]).Distinct())
  {
   var actual=Build(category).Select(c=>c.Name).Order().ToArray();
   var expected=CheckCatalog.Entries.Where(e=>e[0]==category).Select(e=>e[1]).Order().ToArray();
   Assert.Equal(expected,actual);
  }
 });

 private static List<CheckCase> Build(string category)
 {
  if(category=="Geometry")return GeometryChecks.BuildCases();
  if(category=="Strategies")return MeasurementStrategyChecks.BuildCases();
  if(category=="Splines")return SplineChecks.BuildCases();
  List<CheckCase>? results=null;
  TestSupport.Sta(()=>results=ViewportChecks.BuildCases());
  return results!;
 }

 [Fact]
 public void SupportedDrawingHasNoWarning() => TestSupport.Run("Supported drawing has no compatibility warning",()=>
 {
  var doc=DxfReader.Load(System.IO.Path.Combine(AppContext.BaseDirectory,"Fixtures","arc-highlight.dxf"));
  Assert.Empty(doc.Skipped);
  Assert.Null(doc.UnsupportedWarning);
 });

 [Fact]
 public void UnsupportedWarningListsEveryType() => TestSupport.Run("Compatibility warning lists skipped types and counts",()=>
 {
  var doc=new DxfDocument();doc.Skipped.Add("TEXT",2);doc.Skipped.Add("INSERT",3);
  var warning=doc.UnsupportedWarning!;
  Assert.Contains("5 unsupported records",warning);
  Assert.Contains("INSERT: 3",warning);Assert.Contains("TEXT: 2",warning);
  Assert.True(warning.IndexOf("INSERT: 3",StringComparison.Ordinal)<warning.IndexOf("TEXT: 2",StringComparison.Ordinal));
  Assert.Contains("additional geometry",warning);
  Assert.Contains("only to displayed geometry",warning);
 });

 [Theory]
 [InlineData("api-cw750-details.dxf",4269,0,"3DSOLID=3,LEADER=11,VIEWPORT (3D view)=1","Sheet 1:63/2;Sheet 2:73/1;Sheet 3:74/2;Sheet 4:62/2")]
 [InlineData("uncommon.dxf",148,1,"3D POLYLINE=2,3DSOLID=1,MESH=1","")]
 public void PublicDrawingAndGpuUpload(string file,int expectedEntities,int expectedSplines,string expectedSkipped,string expectedLayouts) => TestSupport.Run($"Public drawing: {file}",()=>
 {
  var path=System.IO.Path.Combine(AppContext.BaseDirectory,"Fixtures","Public",file);
  var doc=DxfReader.Load(path);
  output.WriteLine($"Rendered entities: {doc.Entities.Count}; kinds: {string.Join(", ",doc.Entities.GroupBy(e=>e.Kind).Select(g=>$"{g.Key}={g.Count()}"))}");
  output.WriteLine($"Skipped: {string.Join(", ",doc.Skipped.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))}");
  // Each layout as name:paper-space entities/viewports, in tab order.
  var layouts=string.Join(";",doc.Layouts.Select(l=>$"{l.Name}:{l.Entities.Count}/{l.Viewports.Count}"));
  output.WriteLine($"Layouts: {layouts}");
  foreach(var layout in doc.Layouts)
   output.WriteLine($"  {layout.Name}: {string.Join(", ",layout.Entities.GroupBy(e=>e.Kind).Select(g=>$"{g.Key}={g.Count()}"))}");
  Assert.NotNull(doc.UnsupportedWarning);
  foreach(var skipped in doc.Skipped) Assert.Contains($"{skipped.Key}: {skipped.Value:N0}",doc.UnsupportedWarning);
  Assert.Equal(expectedEntities,doc.Entities.Count);
  Assert.Equal(expectedLayouts,layouts);
  Assert.Equal(expectedSplines,doc.Entities.Count(e=>e.Kind=="SPLINE"));
  Assert.Equal(expectedSkipped,string.Join(",",doc.Skipped.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>$"{p.Key}={p.Value}")));
  TestSupport.Sta(()=>Assert.All(ViewportChecks.BuildCases(path),c=>Assert.True(c.Passed,c.Name)));
 });
}
