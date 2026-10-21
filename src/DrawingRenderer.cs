using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using D2D = Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;
namespace VibeDxf;

/// <summary>
/// Draws the whole drawing with Direct3D 11 from GPU buffers uploaded once per document;
/// a camera change only rewrites one constant buffer. Overlays are drawn with Direct2D on the same back buffer.
/// </summary>
public sealed class DrawingRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Camera : register(b0) { float4 xform; float4 color; float segments; float3 pad; };
        float4 Project(float2 p) { return float4(p * xform.xy + xform.zw, 0, 1); }
        float4 LineVS(float2 p : POSITION) : SV_Position { return Project(p); }
        float4 PointVS(float2 p : POSITION, uint id : SV_VertexID) : SV_Position
        {
            float2 offsets[4] = {float2(-1,0),float2(1,0),float2(0,-1),float2(0,1)};
            float4 q=Project(p); q.xy+=offsets[id%4]*pad.xy; return q;
        }
        float4 ArcVS(float2 center : CENTER, float radius : RADIUS, float2 angles : ANGLES, uint id : SV_VertexID) : SV_Position
        {
            float a = angles.x + angles.y * (id / segments);
            return Project(center + radius * float2(cos(a), sin(a)));
        }
        // Rays/xlines: extend ±3 NDC around the point nearest the screen centre; the rasterizer clips the rest.
        float4 RayVS(float2 origin : ORIGIN, float2 direction : DIRECTION, float infinite : KIND, uint id : SV_VertexID) : SV_Position
        {
            float2 b = Project(origin).xy; float2 d = normalize(direction * xform.xy);
            float s0 = -dot(b, d); float s = s0 + (id == 0 ? -3 : 3);
            if (infinite == 0) s = max(s, 0);
            return float4(b + d * s, 0, 1);
        }
        float4 PS() : SV_Target { return color; }
        """;
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Vector4 Transform; public Vector4 Color; public float Segments; public Vector3 Pad; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ArcInstance(Vector2 Center, float Radius, float Start, float Sweep);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RayInstance(Vector2 Origin, Vector2 Direction, float Infinite);
    // Arcs are sorted by radius and drawn per power-of-two radius bucket, each with just enough segments.
    private readonly record struct ArcBucket(int Start, int Count, double MaxRadius);

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11VertexShader lineShader, arcShader, pointShader, rayShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout lineLayout, arcLayout, rayLayout;
    private readonly ID3D11Buffer constantBuffer;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11BlendState blend;
    private readonly D2D.ID2D1Factory1 d2dFactory;
    private readonly D2D.ID2D1DeviceContext d2d;
    private readonly IDWriteFactory dwrite;
    private ID3D11RenderTargetView? renderTarget;
    private D2D.ID2D1Bitmap1? d2dTarget;
    private ID3D11Buffer? lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer;
    private int lineVertexCount,fillVertexCount,pointVertexCount,dotVertexCount,rayInstanceCount;
    private ArcBucket[] buckets = [];
    private int width, height;
    /// <summary>World origin subtracted before converting to float, so large DXF coordinates keep precision.</summary>
    public System.Windows.Point Origin { get; private set; }
    public D2D.ID2D1Factory1 Factory2D => d2dFactory;
    public IDWriteFactory Write => dwrite;
    // Allows offline rendering checks to read the completed GPU frame before flip-discard presentation.
    internal Action? FrameRendered { get; set; }

    public DrawingRenderer(IntPtr hwnd, int width, int height)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        if (D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device!, out context!).Failure)
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device!, out context!).CheckError();
        using var dxgiDevice = device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        this.width = Math.Max(1, width); this.height = Math.Max(1, height);
        swapChain = factory.CreateSwapChainForHwnd(device, hwnd, new SwapChainDescription1
        {
            Width = (uint)this.width, Height = (uint)this.height, Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        });
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);

        var lineCode = Compiler.Compile(ShaderSource, "LineVS", "drawing.hlsl", "vs_4_0");
        var arcCode = Compiler.Compile(ShaderSource, "ArcVS", "drawing.hlsl", "vs_4_0");
        lineShader = device.CreateVertexShader(lineCode.Span);
        arcShader = device.CreateVertexShader(arcCode.Span);
        pointShader = device.CreateVertexShader(Compiler.Compile(ShaderSource,"PointVS","drawing.hlsl","vs_4_0").Span);
        pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "drawing.hlsl", "ps_4_0").Span);
        lineLayout = device.CreateInputLayout([new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0)], lineCode.Span);
        arcLayout = device.CreateInputLayout([
            new InputElementDescription("CENTER", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("RADIUS", 0, Format.R32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ANGLES", 0, Format.R32G32_Float, 12, 0, InputClassification.PerInstanceData, 1)], arcCode.Span);
        var rayCode = Compiler.Compile(ShaderSource, "RayVS", "drawing.hlsl", "vs_4_0");
        rayShader = device.CreateVertexShader(rayCode.Span);
        rayLayout = device.CreateInputLayout([
            new InputElementDescription("ORIGIN", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("DIRECTION", 0, Format.R32G32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("KIND", 0, Format.R32_Float, 16, 0, InputClassification.PerInstanceData, 1)], rayCode.Span);
        constantBuffer = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // Alpha-blended antialiased lines; MSAA is not available on flip-model swap chains.
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { AntialiasedLineEnable = true });
        blend = device.CreateBlendState(BlendDescription.NonPremultiplied);

        d2dFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>();
        using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        d2d = d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);
        dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        CreateTargets();
    }

    public void SetDrawing(IEnumerable<Curve> curves, System.Windows.Point origin) => SetDrawing([new DxfEntity("LINE","0","",curves.ToList())],origin);

    public void SetDrawing(IEnumerable<DxfEntity> drawing, System.Windows.Point origin)
    {
        Origin = origin;
        var lines = new List<Vector2>();
        var arcs = new List<ArcInstance>();
        var rays = new List<RayInstance>();
        Vector2 F(System.Windows.Point p) => new((float)(p.X - origin.X), (float)(p.Y - origin.Y));
        var entities=drawing.ToArray();
        foreach (var c in entities.Where(e=>e.Kind!="POINT").SelectMany(e=>e.RenderCurves??e.Curves))
            if (c.IsUnbounded) rays.Add(new RayInstance(F(c.Start), new((float)(c.End.X - c.Start.X), (float)(c.End.Y - c.Start.Y)), c.Extent == LineExtent.Infinite ? 1 : 0));
            else if (c.IsLine) { lines.Add(F(c.Start)); lines.Add(F(c.End)); }
            else arcs.Add(new ArcInstance(F(c.Center), (float)c.Radius, (float)c.Angle, (float)c.Sweep));
        arcs.Sort((a, b) => a.Radius.CompareTo(b.Radius));
        var bucketList = new List<ArcBucket>();
        for (int i = 0; i < arcs.Count;)
        {
            var key = Math.ILogB(arcs[i].Radius); int start = i;
            while (i < arcs.Count && Math.ILogB(arcs[i].Radius) == key) i++;
            bucketList.Add(new ArcBucket(start, i - start, arcs[i - 1].Radius));
        }
        lineBuffer?.Dispose(); arcBuffer?.Dispose(); lineBuffer = arcBuffer = null;
        if (lines.Count > 0) lineBuffer = device.CreateBuffer(CollectionsMarshal.AsSpan(lines), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        if (arcs.Count > 0) arcBuffer = device.CreateBuffer(CollectionsMarshal.AsSpan(arcs), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        lineVertexCount = lines.Count;
        buckets = [.. bucketList];
        rayBuffer?.Dispose(); rayBuffer = null; rayInstanceCount = rays.Count;
        if (rays.Count > 0) rayBuffer = device.CreateBuffer(CollectionsMarshal.AsSpan(rays), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        var dots=entities.SelectMany(e=>e.RenderDots).SelectMany(p=>Enumerable.Repeat(F(p),4)).ToArray();
        dotBuffer?.Dispose();dotBuffer=null;dotVertexCount=dots.Length;
        if(dots.Length>0)dotBuffer=device.CreateBuffer(dots.AsSpan(),BindFlags.VertexBuffer,ResourceUsage.Immutable);
        var triangles=entities.SelectMany(e=>e.FillTriangles??DxfPrimitives.Triangulate(e.FillContours)).Select(F).ToArray();
        var points=entities.Where(e=>e.Kind=="POINT").SelectMany(e=>Enumerable.Repeat(F(e.Curves[0].Start),4)).ToArray();
        fillBuffer?.Dispose();pointBuffer?.Dispose();fillBuffer=pointBuffer=null;
        fillVertexCount=triangles.Length;pointVertexCount=points.Length;
        if(triangles.Length>0)fillBuffer=device.CreateBuffer(triangles.AsSpan(),BindFlags.VertexBuffer,ResourceUsage.Immutable);
        if(points.Length>0)pointBuffer=device.CreateBuffer(points.AsSpan(),BindFlags.VertexBuffer,ResourceUsage.Immutable);
    }

    private void CreateTargets()
    {
        using (var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
            renderTarget = device.CreateRenderTargetView(backBuffer);
        using var surface = swapChain.GetBuffer<IDXGISurface>(0);
        d2dTarget = d2d.CreateBitmapFromDxgiSurface(surface, new D2D.BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw));
        d2d.Target = d2dTarget;
    }

    private void Resize(int w, int h)
    {
        if (w == width && h == height) return;
        width = w; height = h;
        d2d.Target = null; d2dTarget?.Dispose(); renderTarget?.Dispose();
        swapChain.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTargets();
    }

    /// <summary>
    /// Renders one frame. The camera maps world (x, y) to DIP screen (xÂ·scale + offset.X, âˆ’yÂ·scale + offset.Y);
    /// <paramref name="overlay"/> then draws on the Direct2D context in DIPs.
    /// </summary>
    public void Render(int pixelWidth, int pixelHeight, double dipScale, double scale, System.Windows.Vector offset,
        Color4 background, Color4 lineColor, Action<D2D.ID2D1DeviceContext> overlay)
    {
        if (pixelWidth < 1 || pixelHeight < 1) return;
        Resize(pixelWidth, pixelHeight);
        context.ClearRenderTargetView(renderTarget!, background);
        if (lineBuffer != null || arcBuffer != null || fillBuffer != null || pointBuffer != null || dotBuffer != null || rayBuffer != null)
        {
            context.OMSetRenderTargets(renderTarget!);
            context.RSSetViewport(0, 0, width, height);
            context.RSSetState(rasterizer);
            context.OMSetBlendState(blend);
            context.PSSetShader(pixelShader);
            context.VSSetConstantBuffer(0, constantBuffer);
            context.PSSetConstantBuffer(0, constantBuffer);
            double w = width / dipScale, h = height / dipScale;
            var constants = new Constants
            {
                Transform = new Vector4((float)(2 * scale / w), (float)(2 * scale / h),
                    (float)(2 * (Origin.X * scale + offset.X) / w - 1), (float)(1 - 2 * (offset.Y - Origin.Y * scale) / h)),
                Color = lineColor,
                Pad = new Vector3((float)(8/w),(float)(8/h),0),
            };
            if(fillBuffer!=null)
            {
                SetConstants(constants);context.IASetInputLayout(lineLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.IASetVertexBuffer(0,fillBuffer,(uint)Marshal.SizeOf<Vector2>());
                context.VSSetShader(lineShader);context.Draw((uint)fillVertexCount,0);
            }
            if(dotBuffer!=null)
            {
                var dotsConstants=constants;dotsConstants.Pad*=0.2f;SetConstants(dotsConstants);
                context.IASetInputLayout(lineLayout);context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                context.IASetVertexBuffer(0,dotBuffer,(uint)Marshal.SizeOf<Vector2>());
                context.VSSetShader(pointShader);context.Draw((uint)dotVertexCount,0);
            }
            if(pointBuffer!=null)
            {
                SetConstants(constants);context.IASetInputLayout(lineLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                context.IASetVertexBuffer(0,pointBuffer,(uint)Marshal.SizeOf<Vector2>());
                context.VSSetShader(pointShader);context.Draw((uint)pointVertexCount,0);
            }
            if (lineBuffer != null)
            {
                SetConstants(constants);
                context.IASetInputLayout(lineLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                context.IASetVertexBuffer(0, lineBuffer, (uint)Marshal.SizeOf<Vector2>());
                context.VSSetShader(lineShader);
                context.Draw((uint)lineVertexCount, 0);
            }
            if (rayBuffer != null)
            {
                SetConstants(constants);
                context.IASetInputLayout(rayLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                context.IASetVertexBuffer(0, rayBuffer, (uint)Marshal.SizeOf<RayInstance>());
                context.VSSetShader(rayShader);
                context.DrawInstanced(2, (uint)rayInstanceCount, 0, 0);
            }
            if (arcBuffer != null)
            {
                context.IASetInputLayout(arcLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.LineStrip);
                context.IASetVertexBuffer(0, arcBuffer, (uint)Marshal.SizeOf<ArcInstance>());
                context.VSSetShader(arcShader);
                foreach (var bucket in buckets)
                {
                    // Segment count per full turn that keeps the chord error under ~Â¼ pixel for the bucket's largest arc.
                    var radiusPx = bucket.MaxRadius * scale * dipScale;
                    var step = 2 * Math.Acos(Math.Max(-1, 1 - 0.25 / Math.Max(radiusPx, 1e-9)));
                    constants.Segments = (float)Math.Clamp(Math.Ceiling(Math.Tau / step), 8, 1024);
                    SetConstants(constants);
                    context.DrawInstanced((uint)constants.Segments + 1, (uint)bucket.Count, 0, (uint)bucket.Start);
                }
            }
        }
        d2d.SetDpi(96 * (float)dipScale, 96 * (float)dipScale);
        d2d.BeginDraw();
        overlay(d2d);
        d2d.EndDraw();
        FrameRendered?.Invoke();
        swapChain.Present(1, PresentFlags.None);
    }

    private unsafe void SetConstants(in Constants constants)
    {
        var mapped = context.Map(constantBuffer, 0, MapMode.WriteDiscard);
        *(Constants*)mapped.DataPointer = constants;
        context.Unmap(constantBuffer, 0);
    }

    public void Dispose()
    {
        d2d.Target = null;
        foreach (IDisposable? d in new IDisposable?[] { d2dTarget, renderTarget, lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer, pointShader, rayShader, dwrite, d2d, d2dFactory,
            blend, rasterizer, constantBuffer, rayLayout, arcLayout, lineLayout, pixelShader, arcShader, lineShader, swapChain, context, device })
            d?.Dispose();
    }
}
