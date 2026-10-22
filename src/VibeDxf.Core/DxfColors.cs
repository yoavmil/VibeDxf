namespace VibeDxf.Core;

/// <summary>
/// DXF color resolution. Colors are packed RGBA with R in the low byte (the GPU's R8G8B8A8 layout).
/// </summary>
public static class DxfColors
{
    public const uint White = 0xFFFFFFFF;
    public static uint Pack(byte r, byte g, byte b) => r | (uint)g << 8 | (uint)b << 16 | 0xFF000000;
    public static (byte R, byte G, byte B) Unpack(uint color) => ((byte)color, (byte)(color >> 8), (byte)(color >> 16));
    public static string Hex(uint color) { var (r, g, b) = Unpack(color); return $"#{r:X2}{g:X2}{b:X2}"; }

    // AutoCAD Color Index. 1–9 are named colors; 10–249 are 24 hues in 15° steps, each with five values
    // (100/80/60/50/30%) at full (even index) and half (odd index) saturation; 250–255 are greys.
    private static readonly uint[] Aci = BuildAci();
    private static uint[] BuildAci()
    {
        var aci = new uint[256];
        (byte, byte, byte)[] named = [(0, 0, 0), (255, 0, 0), (255, 255, 0), (0, 255, 0), (0, 255, 255), (0, 0, 255), (255, 0, 255), (255, 255, 255), (128, 128, 128), (192, 192, 192)];
        for (int i = 0; i < named.Length; i++) aci[i] = Pack(named[i].Item1, named[i].Item2, named[i].Item3);
        double[] values = [1, .8, .6, .5, .3];
        for (int i = 10; i < 250; i++)
        {
            double hue = (i / 10 - 1) * 15, v = values[i % 10 / 2] * 255, s = i % 2 == 0 ? 1 : .5;
            // HSV to RGB with AutoCAD's truncation (e.g. ACI 20 = 255,63,0 and ACI 11 = 255,127,127).
            double c = v * s, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = v - c;
            var (r, g, b) = (int)(hue / 60) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
            aci[i] = Pack((byte)(r + m), (byte)(g + m), (byte)(b + m));
        }
        byte[] greys = [51, 80, 105, 130, 190, 255];
        for (int i = 0; i < greys.Length; i++) aci[250 + i] = Pack(greys[i], greys[i], greys[i]);
        return aci;
    }

    /// <summary>Raw ACI palette color (index 1–255).</summary>
    public static uint FromAci(int index) => Aci[Math.Clamp(Math.Abs(index), 0, 255)];

    /// <summary>Group 420 true color: 0x00RRGGBB.</summary>
    public static uint FromTrueColor(int value) => Pack((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>
    /// Resolves an entity color: true color (420) wins, then ACI 1–255; 0 is BYBLOCK (the enclosing insert's color,
    /// white at top level) and 256 or absent is BYLAYER.
    /// </summary>
    public static uint Resolve(int? aci, int? trueColor, uint layerColor, uint? blockColor)
    {
        if (trueColor is { } t) return FromTrueColor(t);
        return aci switch
        {
            null or 256 => layerColor,
            0 => blockColor ?? White,
            var a => FromAci(a.Value),
        };
    }

}
