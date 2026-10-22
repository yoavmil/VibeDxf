namespace VibeDxf;
// Color mapping lives in Core; this adapter retains the current dark theme policy.
public static class DxfColors
{
 public const uint White=VibeDxf.Core.DxfColors.White;
 public static uint Pack(byte r,byte g,byte b)=>VibeDxf.Core.DxfColors.Pack(r,g,b);
 public static (byte R,byte G,byte B) Unpack(uint color)=>VibeDxf.Core.DxfColors.Unpack(color);
 public static string Hex(uint color)=>VibeDxf.Core.DxfColors.Hex(color);
 public static uint FromAci(int index)=>VibeDxf.Core.DxfColors.FromAci(index);
 public static uint FromTrueColor(int value)=>VibeDxf.Core.DxfColors.FromTrueColor(value);
 public static uint Resolve(int? aci,int? trueColor,uint layerColor,uint? blockColor)=>ForDarkBackground(VibeDxf.Core.DxfColors.Resolve(aci,trueColor,layerColor,blockColor));
    public static uint ForDarkBackground(uint color)
    {
        var (r, g, b) = Unpack(color);
        const double floor = 0.3;
        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        if (luminance >= floor) return color;
        double t = (floor - luminance) / (1 - luminance);
        byte Lift(byte c) => (byte)Math.Round(c + (255 - c) * t);
        return Pack(Lift(r), Lift(g), Lift(b));
    }
}
