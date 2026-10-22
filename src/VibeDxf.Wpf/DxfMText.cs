using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace VibeDxf.Wpf;

// Layout and outline extraction run only while loading. Rendering reuses immutable GPU geometry.
internal static class DxfMText
{
    private sealed record Style(double Height,double Width=1,double Tracking=1,double Oblique=0,
        bool Bold=false,bool Italic=false,bool Underline=false,bool Overline=false,bool Strike=false,int Align=0);
    private sealed record Token(string Text,Style Style,char Fraction='\0',string Denominator="",bool Break=false);
    private sealed record Ink(List<Point[]> Contours,List<Curve> Decorations,double Advance,double Height);
    private sealed record Placed(Ink Ink,double X);
    private sealed class Line
    {
        public List<Placed> Items=[];
        public double Width,Height;
    }
    internal sealed record Layout(DxfEntity Entity,int Lines,double Width,double Height);

    public static Layout Read(List<(int Code,string Value)> tags,string layer,string handle,Action<string>? warn=null)
    {
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
                throw new InvalidDataException("Invalid MTEXT number.");
            return value;
        }
        if(N(75)!=0||N(76)>1||E(72)>1)throw new NotSupportedException("MTEXT (columns)");
        if(N(72,1)==3)throw new NotSupportedException("MTEXT (vertical flow)");
        if(Math.Abs(N(210))>1e-9||Math.Abs(N(220))>1e-9||Math.Abs(Math.Abs(N(230,1))-1)>1e-9||Math.Abs(N(31))>1e-9)
            throw new NotSupportedException("MTEXT (3D orientation)");
        if(N(90)!=0)warn?.Invoke("MTEXT background mask/frame is not displayed");
        var height=N(40);var wrap=N(41);var attach=(int)N(71,1);var factor=N(44,1);var spacing=(int)N(73,1);
        if(height<=0||wrap<0||attach is <1 or >9||factor is <.25 or >4||spacing is <1 or >2)
            throw new InvalidDataException("Invalid MTEXT layout dimensions.");
        var raw=string.Concat(tags.Where(t=>t.Code is 3 or 1).Select(t=>t.Value));
        if(raw.Length>100000)throw new NotSupportedException("MTEXT (text length limit)");
        var tokens=Parse(raw,height,warn);var plain=new StringBuilder();
        foreach(var token in tokens)
            if(token.Break)plain.Append('\n');
            else if(token.Fraction!='\0')plain.Append(token.Text).Append('/').Append(token.Denominator);
            else plain.Append(token.Text);
        var lines=FormatLines(tokens,height,wrap);
        var width=wrap>0?Math.Max(wrap,lines.Max(l=>l.Width)):lines.Max(l=>l.Width);
        // The DXF line spacing factor multiplies the default 5/3 cap-height baseline distance.
        var baselines=new List<double>();double y=0;
        for(int i=0;i<lines.Count;i++)
        {
            baselines.Add(y);
            if(i+1<lines.Count)y-=height*5/3*factor*(spacing==1?Math.Max(lines[i].Height,lines[i+1].Height)/height:1);
        }
        var contours=new List<Point[]>();var decorations=new List<Curve>();
        for(int i=0;i<lines.Count;i++)
        {
            // Attachment controls the reference box, while the paragraph remains left aligned.
            foreach(var placed in lines[i].Items)
            {
                var offset=new Vector(placed.X,baselines[i]);
                contours.AddRange(placed.Ink.Contours.Select(p=>p.Select(v=>v+offset).ToArray()));
                decorations.AddRange(placed.Ink.Decorations.Select(c=>Curve.Line(c.Start+offset,c.End+offset)));
            }
        }
        var box=contours.SelectMany(p=>p).Aggregate(Rect.Empty,(r,p)=>{r.Union(p);return r;});
        // Blank paragraphs retain their layout height even when they contain no ink.
        var top=Math.Max(height,box.IsEmpty?0:box.Bottom);
        var bottom=Math.Min(baselines[^1],box.IsEmpty?0:box.Top);
        var boxHeight=top-bottom;
        var dx=-((attach-1)%3)*width/2;
        var dy=((attach-1)/3) switch{0=>-top,1=>-(top+bottom)/2,_=>-bottom};
        double rotation=N(50)*Math.PI/180;
        var direction=new Vector(Math.Cos(rotation),Math.Sin(rotation));
        if(tags.Any(t=>t.Code==11))
        {
            direction=new Vector(N(11),N(21));if(direction.Length<1e-12)throw new InvalidDataException("Invalid MTEXT direction.");direction.Normalize();
        }
        else if(!tags.Any(t=>t.Code==50)&&embedded.Any(t=>t.Code==10))
        {
            if(Math.Abs(E(30))>1e-9)throw new NotSupportedException("MTEXT (3D orientation)");
            direction=new Vector(E(10),E(20));
            if(!double.IsFinite(direction.X)||!double.IsFinite(direction.Y)||direction.Length<1e-12)throw new InvalidDataException("Invalid embedded MTEXT direction.");
            direction.Normalize();
        }
        var vertical=new Vector(-direction.Y,direction.X)*(N(230,1)<0?-1:1);var anchor=new Point(N(10),N(20));
        Point World(Point p)=>anchor+(p.X+dx)*direction+(p.Y+dy)*vertical;
        contours=contours.Select(p=>p.Select(World).ToArray()).ToList();
        decorations=decorations.Select(c=>Curve.Line(World(c.Start),World(c.End))).ToList();
        var curves=DxfPrimitives.Outline(contours);curves.AddRange(decorations);
        return new(new DxfEntity("MTEXT",layer,handle,curves){FillContours=contours,Text=plain.ToString(),IsApproximate=true},lines.Count,width,boxHeight);
    }

    private static IEnumerable<string> AsElements(this TextElementEnumerator enumerator)
    {while(enumerator.MoveNext())yield return enumerator.GetTextElement();}

    private sealed class RunProperties(Style style,double baseHeight) : TextRunProperties
    {
        public override Typeface Typeface {get;}=new(new FontFamily("Arial"),style.Italic?FontStyles.Italic:FontStyles.Normal,
            style.Bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal);
        public override double FontRenderingEmSize => 100*style.Height/baseHeight/
            (Typeface.TryGetGlyphTypeface(out var glyph)?glyph.CapsHeight:.72);
        public override double FontHintingEmSize=>FontRenderingEmSize;
        public override Brush ForegroundBrush=>Brushes.White;
        public override Brush BackgroundBrush=>null!;
        public override CultureInfo CultureInfo=>CultureInfo.InvariantCulture;
        public override TextEffectCollection TextEffects=>null!;
        public override TextDecorationCollection TextDecorations
        {
            get
            {
                var result=new TextDecorationCollection();
                if(style.Underline)result.Add(System.Windows.TextDecorations.Underline[0]);
                if(style.Overline)result.Add(System.Windows.TextDecorations.OverLine[0]);
                if(style.Strike)result.Add(System.Windows.TextDecorations.Strikethrough[0]);
                return result;
            }
        }
    }
    private sealed class ParagraphProperties(TextRunProperties defaults,bool wrap) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection=>FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment=>TextAlignment.Left;
        public override double LineHeight=>0;
        public override bool FirstLineInParagraph=>true;
        public override TextRunProperties DefaultTextRunProperties=>defaults;
        public override TextWrapping TextWrapping=>wrap?TextWrapping.WrapWithOverflow:TextWrapping.NoWrap;
        public override TextMarkerProperties TextMarkerProperties=>null!;
        public override double Indent=>0;
    }
    private sealed record Span(int Start,string Text,Style Style,Ink? Object=null);
    private sealed class Source(List<Span> spans,string text,double baseHeight) : TextSource
    {
        public int Length=>text.Length;
        public double MaxHeight(int start,int length)=>spans.Where(s=>s.Start<start+length&&s.Start+s.Text.Length>start)
            .Select(s=>s.Object?.Height??s.Style.Height).DefaultIfEmpty(baseHeight).Max();
        public override TextRun GetTextRun(int index)
        {
            if(index>=text.Length)return new TextEndOfParagraph(1);
            var span=spans.First(s=>s.Start<=index&&index<s.Start+s.Text.Length);
            var props=new RunProperties(span.Style,baseHeight);
            if(span.Object is {} ink)return new EmbeddedInk(ink,props,baseHeight);
            if(span.Text=="\n")return new TextEndOfLine(1);
            var offset=index-span.Start;
            return new TextCharacters(span.Text,offset,span.Text.Length-offset,props);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)=>
            new(limit,new(CultureInfo.InvariantCulture,new CharacterBufferRange(text,0,limit)));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index)=>index;
    }
    private sealed class EmbeddedInk(Ink ink,TextRunProperties properties,double baseHeight) : TextEmbeddedObject
    {
        private double Scale=>100/baseHeight;
        private Rect Bounds=>ink.Contours.SelectMany(p=>p).Aggregate(new Rect(0,0,ink.Advance,ink.Height),
            (r,p)=>{r.Union(p);return r;});
        public override CharacterBufferReference CharacterBufferReference=>new("\uFFFC",0);
        public override int Length=>1;
        public override TextRunProperties Properties=>properties;
        public override LineBreakCondition BreakBefore=>LineBreakCondition.BreakPossible;
        public override LineBreakCondition BreakAfter=>LineBreakCondition.BreakPossible;
        public override bool HasFixedSize=>true;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)=>
            new(Math.Max(.001,ink.Advance*Scale),Bounds.Height*Scale,Bounds.Bottom*Scale);
        public override Rect ComputeBoundingBox(bool rightToLeft,bool sideways)=>
            new(Bounds.Left*Scale,-Bounds.Bottom*Scale,Bounds.Width*Scale,Bounds.Height*Scale);
        public override void Draw(DrawingContext dc,Point origin,bool rightToLeft,bool sideways)
        {
            Point Map(Point p)=>origin+new Vector(p.X*Scale,-p.Y*Scale);
            var geometry=new PathGeometry{FillRule=FillRule.EvenOdd};
            foreach(var p in ink.Contours)
                geometry.Figures.Add(new PathFigure(Map(p[0]),[new PolyLineSegment(p.Skip(1).Select(Map),true)],true));
            dc.DrawGeometry(Brushes.White,null,geometry);
            foreach(var line in ink.Decorations)dc.DrawLine(new Pen(Brushes.White,Math.Max(.3,Scale*baseHeight*.015)),Map(line.Start),Map(line.End));
        }
    }
    private static List<Line> FormatLines(List<Token> tokens,double baseHeight,double wrap)
    {
        var spans=new List<Span>();var text=new StringBuilder();
        void Add(string value,Style style,Ink? ink=null)
        {if(value.Length==0)return;spans.Add(new(text.Length,value,style,ink));text.Append(value);}
        foreach(var token in tokens)
        {
            if(token.Break){Add("\n",token.Style);continue;}
            if(token.Fraction!='\0'){Add("\uFFFC",token.Style,Fraction(token));continue;}
            if(token.Style.Width==1&&token.Style.Tracking==1&&token.Style.Oblique==0){Add(token.Text,token.Style);continue;}
            // WPF handles the line breaks around DXF-specific stretched/slanted objects.
            // Ordinary runs remain native TextCharacters for shaping, kerning and bidi.
            foreach(Match word in Regex.Matches(token.Text,@"[ \t]+|[^ \t]+"))
            {
                if(string.IsNullOrWhiteSpace(word.Value)){Add(word.Value,token.Style);continue;}
                Add("\uFFFC",token.Style,Glyph(word.Value,token.Style));
            }
        }
        var source=new Source(spans,text.ToString(),baseHeight);var lines=new List<Line>();
        using var formatter=TextFormatter.Create(TextFormattingMode.Ideal);
        var paragraph=new ParagraphProperties(new RunProperties(new Style(baseHeight),baseHeight),wrap>0);
        int position=0;TextLineBreak? previous=null;
        try
        {
            do
            {
                using var formatted=formatter.FormatLine(source,position,wrap>0?wrap*100/baseHeight:0,paragraph,previous);
                previous?.Dispose();previous=formatted.GetTextLineBreak();
                var group=new DrawingGroup();using(var dc=group.Open())formatted.Draw(dc,new Point(0,-formatted.Baseline),InvertAxes.None);
                var contours=new List<Point[]>();
                void Extract(Drawing drawing,Matrix transform)
                {
                    if(drawing is DrawingGroup children)
                    {
                        var combined=children.Transform?.Value??Matrix.Identity;combined.Append(transform);
                        foreach(var child in children.Children)Extract(child,combined);return;
                    }
                    Geometry? geometry=drawing is GlyphRunDrawing glyph?glyph.GlyphRun.BuildGeometry():
                        drawing is GeometryDrawing shape?shape.Geometry:null;
                    if(geometry==null)return;
                    if(drawing is GeometryDrawing painted&&painted.Pen!=null)geometry=geometry.GetWidenedPathGeometry(painted.Pen);
                    var flattened=geometry.GetFlattenedPathGeometry(.02,ToleranceType.Absolute);
                    var matrix=flattened.Transform?.Value??Matrix.Identity;matrix.Append(transform);
                    foreach(var figure in flattened.Figures)
                    {
                        var points=new List<Point>{figure.StartPoint};
                        foreach(var segment in figure.Segments)
                            if(segment is PolyLineSegment poly)points.AddRange(poly.Points);else if(segment is LineSegment line)points.Add(line.Point);
                        if(points.Count<3)continue;
                        contours.Add(points.Select(p=>{var q=matrix.Transform(p);return new Point(q.X*baseHeight/100,-q.Y*baseHeight/100);}).ToArray());
                    }
                }
                Extract(group,Matrix.Identity);
                var line=new Line{Width=formatted.WidthIncludingTrailingWhitespace*baseHeight/100,
                    Height=Math.Max(baseHeight,source.MaxHeight(position,formatted.Length))};
                line.Items.Add(new(new Ink(contours,[],line.Width,line.Height),0));lines.Add(line);
                position+=formatted.Length;
                if(lines.Count>10000||formatted.Length<=0)throw new InvalidDataException("MTEXT layout limit.");
            }while(position<=source.Length);
        }
        finally{previous?.Dispose();}
        return lines;
    }

    private static List<Token> Parse(string raw,double height,Action<string>? warn)
    {
        var tokens=new List<Token>();var state=new Style(height);var stack=new Stack<Style>();var buffer=new StringBuilder();
        void Flush()
        {
            if(buffer.Length==0)return;
            // Shared TEXT decoding: %% symbols and codes, plus caret-encoded tabs (^I) used in CAD tables.
            tokens.Add(new(DxfPrimitives.DecodeText(buffer.ToString()),state));buffer.Clear();
        }
        for(int i=0;i<raw.Length;i++)
        {
            char ch=raw[i];
            if(ch=='{'){Flush();stack.Push(state);continue;}
            if(ch=='}'){Flush();if(stack.Count>0)state=stack.Pop();else warn?.Invoke("MTEXT unmatched formatting braces");continue;}
            if(ch=='\n'){Flush();tokens.Add(new("",state,Break:true));continue;}
            if(ch=='\r')continue;
            if(ch!='\\'||i+1==raw.Length){buffer.Append(ch);continue;}
            char command=raw[++i];
            if(command is '\\' or '{' or '}'){buffer.Append(command);continue;}
            if(command=='~'){buffer.Append('\u00a0');continue;}
            if(command=='U'&&i+5<raw.Length&&raw[i+1]=='+')
            {buffer.Append(DxfPrimitives.DecodeText(raw.Substring(i-1,7)));i+=5;continue;}
            Flush();
            if(command is 'P' or 'X'){tokens.Add(new("",state,Break:true));continue;}
            if(command is 'L' or 'l'){state=state with{Underline=command=='L'};continue;}
            if(command is 'O' or 'o'){state=state with{Overline=command=='O'};continue;}
            if(command is 'K' or 'k'){state=state with{Strike=command=='K'};continue;}
            int end=raw.IndexOf(';',i+1);
            if(end<0){warn?.Invoke("MTEXT unterminated format command");buffer.Append('\\').Append(command);continue;}
            var arg=raw[(i+1)..end];i=end;
            double Value(bool relative=false)
            {
                bool x=arg.EndsWith('x')||arg.EndsWith('X');
                if(!double.TryParse(x?arg[..^1]:arg,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))
                    throw new InvalidDataException("Invalid MTEXT format number.");
                return relative&&x?value*state.Height:value;
            }
            switch(command)
            {
                case 'H':state=state with{Height=Value(true)};break;
                case 'W':state=state with{Width=Value()};break;
                case 'T':state=state with{Tracking=Value()};break;
                case 'Q':state=state with{Oblique=Value()};break;
                case 'A':state=state with{Align=(int)Value()};break;
                case 'f':case 'F':
                    state=state with{Bold=arg.Contains("|b1",StringComparison.OrdinalIgnoreCase),Italic=arg.Contains("|i1",StringComparison.OrdinalIgnoreCase)};break;
                case 'C':case 'c':break; // The viewer renders the entire drawing in monochrome.
                case 'S':
                    int split=arg.IndexOfAny(['#','/','^']);
                    if(split<0){warn?.Invoke("MTEXT invalid stacked fraction");tokens.Add(new(arg,state));}
                    else tokens.Add(new(DxfPrimitives.DecodeText(arg[..split]),state,arg[split],DxfPrimitives.DecodeText(arg[(split+1)..].TrimStart())));
                    break;
                default:
                    warn?.Invoke(command=='p'?"MTEXT paragraph indentation/alignment is simplified":"MTEXT unsupported inline formatting");
                    break;
            }
            if(state.Height<=0||state.Height>height*1000||state.Width<=0||state.Width>1000||state.Tracking<=0||state.Tracking>100||Math.Abs(state.Oblique)>=85)
                throw new InvalidDataException("Invalid MTEXT character dimensions.");
        }
        Flush();if(stack.Count>0)warn?.Invoke("MTEXT unmatched formatting braces");return tokens;
    }

    private static Ink Glyph(string value,Style style)
    {
        if(style.Tracking!=1&&value.Length>1)
        {
            var trackedContours=new List<Point[]>();var trackedDecorations=new List<Curve>();double x=0;
            foreach(var element in StringInfo.GetTextElementEnumerator(value).AsElements())
            {
                var ink=Glyph(element,style with{Tracking=1});var offset=new Vector(x,0);
                trackedContours.AddRange(ink.Contours.Select(p=>p.Select(v=>v+offset).ToArray()));
                trackedDecorations.AddRange(ink.Decorations.Select(c=>Curve.Line(c.Start+offset,c.End+offset)));
                x+=ink.Advance*style.Tracking;
            }
            return new(trackedContours,trackedDecorations,x,style.Height);
        }
        var typeface=new Typeface(new FontFamily("Arial"),style.Italic?FontStyles.Italic:FontStyles.Normal,style.Bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal);
        var text=new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,100,Brushes.White,1);
        var geometry=text.BuildGeometry(default).GetFlattenedPathGeometry(.02,ToleranceType.Absolute);
        double cap=typeface.TryGetGlyphTypeface(out var glyph)?glyph.CapsHeight*100:72;
        double scale=style.Height/cap;var contours=new List<Point[]>();
        foreach(var figure in geometry.Figures)
        {
            var points=new List<Point>{figure.StartPoint};
            foreach(var segment in figure.Segments)
                if(segment is PolyLineSegment poly)points.AddRange(poly.Points);else if(segment is LineSegment line)points.Add(line.Point);
            contours.Add(points.Select(p=>
            {
                var y=(text.Baseline-p.Y)*scale;return new Point(p.X*scale*style.Width+Math.Tan(style.Oblique*Math.PI/180)*y,y);
            }).ToArray());
        }
        var advance=text.WidthIncludingTrailingWhitespace*scale*style.Width;var decorations=new List<Curve>();
        if(style.Underline)decorations.Add(Curve.Line(new(0,-style.Height*.15),new(advance,-style.Height*.15)));
        if(style.Overline)decorations.Add(Curve.Line(new(0,style.Height*1.1),new(advance,style.Height*1.1)));
        if(style.Strike)decorations.Add(Curve.Line(new(0,style.Height*.45),new(advance,style.Height*.45)));
        return new(contours,decorations,advance,style.Height);
    }

    private static Ink Fraction(Token token)
    {
        var style=token.Style;var small=style with{Height=style.Height*.65,Underline=false,Overline=false,Strike=false};
        var upper=Glyph(token.Text,small);var lower=Glyph(token.Denominator,small);
        double gap=style.Height*.15,width=token.Fraction=='#'?upper.Advance+lower.Advance+gap*2:Math.Max(upper.Advance,lower.Advance)+gap;
        double total=small.Height*2+gap,bottom=style.Align switch{1=>style.Height/2-total/2,2=>style.Height-total,_=>0};
        var topOffset=new Vector(token.Fraction=='#'?0:(width-upper.Advance)/2,bottom+small.Height+gap);
        var lowerOffset=new Vector(token.Fraction=='#'?upper.Advance+gap*2:(width-lower.Advance)/2,bottom);
        var contours=upper.Contours.Select(p=>p.Select(v=>v+topOffset).ToArray()).Concat(lower.Contours.Select(p=>p.Select(v=>v+lowerOffset).ToArray())).ToList();
        var lines=new List<Curve>();
        if(token.Fraction=='/')lines.Add(Curve.Line(new(0,bottom+small.Height+gap/2),new(width,bottom+small.Height+gap/2)));
        if(token.Fraction=='#')lines.Add(Curve.Line(new(upper.Advance,bottom),new(upper.Advance+gap*2,bottom+total)));
        return new(contours,lines,width,total);
    }
}
