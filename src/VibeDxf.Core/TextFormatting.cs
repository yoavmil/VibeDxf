using System.Globalization;
using System.Text;
namespace VibeDxf.Core;
public sealed record DxfTextStyle(double Height,double Width=1,double Tracking=1,double Oblique=0,bool Bold=false,bool Italic=false,bool Underline=false,bool Overline=false,bool Strike=false,int Align=0);
public sealed record DxfTextRun(string Text,DxfTextStyle Style,char Fraction='\0',string Denominator="",bool Break=false);
internal static class DxfTextFormatting
{
    public static List<DxfTextRun> Parse(string raw,double height,Action<string>? warn)
    {
        var tokens=new List<DxfTextRun>();var state=new DxfTextStyle(height);var stack=new Stack<DxfTextStyle>();var buffer=new StringBuilder();
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

}
