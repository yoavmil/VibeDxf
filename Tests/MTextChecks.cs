using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class MTextChecks
{
    [Fact]
    public void UncommonEmbeddedMetadataDoesNotRotateLabels()=>TestSupport.Run("uncommon.dxf: all 19 MTEXT labels ignore embedded insertion points as directions",()=>
    {
        var lines=File.ReadAllLines(Path.Combine(AppContext.BaseDirectory,"Fixtures","Public","uncommon.dxf"));
        var pairs=Enumerable.Range(0,lines.Length/2).Select(i=>(Code:int.Parse(lines[i*2].Trim()),Value:lines[i*2+1])).ToArray();
        string section="";int labels=0;
        for(int i=0;i<pairs.Length;)
        {
            if(pairs[i].Code!=0){i++;continue;}
            int end=i+1;while(end<pairs.Length&&pairs[end].Code!=0)end++;
            var fields=pairs[(i+1)..end].ToList();var kind=pairs[i].Value.Trim();i=end;
            if(kind=="SECTION")section=fields.First(p=>p.Code==2).Value.Trim();
            if(kind=="ENDSEC")section="";
            if(section!="ENTITIES"||kind!="MTEXT")continue;
            var main=fields.TakeWhile(p=>p.Code is not (101 or 1001)).ToList();
            Assert.DoesNotContain(main,p=>p.Code is 11 or 50);
            var expected=DxfMText.Read(main,"0","").Entity;
            var actual=DxfMText.Read(fields,"0","").Entity;
            Assert.Equal(expected.Text,actual.Text);Assert.Equal(expected.Curves.Count,actual.Curves.Count);
            foreach(var (a,b) in expected.Curves.Zip(actual.Curves))
            {TestSupport.Near(a.Start.X,b.Start.X);TestSupport.Near(a.Start.Y,b.Start.Y);}
            labels++;
        }
        Assert.Equal(19,labels);
    });

    [Fact]
    public void EmbeddedDirectionsUseTheirOwnGroupCodes()=>TestSupport.Run("MTEXT embedded direction uses 10/20 and preserves entity height/anchor",()=>
    {
        var main=Tags(40,2,10,100,20,200,1,"TEST");
        var embedded=Tags(101,"Embedded Object",10,0,20,1,11,100,21,200,40,30,41,99,71,2,72,1);
        main.AddRange(embedded);
        var actual=DxfMText.Read(main,"0","").Entity;
        var expected=Layout("TEST",0,1,10,100,20,200,11,0,21,1).Entity;
        foreach(var (a,b) in expected.Curves.Zip(actual.Curves))
        {TestSupport.Near(a.Start.X,b.Start.X);TestSupport.Near(a.Start.Y,b.Start.Y);}
    });

    [Theory]
    [InlineData("mtext-single-label.dxf","Hello VibeDxf")]
    [InlineData("mtext-single-uncommon-label.dxf","MTEXT")]
    [InlineData("mtext-single-example-label.dxf","DETAIL \"A\"")]
    public void SingleHorizontalLabelGpu(string file,string expected)=>TestSupport.Run($"GPU isolated horizontal MTEXT: {file}",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var doc=DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures",file));
        var label=Assert.Single(doc.Entities);Assert.Equal(expected,label.Text);Assert.Equal("MTEXT",label.Kind);
        Assert.Empty(doc.Skipped);Assert.Empty(doc.RenderingWarnings);Assert.True(label.Bounds.Width>label.Bounds.Height*3);
        var view=new DxfViewport();var window=new Window{Content=view,Width=900,Height=300,Left=-20000,Top=-20000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();view.SetDocument(doc);Pump();
            var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,$"Single horizontal MTEXT · {expected} · {file}",()=>Call(view,"RenderNow"));
            var triangles=DxfPrimitives.Triangulate(label.FillContours);
            var triangle=triangles.Chunk(3).MaxBy(t=>Math.Abs(Vector.CrossProduct(t[1]-t[0],t[2]-t[0])))!;
            var probe=new Point(triangle.Average(p=>p.X),triangle.Average(p=>p.Y));
            var screen=(Point)Call(view,"Screen",probe)!;
            Assert.True(frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),(r,g,b)=>r>120&&g>120&&b>120));
        }
        finally{window.Close();Pump();}
    }));

    [Theory]
    [InlineData("AVATAR office")]
    [InlineData("שלום עולם")]
    [InlineData("العربية")]
    [InlineData("Ω 中文 😀")]
    public void UsesWpfTextShaping(string value)=>TestSupport.Run($"MTEXT WPF shaping: {value}",()=>
    {
        var layout=Layout(value);var typeface=new Typeface("Arial");
        Assert.True(typeface.TryGetGlyphTypeface(out var font));
        var reference=new FormattedText(value,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,100,Brushes.White,1);
        TestSupport.Near(reference.WidthIncludingTrailingWhitespace*2/(font.CapsHeight*100),layout.Width,.03);
        Assert.Equal(value,layout.Entity.Text);Assert.NotEmpty(layout.Entity.Curves);
        Assert.All(layout.Entity.Curves,c=>Assert.True(double.IsFinite(c.Start.X)&&double.IsFinite(c.Start.Y)));
    });

    [Fact]
    public void FormattingBoundariesPreserveKerning()=>TestSupport.Run("MTEXT WPF kerning survives formatting scope boundaries",()=>
    {
        TestSupport.Near(Layout("AVATAR").Width,Layout("A{VA}TAR").Width,.02);
        Assert.Equal(2,Layout("First\nSecond").Lines);
        Assert.Equal(2,Layout("First\n").Lines);
    });

    private static List<(int Code,string Value)> Tags(params object[] data)=>Enumerable.Range(0,data.Length/2)
        .Select(i=>((int)data[2*i],Convert.ToString(data[2*i+1],System.Globalization.CultureInfo.InvariantCulture)!)).ToList();
    private static DxfMText.Layout Layout(string text,double width=0,int attach=1,params object[] extra)
    {
        var tags=Tags(40,2,41,width,71,attach,1,text);tags.AddRange(Tags(extra));return DxfMText.Read(tags,"LABELS","M");
    }
    private static DxfDocument Drawing()=>DxfReader.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","mtext.dxf"));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void AttachmentPositions(int attachment)=>TestSupport.Run($"MTEXT attachment {attachment}",()=>
    {
        var layout=Layout("TEST",0,attachment);var b=layout.Entity.Bounds;
        int horizontal=(attachment-1)%3,vertical=(attachment-1)/3;
        if(horizontal==0)Assert.True(b.Left>=-.05&&b.Left<.2);
        if(horizontal==1)TestSupport.Near(0,b.Left+b.Width/2,.15);
        if(horizontal==2)TestSupport.Near(0,b.Right,.15);
        if(vertical==0)TestSupport.Near(0,b.Bottom,.02);
        if(vertical==1)TestSupport.Near(0,b.Top+b.Height/2,.02);
        if(vertical==2)TestSupport.Near(0,b.Top,.02);
    });

    [Fact]
    public void ParagraphsWrappingAndSpacing()=>TestSupport.Run("MTEXT paragraph breaks, wrapping, long words and line spacing",()=>
    {
        var plain=Layout(@"First\PSecond");Assert.Equal(2,plain.Lines);Assert.Equal("First\nSecond",plain.Entity.Text);
        var wrap=Layout("One two three four five",8);Assert.True(wrap.Lines>1);Assert.True(wrap.Entity.Bounds.Width<=8.2);
        var longWord=Layout("UnbrokenLongWord",3);Assert.Equal(1,longWord.Lines);Assert.True(longWord.Entity.Bounds.Width>3);
        var spaced=Layout(@"TEST\PTEST",0,1,44,2);
        TestSupport.Near(2*5d/3,spaced.Height-Layout(@"TEST\PTEST").Height,.03);
        Assert.Equal(3,Layout(@"First\P\PThird").Lines);
        Assert.True(Layout(@"One\~Two",4).Entity.Text!.Contains('\u00a0'));
    });

    [Fact]
    public void FormattingScopesAndFractions()=>TestSupport.Run("MTEXT formatting scopes, glyph holes, fractions and escapes",()=>
    {
        var plain=Layout("TEST");var large=Layout(@"{\H2x;TEST}");
        TestSupport.Near(plain.Entity.Bounds.Width*2,large.Entity.Bounds.Width,.02);
        var narrow=Layout(@"\W0.5;TEST");TestSupport.Near(plain.Entity.Bounds.Width*.5,narrow.Entity.Bounds.Width,.02);
        Assert.True(Layout(@"\T2;TEST").Width>plain.Width*1.8);
        var fraction=Layout(@"\A1;2{\H0.8x;\S1#2;} \S3/4; \S+0.1^-0.2;");
        Assert.Equal("21/2 3/4 +0.1/-0.2",fraction.Entity.Text);Assert.NotEmpty(fraction.Entity.Curves);
        Assert.Equal(@"Ω ° ± Ø {literal}",Layout(@"\U+03A9 %%d %%p %%c \{literal\}").Entity.Text);
        var decorated=Layout(@"\LTEST\l");Assert.True(decorated.Entity.Curves.Count>plain.Entity.Curves.Count);
        var restored=Layout(@"{\H2x;A}B");Assert.True(restored.Entity.Bounds.Height>=plain.Entity.Bounds.Height*1.9);
        var o=Layout("O").Entity;var center=new Point(o.Bounds.Left+o.Bounds.Width/2,o.Bounds.Top+o.Bounds.Height/2);
        Assert.False(o.Contains(center));Assert.Null(new MidpointIndex([o]).Find(o.Curves[0].At(.5),.01));
    });

    [Fact]
    public void ChunkingOrientationAndBlocks()=>TestSupport.Run("MTEXT chunks, direction vectors, reversed normal and nested INSERT",()=>
    {
        var tags=Tags(40,2,3,"Leading ",3,@"\P",1," trailing",10,5,20,7,11,0,21,1,50,0);
        var rotated=DxfMText.Read(tags,"0","M");Assert.Equal("Leading \n trailing",rotated.Entity.Text);
        var plain=Layout("TEST");var turned=Layout("TEST",0,1,11,0,21,1,50,0);
        var a=plain.Entity.Curves[0].Start;var b=turned.Entity.Curves[0].Start;
        TestSupport.Near(-a.Y,b.X);TestSupport.Near(a.X,b.Y);
        var reflected=Layout("TEST",0,1,230,-1).Entity.Curves[0].Start;
        TestSupport.Near(a.X,reflected.X);TestSupport.Near(-a.Y,reflected.Y);
        var doc=Drawing();Assert.Empty(doc.Skipped);Assert.Empty(doc.RenderingWarnings);Assert.Equal(7,doc.Entities.Count);
        var nested=doc.Entities.Single(e=>e.Handle=="I[0,0]/NOTE_TEXT");Assert.Equal("MTEXT",nested.Kind);Assert.Equal("LABELS",nested.Layer);
        var normal=Layout(@"Nested\Ptext with \S1/2;",12).Entity;
        var matrix=Matrix.Identity;matrix.Scale(1.3,1.3);matrix.Rotate(25);matrix.Translate(55,-10);
        TestSupport.Near(matrix.Transform(normal.Curves[0].Start).X,nested.Curves[0].Start.X);
        TestSupport.Near(matrix.Transform(normal.Curves[0].Start).Y,nested.Curves[0].Start.Y);
    });

    [Fact]
    public void LimitationsWarnWithoutLosingText()=>TestSupport.Run("MTEXT simplified formatting warns; unsupported layouts do not discard valid geometry",()=>
    {
        var path=Path.GetTempFileName();
        try
        {
            var tags=Tags(0,"SECTION",2,"ENTITIES",0,"MTEXT",5,"M",40,2,1,@"\pql;Visible",90,1,
                0,"MTEXT",40,2,75,1,1,"Columns",0,"MTEXT",40,2,72,3,1,"Vertical",0,"ENDSEC",0,"EOF");
            File.WriteAllLines(path,tags.SelectMany(t=>new[]{t.Code.ToString(),t.Value}));var doc=DxfReader.Load(path);
            Assert.Equal("Visible",Assert.Single(doc.Entities).Text);
            Assert.Equal(1,doc.Skipped["MTEXT (columns)"]);Assert.Equal(1,doc.Skipped["MTEXT (vertical flow)"]);
            Assert.Contains("paragraph",doc.UnsupportedWarning!);Assert.Contains("background",doc.UnsupportedWarning!);
            var notesOnly=new DrawingBuilder();notesOnly.RenderingWarnings.Add("MTEXT paragraph formatting is simplified",1);
            Assert.NotNull(notesOnly.UnsupportedWarning);Assert.DoesNotContain("records were skipped",notesOnly.UnsupportedWarning!);
        }
        finally{File.Delete(path);}
    });

    [Fact]
    public void GpuTextSelectionDistanceAndReuse()=>TestSupport.Run("GPU MTEXT wrapping, formatting, selection, distances and buffer reuse",()=>TestSupport.Sta(()=>
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Field(object target,string name)=>target.GetType().GetField(name,flags)!.GetValue(target);
        object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        var view=new DxfViewport();var window=new Window{Content=view,Width=1200,Height=850,Left=-20000,Top=-20000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false};
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            var doc=Drawing();var entity=doc.Entities.Single(e=>e.Handle=="PLAIN");
            window.Show();view.SetDocument(doc);Pump();var renderer=(DrawingRenderer)Field(Field(view,"host")!,"<Renderer>k__BackingField")!;
            var frame=VisualReport.Capture(renderer,"MTEXT paragraphs, wrapping, inline formatting, fractions, rotation and block insertion",()=>Call(view,"RenderNow"));
            var triangles=DxfPrimitives.Triangulate(entity.FillContours);var tri=triangles.Chunk(3).MaxBy(t=>Math.Abs(Vector.CrossProduct(t[1]-t[0],t[2]-t[0])))!;
            var probe=new Point(tri.Average(p=>p.X),tri.Average(p=>p.Y));
            bool Pixel(Point p,Func<byte,byte,byte,bool> predicate)
            {
                var screen=(Point)Call(view,"Screen",p)!;
                return frame.Near((int)(screen.X*frame.Width/view.ActualWidth),(int)(screen.Y*frame.Height/view.ActualHeight),predicate);
            }
            Assert.True(Pixel(probe,(r,g,b)=>r>120&&g>120&&b>120));
            Call(view,"SelectAt",probe,true);Assert.Same(entity,view.Selected.Single().Entity);Assert.Null(view.Selected[0].Midpoint);
            frame=VisualReport.Capture(renderer,"Selected MTEXT outlines",()=>Call(view,"RenderNow"));
            Assert.True(Pixel(entity.Curves[0].At(.5),(r,g,b)=>r>200&&g>90&&b<70));
            var p=new Point(entity.Bounds.Right+3,entity.Bounds.Top);
            var point=new DxfEntity("POINT","0","P",[Curve.Line(p,p)]);var extended=new DrawingBuilder();extended.Entities.AddRange(doc.Entities);extended.Entities.Add(point);view.SetDocument(extended);Pump();
            Call(view,"SelectAt",probe,false);Call(view,"SelectAt",p,false);
            TestSupport.Near(entity.Distance(point).Distance,view.Measurement!.Value.Distance);
            Assert.True(view.Measurement.Value.Distance>=3);
            var fill=Field(renderer,"fillBuffer");var lines=Field(renderer,"lineBuffer");
            view.GetType().GetField("scale",flags)!.SetValue(view,(double)Field(view,"scale")!*2);
            view.GetType().GetField("offset",flags)!.SetValue(view,(Vector)Field(view,"offset")!+new Vector(20,-10));
            Call(view,"RenderNow");Assert.Same(fill,Field(renderer,"fillBuffer"));Assert.Same(lines,Field(renderer,"lineBuffer"));
        }
        finally{window.Close();Pump();}
    }));
}
