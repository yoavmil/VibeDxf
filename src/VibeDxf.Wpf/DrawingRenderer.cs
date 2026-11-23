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
namespace VibeDxf.Wpf;

/// <summary>
/// Draws the whole drawing with Direct3D 11 from GPU buffers uploaded once per document;
/// a camera change only rewrites one constant buffer. Overlays are drawn with Direct2D on the same back buffer.
/// Model and paper (layout) geometry share the buffers as separate ranges; layout viewports
/// re-draw the model range through their own transform, clipped with a scissor rectangle.
/// </summary>
internal sealed class DrawingRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Camera : register(b0) { float4 row0; float4 row1; float4 mono; float segments; float3 pad; float4 style; };
        // style.x: 1 = monochrome; style.y: annotation opacity; style.z: opacity of the whole pass; style.w: 1 = light background (COLOR1).
        // groups: per layer+category flags, bit 0 visible, bit 1 annotation. Rewritten on visibility changes only.
        Buffer<uint> groups : register(t0);
        struct VSOut { float4 pos : SV_Position; nointerpolation float4 color : COLOR; };
        // Affine world-to-NDC transform; layout viewports add rotation (view twist) to the plain camera.
        float2 Linear(float2 v) { return float2(dot(row0.xy, v), dot(row1.xy, v)); }
        float4 Project(float2 p) { return float4(Linear(p) + float2(row0.z, row1.z), 0, 1); }
        VSOut Shade(float4 pos, float4 color, float4 light, uint group)
        {
            VSOut o; uint flags = groups.Load(group);
            // Hidden groups move outside the clip volume, so clipping discards their primitives.
            o.pos = (flags & 1) ? pos : float4(-10, -10, 0, 1);
            o.color = style.x > 0.5 ? mono : (style.w > 0.5 ? light : color);
            o.color.a *= (flags & 2) ? min(style.y, style.z) : style.z;
            return o;
        }
        float4 Project3D(float3 p) { return float4(Linear(p.xy) + float2(row0.z,row1.z), min(1,p.z*(1+pad.z)),1); }
        VSOut LineVS(float3 p : POSITION, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP) { return Shade(Project3D(p), c, l, g); }
        VSOut PointVS(float3 p : POSITION, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP, uint id : SV_VertexID)
        {
            float2 offsets[4] = {float2(-1,0),float2(1,0),float2(0,-1),float2(0,1)};
            float4 q=Project3D(p); q.xy+=offsets[id%4]*pad.xy; return Shade(q, c, l, g);
        }
        VSOut ArcVS(float2 center : CENTER, float radius : RADIUS, float2 angles : ANGLES, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP, uint id : SV_VertexID)
        {
            float a = angles.x + angles.y * (id / segments);
            return Shade(Project(center + radius * float2(cos(a), sin(a))), c, l, g);
        }
        // Filled discs (SVG filled circles): a triangle fan expanded as a TriangleList, one instance per disc.
        VSOut DiscVS(float2 center : CENTER, float radius : RADIUS, float2 angles : ANGLES, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP, uint id : SV_VertexID)
        {
            uint k = id % 3; float a = angles.x + angles.y * ((id / 3 + k - 1) / segments);
            float2 p = k == 0 ? center : center + radius * float2(cos(a), sin(a));
            return Shade(Project(p), c, l, g);
        }
        // Cubic Béziers (SVG paths/ellipses): one instance per curve, tessellated for the current zoom like arcs.
        VSOut CubicVS(float2 p0 : CONTROL0, float2 p1 : CONTROL1, float2 p2 : CONTROL2, float2 p3 : CONTROL3, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP, uint id : SV_VertexID)
        {
            float t = id / segments; float u = 1 - t;
            return Shade(Project(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3), c, l, g);
        }
        // Rays/xlines: extend ±3 NDC around the point nearest the screen centre; the rasterizer clips the rest.
        VSOut RayVS(float2 origin : ORIGIN, float2 direction : DIRECTION, float infinite : KIND, float4 c : COLOR, float4 l : COLOR1, uint g : GROUP, uint id : SV_VertexID)
        {
            float2 b = Project(origin).xy; float2 d = normalize(Linear(direction));
            float s0 = -dot(b, d); float s = s0 + (id == 0 ? -3 : 3);
            if (infinite == 0) s = max(s, 0);
            return Shade(float4(b + d * s, 0, 1), c, l, g);
        }
        float4 PS(VSOut i) : SV_Target { return i.color; }
        """;
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Vector4 Row0; public Vector4 Row1; public Vector4 Color; public float Segments; public Vector3 Pad; public Vector4 Style; }
    // Color/Light are packed RGBA (R in the low byte) for dark/light backgrounds; Group indexes the visibility table.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(Vector3 Position, uint Color, uint Group, uint Light);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ArcInstance(Vector2 Center, float Radius, float Start, float Sweep, uint Color, uint Group, uint Light);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CubicInstance(Vector2 P0, Vector2 P1, Vector2 P2, Vector2 P3, uint Color, uint Group, uint Light);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RayInstance(Vector2 Origin, Vector2 Direction, float Infinite, uint Color, uint Group, uint Light);
    /// <summary>Group flag bits, see <see cref="SetGroupFlags"/>.</summary>
    public const uint GroupVisible = 1, GroupAnnotation = 2;
    private const float AnnotationOpacity = 0.45f;
    // Arcs are sorted by radius and drawn per power-of-two radius bucket, each with just enough segments.
    private readonly record struct ArcBucket(int Start, int Count, double MaxRadius);
    private readonly record struct Range(int Start, int Count);
    // One uploaded space (model or paper): its float origin and its ranges in the shared buffers.
    private sealed record Part(System.Windows.Point Origin, Range Lines, Range Fills, Range Points, Range Dots, Range Rays, ArcBucket[] Buckets, bool Depth, ArcBucket[] Discs, ArcBucket[] Cubics);

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11VertexShader lineShader, arcShader, pointShader, rayShader, discShader, cubicShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout lineLayout, arcLayout, rayLayout, cubicLayout;
    private readonly ID3D11Buffer constantBuffer;
    private readonly ID3D11RasterizerState rasterizer, scissorRasterizer;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11DepthStencilState depthState,flatDepthState;
    private ID3D11Texture2D? depthTexture;
    private ID3D11DepthStencilView? depthTarget;
    private readonly D2D.ID2D1Factory1 d2dFactory;
    private readonly D2D.ID2D1DeviceContext d2d;
    private readonly IDWriteFactory dwrite;
    private ID3D11RenderTargetView? renderTarget;
    private D2D.ID2D1Bitmap1? d2dTarget;
    private ID3D11Buffer? lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer, discBuffer, cubicBuffer;
    // Model-space counts and buckets; each layout's paper part follows the model ranges in every buffer.
    private int lineVertexCount,fillVertexCount,pointVertexCount,dotVertexCount,rayInstanceCount;
    private ArcBucket[] buckets = [];
    private Part? model;
    private Part[] papers = [];
    private Dictionary<DxfViewportWindow,Part> projectedParts = [];
    // Visibility table: one flag word per group; groupLayers maps groups to layers for per-viewport freezes.
    private ID3D11Buffer? groupBuffer;
    private ID3D11ShaderResourceView? groupView;
    private uint[] groupFlags = [GroupVisible];
    private int[] groupLayers = [];
    private int width, height;
    private uint samples = 1;
    private ID3D11Texture2D? backBuffer, msaaTexture;
    private ID3D11RenderTargetView? msaaTarget;
    private bool lightBackground;
    /// <summary>World origin subtracted before converting to float, so large DXF coordinates keep precision.</summary>
    public System.Windows.Point Origin { get; private set; }
    public D2D.ID2D1Factory1 Factory2D => d2dFactory;
    public IDWriteFactory Write => dwrite;
    // Allows offline rendering checks to read the completed GPU frame before flip-discard presentation.
    internal Action? FrameRendered { get; set; }
    // Regression instrumentation for candidate-resource rollback; never part of the host API.
    internal Action? BeforeDrawingCommit { get; set; }

    public DrawingRenderer(IntPtr hwnd, int width, int height)
    {
        try
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
        discShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "DiscVS", "drawing.hlsl", "vs_4_0").Span);
        var cubicCode = Compiler.Compile(ShaderSource, "CubicVS", "drawing.hlsl", "vs_4_0");
        cubicShader = device.CreateVertexShader(cubicCode.Span);
        cubicLayout = device.CreateInputLayout([
            new InputElementDescription("CONTROL", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("CONTROL", 1, Format.R32G32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("CONTROL", 2, Format.R32G32_Float, 16, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("CONTROL", 3, Format.R32G32_Float, 24, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 32, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 36, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 1, Format.R8G8B8A8_UNorm, 40, 0, InputClassification.PerInstanceData, 1)], cubicCode.Span);
        pointShader = device.CreateVertexShader(Compiler.Compile(ShaderSource,"PointVS","drawing.hlsl","vs_4_0").Span);
        pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "drawing.hlsl", "ps_4_0").Span);
        lineLayout = device.CreateInputLayout([
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 12, 0),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 16, 0),
            new InputElementDescription("COLOR", 1, Format.R8G8B8A8_UNorm, 20, 0)], lineCode.Span);
        arcLayout = device.CreateInputLayout([
            new InputElementDescription("CENTER", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("RADIUS", 0, Format.R32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ANGLES", 0, Format.R32G32_Float, 12, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 24, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 1, Format.R8G8B8A8_UNorm, 28, 0, InputClassification.PerInstanceData, 1)], arcCode.Span);
        var rayCode = Compiler.Compile(ShaderSource, "RayVS", "drawing.hlsl", "vs_4_0");
        rayShader = device.CreateVertexShader(rayCode.Span);
        rayLayout = device.CreateInputLayout([
            new InputElementDescription("ORIGIN", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("DIRECTION", 0, Format.R32G32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("KIND", 0, Format.R32_Float, 16, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 24, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 1, Format.R8G8B8A8_UNorm, 28, 0, InputClassification.PerInstanceData, 1)], rayCode.Span);
        constantBuffer = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // Flip-model swap chains cannot be multisampled, so geometry renders into an MSAA texture that is resolved into the
        // back buffer before Direct2D overlays. Alpha "antialiased lines" are a fallback only (most drivers draw them aliased).
        foreach (var count in new uint[] { 8, 4, 2 })
            if (device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, count) > 0 && device.CheckMultisampleQualityLevels(Format.D32_Float, count) > 0) { samples = count; break; }
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { AntialiasedLineEnable = samples == 1, MultisampleEnable = samples > 1 });
        scissorRasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { AntialiasedLineEnable = samples == 1, MultisampleEnable = samples > 1, ScissorEnable = true });
        blend = device.CreateBlendState(BlendDescription.NonPremultiplied);
        depthState=device.CreateDepthStencilState(new DepthStencilDescription{DepthEnable=true,DepthWriteMask=DepthWriteMask.All,DepthFunc=ComparisonFunction.GreaterEqual});
        flatDepthState=device.CreateDepthStencilState(new DepthStencilDescription{DepthEnable=false,DepthWriteMask=DepthWriteMask.Zero});

        d2dFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>();
        using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        d2d = d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);
        dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        CreateTargets();
        }
        catch { Dispose(); throw; }
    }

    public void SetDrawing(IEnumerable<Curve> curves, System.Windows.Point origin) => SetDrawing([new DxfEntity("LINE","0","",curves.ToList())],origin);

    public void SetDrawing(IEnumerable<DxfEntity> drawing, System.Windows.Point origin) => SetDrawing(drawing, origin, []);

    /// <summary>
    /// Uploads model space and every paper layout once; each keeps its own float origin for precision.
    /// <paramref name="groupLayers"/> maps <see cref="DxfEntity.Group"/> to a layer index for per-viewport freezes.
    /// </summary>
    public void SetDrawing(IEnumerable<DxfEntity> modelEntities, System.Windows.Point origin,
        IReadOnlyList<(IEnumerable<DxfEntity> Entities, System.Windows.Point Origin)> layouts, IReadOnlyList<int>? groupLayers = null,
        CancellationToken cancellationToken = default, IReadOnlyList<DxfViewportWindow>? projectedViewports = null, bool sourceColorsOnLight = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = new List<Vertex>();
        var arcs = new List<ArcInstance>();
        var discs = new List<ArcInstance>();
        var cubics = new List<CubicInstance>(); var curvature = new List<float>();
        var rays = new List<RayInstance>();
        var dots = new List<Vertex>();
        var triangles = new List<Vertex>();
        var points = new List<Vertex>();
        int maxGroup = 0;
        Part Collect(DxfEntity[] entities, System.Windows.Point o)
        {
            int l = lines.Count, a = arcs.Count, r = rays.Count, d = dots.Count, f = triangles.Count, p = points.Count, k = discs.Count, b = cubics.Count;
            Vector2 F(System.Windows.Point q) => new((float)(q.X - o.X), (float)(q.Y - o.Y));
            foreach (var e in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint color = DxfDisplayColors.ForDarkBackground(e.Color), group = (uint)e.Group; maxGroup = Math.Max(maxGroup, e.Group);
                // Light background: SVG keeps its paper-authored source colors; DXF darkens colors that would vanish on white.
                uint light = sourceColorsOnLight ? e.Color : DxfColors.ForLightBackground(e.Color);
                uint fillSource = e.FillColor ?? e.Color, fill = DxfDisplayColors.ForDarkBackground(fillSource),
                    fillLight = sourceColorsOnLight ? fillSource : DxfColors.ForLightBackground(fillSource);
                Vertex V(System.Windows.Point q) => new(new Vector3(F(q),0), color, group, light);
                Vertex V3(System.Windows.Media.Media3D.Point3D q)=>new(new((float)(q.X-o.X),(float)(q.Y-o.Y),(float)q.Z),color,group,light);
                if(e.DisplayGeometry3D is { } spatial)
                {
                    foreach(var line in spatial.Lines){lines.Add(V3(line.Start));lines.Add(V3(line.End));}
                    foreach(var triangle in spatial.Triangles){triangles.Add(V3(triangle.A));triangles.Add(V3(triangle.B));triangles.Add(V3(triangle.C));}
                    foreach(var q in spatial.Dots)dots.AddRange(Enumerable.Repeat(V3(q),4));
                    continue;
                }
                if (e.Kind == "POINT") { points.AddRange(Enumerable.Repeat(V(e.Curves[0].Start), 4)); continue; }
                foreach (var c in e.RenderCurves ?? e.Curves)
                    if (c.IsUnbounded) rays.Add(new RayInstance(F(c.Start), new((float)(c.End.X - c.Start.X), (float)(c.End.Y - c.Start.Y)), c.Extent == LineExtent.Infinite ? 1 : 0, color, group, light));
                    else if (c.IsLine) { lines.Add(V(c.Start)); lines.Add(V(c.End)); }
                    else arcs.Add(new ArcInstance(F(c.Center), (float)c.Radius, (float)c.Angle, (float)c.Sweep, color, group, light));
                foreach (var q in e.RenderDots) dots.AddRange(Enumerable.Repeat(V(q), 4));
                foreach (var q in e.RenderCubics ?? []) { cubics.Add(new CubicInstance(F(q.P0), F(q.P1), F(q.P2), F(q.P3), color, group, light)); curvature.Add((float)q.Curvature); }
                foreach (var q in e.FillTriangles ?? DxfPrimitives.Triangulate(e.FillContours)) triangles.Add(new(new Vector3(F(q), 0), fill, group, fillLight));
                if (e.FillDisc && e.Curves is [{ IsLine: false } disc]) discs.Add(new ArcInstance(F(disc.Center), (float)disc.Radius, 0, (float)Math.Tau, fill, group, fillLight));
            }
            ArcBucket[] Buckets(List<ArcInstance> list, int first)
            {
                list.Sort(first, list.Count - first, Comparer<ArcInstance>.Create((x, y) => x.Radius.CompareTo(y.Radius)));
                var result = new List<ArcBucket>();
                for (int i = first; i < list.Count;)
                {
                    var key = Math.ILogB(list[i].Radius); int start = i;
                    while (i < list.Count && Math.ILogB(list[i].Radius) == key) i++;
                    result.Add(new ArcBucket(start, i - start, list[i - 1].Radius));
                }
                return [.. result];
            }
            var bucketList = Buckets(arcs, a); var discBuckets = Buckets(discs, k);
            // Cubics are bucketed by their second-difference magnitude, which bounds the chord error for n segments.
            var order = Enumerable.Range(b, cubics.Count - b).OrderBy(i => curvature[i]).ToArray();
            var sorted = order.Select(i => cubics[i]).ToArray(); var sortedCurvature = order.Select(i => curvature[i]).ToArray();
            for (int i = 0; i < sorted.Length; i++) { cubics[b + i] = sorted[i]; curvature[b + i] = sortedCurvature[i]; }
            var cubicBuckets = new List<ArcBucket>();
            for (int i = b; i < cubics.Count;)
            {
                var key = Math.ILogB(Math.Max(curvature[i], 1e-30f)); int start = i;
                while (i < cubics.Count && Math.ILogB(Math.Max(curvature[i], 1e-30f)) == key) i++;
                cubicBuckets.Add(new ArcBucket(start, i - start, curvature[i - 1]));
            }
            return new Part(o, new(l, lines.Count - l), new(f, triangles.Count - f), new(p, points.Count - p), new(d, dots.Count - d),
                new(r, rays.Count - r), bucketList,entities.Any(e=>e.DisplayGeometry3D!=null), discBuckets, [.. cubicBuckets]);
        }
        var nextModel = Collect(modelEntities.ToArray(), origin);
        var nextPapers = layouts.Select(l => Collect(l.Entities.ToArray(), l.Origin)).ToArray();
                var nextProjected = (projectedViewports??[]).Where(v=>v.ProjectedEntities!=null).ToDictionary(v=>v,v=>
        {
            var entities=v.ProjectedEntities!.ToArray();var bounds=entities.Aggregate(System.Windows.Rect.Empty,(r,e)=>{r.Union(e.Bounds);return r;});
            var center=bounds.IsEmpty?default:new System.Windows.Point(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2);
            return Collect(entities,center);
        });
        ID3D11Buffer? Upload<T>(List<T> data) where T : unmanaged =>
            data.Count == 0 ? null : device.CreateBuffer(CollectionsMarshal.AsSpan(data), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        var nextBuffers = new ID3D11Buffer?[8];
        ID3D11Buffer? nextGroups = null;
        ID3D11ShaderResourceView? nextGroupView = null;
        var nextGroupLayers = (groupLayers ?? Array.Empty<int>()).ToArray();
        var count = Math.Max(maxGroup + 1, nextGroupLayers.Length);
        try
        {
            nextBuffers[0] = Upload(lines); nextBuffers[1] = Upload(arcs); nextBuffers[2] = Upload(rays);
            nextBuffers[3] = Upload(dots); nextBuffers[4] = Upload(triangles); nextBuffers[5] = Upload(points); nextBuffers[6] = Upload(discs); nextBuffers[7] = Upload(cubics);
            nextGroups = device.CreateBuffer(new BufferDescription((uint)(count * sizeof(uint)), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            nextGroupView = device.CreateShaderResourceView(nextGroups, new ShaderResourceViewDescription(nextGroups, Format.R32_UInt, 0, (uint)count));
            BeforeDrawingCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            foreach (var buffer in nextBuffers) buffer?.Dispose();
            nextGroupView?.Dispose(); nextGroups?.Dispose(); throw;
        }
        foreach (var old in new[] { lineBuffer, arcBuffer, rayBuffer, dotBuffer, fillBuffer, pointBuffer, discBuffer, cubicBuffer }) old?.Dispose();
        lineBuffer = nextBuffers[0]; arcBuffer = nextBuffers[1]; rayBuffer = nextBuffers[2];
        dotBuffer = nextBuffers[3]; fillBuffer = nextBuffers[4]; pointBuffer = nextBuffers[5]; discBuffer = nextBuffers[6]; cubicBuffer = nextBuffers[7];
        Origin = origin; model = nextModel; papers = nextPapers; projectedParts = nextProjected;
        lineVertexCount = model.Lines.Count; rayInstanceCount = model.Rays.Count; dotVertexCount = model.Dots.Count;
        fillVertexCount = model.Fills.Count; pointVertexCount = model.Points.Count; buckets = model.Buckets;
        // Visibility table: every group starts visible until the viewer applies its layer and category state.
        this.groupLayers = nextGroupLayers;
        groupView?.Dispose(); groupBuffer?.Dispose();
        groupBuffer = nextGroups; groupView = nextGroupView;
        groupFlags = Enumerable.Repeat(GroupVisible, count).ToArray();
        WriteGroups(groupFlags);
    }

    /// <summary>
    /// Sets each group's flags (<see cref="GroupVisible"/>, <see cref="GroupAnnotation"/>). This rewrites only the small
    /// visibility table; geometry buffers are never re-uploaded for visibility or color changes.
    /// </summary>
    public void SetGroupFlags(IReadOnlyList<uint> flags)
    {
        for (int i = 0; i < groupFlags.Length; i++) groupFlags[i] = i < flags.Count ? flags[i] : GroupVisible;
        WriteGroups(groupFlags);
    }

    private unsafe void WriteGroups(uint[] flags)
    {
        if (groupBuffer == null) return;
        var mapped = context.Map(groupBuffer, 0, MapMode.WriteDiscard);
        fixed (uint* source = flags) Buffer.MemoryCopy(source, (void*)mapped.DataPointer, flags.Length * sizeof(uint), flags.Length * sizeof(uint));
        context.Unmap(groupBuffer, 0);
    }

    private void CreateTargets()
    {
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = device.CreateRenderTargetView(backBuffer);
        if (samples > 1)
        {
            msaaTexture = device.CreateTexture2D(new Texture2DDescription { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = backBuffer.Description.Format, SampleDescription = new(samples, 0), BindFlags = BindFlags.RenderTarget });
            msaaTarget = device.CreateRenderTargetView(msaaTexture);
        }
        using var surface = swapChain.GetBuffer<IDXGISurface>(0);
        d2dTarget = d2d.CreateBitmapFromDxgiSurface(surface, new D2D.BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw));
        d2d.Target = d2dTarget;
        depthTexture=device.CreateTexture2D(new Texture2DDescription{Width=(uint)width,Height=(uint)height,MipLevels=1,ArraySize=1,Format=Format.D32_Float,SampleDescription=new(samples,0),BindFlags=BindFlags.DepthStencil});
        depthTarget=device.CreateDepthStencilView(depthTexture);
    }

    private void Resize(int w, int h)
    {
        if (w == width && h == height) return;
        width = w; height = h;
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        d2d.Target = null; d2dTarget?.Dispose(); renderTarget?.Dispose();depthTarget?.Dispose();depthTexture?.Dispose();
        msaaTarget?.Dispose(); msaaTexture?.Dispose(); backBuffer?.Dispose(); msaaTarget = null; msaaTexture = null; backBuffer = null;
        swapChain.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTargets();
    }

    /// <summary>
    /// Renders one frame. The camera maps world (x, y) to DIP screen (x*scale + offset.X, -y*scale + offset.Y);
    /// <paramref name="overlay"/> then draws on the Direct2D context in DIPs. With a <paramref name="layout"/> index
    /// the frame shows that layout: each viewport's model view clipped to its window, then its paper space on top.
    /// </summary>
    public void Render(int pixelWidth, int pixelHeight, double dipScale, double scale, System.Windows.Vector offset,
        Color4 background, Color4 lineColor, Action<D2D.ID2D1DeviceContext> overlay,
        int layout = -1, IReadOnlyList<DxfViewportWindow>? viewports = null, bool monochrome = false,DxfViewportWindow? modelView=null, bool lightBackground = false)
    {
        this.lightBackground = lightBackground;
        if (pixelWidth < 1 || pixelHeight < 1) return;
        Resize(pixelWidth, pixelHeight);
        var target = msaaTarget ?? renderTarget!;
        context.ClearRenderTargetView(target, background);
        var paper = layout >= 0 && layout < papers.Length ? papers[layout] : null;
        if (model != null)
        {
            context.OMSetRenderTargets(target,depthTarget!);
            context.ClearDepthStencilView(depthTarget!,DepthStencilClearFlags.Depth,0,0);
            context.RSSetViewport(0, 0, width, height);
            context.OMSetBlendState(blend);
            context.PSSetShader(pixelShader);
            context.VSSetConstantBuffer(0, constantBuffer);
            context.PSSetConstantBuffer(0, constantBuffer);
            context.VSSetShaderResource(0, groupView!);
            var camera = new System.Windows.Media.Matrix(scale, 0, 0, -scale, offset.X, offset.Y);
            if (paper == null) { context.RSSetState(rasterizer); Draw(modelView!=null&&projectedParts.TryGetValue(modelView,out var displayed)?displayed:model, (modelView?.ModelToPaper??System.Windows.Media.Matrix.Identity)*camera, dipScale, lineColor, 1, monochrome); }
            else
            {
                context.RSSetState(scissorRasterizer);
                foreach (var viewport in viewports ?? [])
                {
                    var a = camera.Transform(viewport.Paper.TopLeft); var b = camera.Transform(viewport.Paper.BottomRight);
                    int Clamp(double v, int max) => (int)Math.Clamp(Math.Round(v * dipScale), 0, max);
                    int left = Clamp(Math.Min(a.X, b.X), width), right = Clamp(Math.Max(a.X, b.X), width);
                    int top = Clamp(Math.Min(a.Y, b.Y), height), bottom = Clamp(Math.Max(a.Y, b.Y), height);
                    if (right <= left || bottom <= top) continue;
                    context.RSSetScissorRect(new Vortice.RawRect(left, top, right, bottom));
                    // Layers frozen in this viewport only: hide their groups for this pass.
                    if (viewport.FrozenLayers.Count > 0)
                        WriteGroups([.. groupFlags.Select((f, g) => g < groupLayers.Length && viewport.FrozenLayers.Contains(groupLayers[g]) ? f & ~GroupVisible : f)]);
                    context.ClearDepthStencilView(depthTarget!,DepthStencilClearFlags.Depth,0,0);
                    Draw(projectedParts.TryGetValue(viewport,out var projected)?projected:model, viewport.ModelToPaper * camera, dipScale, lineColor, 1, monochrome);
                    if (viewport.FrozenLayers.Count > 0) WriteGroups(groupFlags);
                }
                context.RSSetState(rasterizer);
                Draw(paper, camera, dipScale, lineColor, 1, monochrome);
            }
        }
        if (msaaTexture != null) context.ResolveSubresource(backBuffer!, 0, msaaTexture, 0, backBuffer!.Description.Format);
        d2d.SetDpi(96 * (float)dipScale, 96 * (float)dipScale);
        d2d.BeginDraw();
        overlay(d2d);
        d2d.EndDraw();
        FrameRendered?.Invoke();
        swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Draws one uploaded space through an affine world-to-DIP transform.</summary>
    private void Draw(Part part, System.Windows.Media.Matrix worldToDip, double dipScale, Color4 lineColor, float opacity, bool monochrome)
    {
        context.OMSetDepthStencilState(part.Depth?depthState:flatDepthState);
        double w = width / dipScale, h = height / dipScale;
        var m = worldToDip; var o = part.Origin;
        // Vertices are relative to the part origin; fold it into the translation in double precision.
        double cx = o.X * m.M11 + o.Y * m.M21 + m.OffsetX, cy = o.X * m.M12 + o.Y * m.M22 + m.OffsetY;
        var constants = new Constants
        {
            Row0 = new Vector4((float)(2 * m.M11 / w), (float)(2 * m.M21 / w), (float)(2 * cx / w - 1), 0),
            Row1 = new Vector4((float)(-2 * m.M12 / h), (float)(-2 * m.M22 / h), (float)(1 - 2 * cy / h), 0),
            Color = lineColor,
            Pad = new Vector3((float)(8/w),(float)(8/h),0),
            Style = new Vector4(monochrome ? 1 : 0, AnnotationOpacity, opacity, lightBackground ? 1 : 0),
        };
        var stride = (uint)Marshal.SizeOf<Vertex>();
        if(fillBuffer!=null&&part.Fills.Count>0)
        {
            SetConstants(constants);context.IASetInputLayout(lineLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0,fillBuffer,stride);
            context.VSSetShader(lineShader);context.Draw((uint)part.Fills.Count,(uint)part.Fills.Start);
        }
        if (discBuffer != null && part.Discs.Length > 0)
        {
            context.IASetInputLayout(arcLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0, discBuffer, (uint)Marshal.SizeOf<ArcInstance>());
            context.VSSetShader(discShader);
            DrawBuckets(part.Discs, m, dipScale, constants, 3, 0);
        }
        constants.Pad.Z=part.Depth?1e-6f:0;
        if(dotBuffer!=null&&part.Dots.Count>0)
        {
            var dotsConstants=constants;dotsConstants.Pad*=0.2f;SetConstants(dotsConstants);
            context.IASetInputLayout(lineLayout);context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            context.IASetVertexBuffer(0,dotBuffer,stride);
            context.VSSetShader(pointShader);context.Draw((uint)part.Dots.Count,(uint)part.Dots.Start);
        }
        if(pointBuffer!=null&&part.Points.Count>0)
        {
            SetConstants(constants);context.IASetInputLayout(lineLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            context.IASetVertexBuffer(0,pointBuffer,stride);
            context.VSSetShader(pointShader);context.Draw((uint)part.Points.Count,(uint)part.Points.Start);
        }
        if (lineBuffer != null && part.Lines.Count > 0)
        {
            SetConstants(constants);
            context.IASetInputLayout(lineLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            context.IASetVertexBuffer(0, lineBuffer, stride);
            context.VSSetShader(lineShader);
            context.Draw((uint)part.Lines.Count, (uint)part.Lines.Start);
        }
        if (rayBuffer != null && part.Rays.Count > 0)
        {
            SetConstants(constants);
            context.IASetInputLayout(rayLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            context.IASetVertexBuffer(0, rayBuffer, (uint)Marshal.SizeOf<RayInstance>());
            context.VSSetShader(rayShader);
            context.DrawInstanced(2, (uint)part.Rays.Count, 0, (uint)part.Rays.Start);
        }
        if (arcBuffer != null && part.Buckets.Length > 0)
        {
            context.IASetInputLayout(arcLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.LineStrip);
            context.IASetVertexBuffer(0, arcBuffer, (uint)Marshal.SizeOf<ArcInstance>());
            context.VSSetShader(arcShader);
            DrawBuckets(part.Buckets, m, dipScale, constants, 1, 1);
        }
        if (cubicBuffer != null && part.Cubics.Length > 0)
        {
            context.IASetInputLayout(cubicLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.LineStrip);
            context.IASetVertexBuffer(0, cubicBuffer, (uint)Marshal.SizeOf<CubicInstance>());
            context.VSSetShader(cubicShader);
            var pixels = Math.Sqrt(Math.Abs(m.Determinant)) * dipScale;
            foreach (var bucket in part.Cubics)
            {
                // Uniform sampling error ≤ 6·M·s/(8·n²): n = √(3·M·s) keeps it under ~1/4 pixel.
                constants.Segments = (float)Math.Clamp(Math.Ceiling(Math.Sqrt(3 * bucket.MaxRadius * pixels)), 1, 1024);
                SetConstants(constants);
                context.DrawInstanced((uint)constants.Segments + 1, (uint)bucket.Count, 0, (uint)bucket.Start);
            }
        }
    }

    // Segments per full turn keep the chord error under ~1/4 pixel for each bucket's largest arc or disc.
    private void DrawBuckets(ArcBucket[] list, System.Windows.Media.Matrix m, double dipScale, Constants constants, uint perSegment, uint extra)
    {
        var linearScale = Math.Sqrt(Math.Abs(m.Determinant));
        foreach (var bucket in list)
        {
            var radiusPx = bucket.MaxRadius * linearScale * dipScale;
            var step = 2 * Math.Acos(Math.Max(-1, 1 - 0.25 / Math.Max(radiusPx, 1e-9)));
            constants.Segments = (float)Math.Clamp(Math.Ceiling(Math.Tau / step), 8, 1024);
            SetConstants(constants);
            context.DrawInstanced((uint)constants.Segments * perSegment + extra, (uint)bucket.Count, 0, (uint)bucket.Start);
        }
    }

    private unsafe void SetConstants(in Constants constants)
    {
        var mapped = context.Map(constantBuffer, 0, MapMode.WriteDiscard);
        *(Constants*)mapped.DataPointer = constants;
        context.Unmap(constantBuffer, 0);
    }

    public void Dispose()
    {
        if (d2d != null) d2d.Target = null;
        foreach (IDisposable? d in new IDisposable?[] { msaaTarget, msaaTexture, backBuffer, depthTarget,depthTexture,depthState,flatDepthState,groupView, groupBuffer, d2dTarget, renderTarget, lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer, discBuffer, cubicBuffer, pointShader, rayShader, discShader, cubicShader, cubicLayout, dwrite, d2d, d2dFactory,
            blend, rasterizer, scissorRasterizer, constantBuffer, rayLayout, arcLayout, lineLayout, pixelShader, arcShader, lineShader, swapChain, context, device })
            d?.Dispose();
    }
}
