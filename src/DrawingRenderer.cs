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
/// Model and paper (layout) geometry share the buffers as separate ranges; layout viewports
/// re-draw the model range through their own transform, clipped with a scissor rectangle.
/// </summary>
public sealed class DrawingRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Camera : register(b0) { float4 row0; float4 row1; float4 mono; float segments; float3 pad; float4 style; };
        // style.x: 1 = monochrome; style.y: annotation opacity; style.z: opacity of the whole pass.
        // groups: per layer+category flags, bit 0 visible, bit 1 annotation. Rewritten on visibility changes only.
        Buffer<uint> groups : register(t0);
        struct VSOut { float4 pos : SV_Position; nointerpolation float4 color : COLOR; };
        // Affine world-to-NDC transform; layout viewports add rotation (view twist) to the plain camera.
        float2 Linear(float2 v) { return float2(dot(row0.xy, v), dot(row1.xy, v)); }
        float4 Project(float2 p) { return float4(Linear(p) + float2(row0.z, row1.z), 0, 1); }
        VSOut Shade(float4 pos, float4 color, uint group)
        {
            VSOut o; uint flags = groups.Load(group);
            // Hidden groups move outside the clip volume, so clipping discards their primitives.
            o.pos = (flags & 1) ? pos : float4(-10, -10, 0, 1);
            o.color = style.x > 0.5 ? mono : color;
            o.color.a *= (flags & 2) ? min(style.y, style.z) : style.z;
            return o;
        }
        VSOut LineVS(float2 p : POSITION, float4 c : COLOR, uint g : GROUP) { return Shade(Project(p), c, g); }
        VSOut PointVS(float2 p : POSITION, float4 c : COLOR, uint g : GROUP, uint id : SV_VertexID)
        {
            float2 offsets[4] = {float2(-1,0),float2(1,0),float2(0,-1),float2(0,1)};
            float4 q=Project(p); q.xy+=offsets[id%4]*pad.xy; return Shade(q, c, g);
        }
        VSOut ArcVS(float2 center : CENTER, float radius : RADIUS, float2 angles : ANGLES, float4 c : COLOR, uint g : GROUP, uint id : SV_VertexID)
        {
            float a = angles.x + angles.y * (id / segments);
            return Shade(Project(center + radius * float2(cos(a), sin(a))), c, g);
        }
        // Rays/xlines: extend ±3 NDC around the point nearest the screen centre; the rasterizer clips the rest.
        VSOut RayVS(float2 origin : ORIGIN, float2 direction : DIRECTION, float infinite : KIND, float4 c : COLOR, uint g : GROUP, uint id : SV_VertexID)
        {
            float2 b = Project(origin).xy; float2 d = normalize(Linear(direction));
            float s0 = -dot(b, d); float s = s0 + (id == 0 ? -3 : 3);
            if (infinite == 0) s = max(s, 0);
            return Shade(float4(b + d * s, 0, 1), c, g);
        }
        float4 PS(VSOut i) : SV_Target { return i.color; }
        """;
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants { public Vector4 Row0; public Vector4 Row1; public Vector4 Color; public float Segments; public Vector3 Pad; public Vector4 Style; }
    // Color is packed RGBA (R in the low byte); Group indexes the visibility table.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(Vector2 Position, uint Color, uint Group);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ArcInstance(Vector2 Center, float Radius, float Start, float Sweep, uint Color, uint Group);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RayInstance(Vector2 Origin, Vector2 Direction, float Infinite, uint Color, uint Group);
    /// <summary>Group flag bits, see <see cref="SetGroupFlags"/>.</summary>
    public const uint GroupVisible = 1, GroupAnnotation = 2;
    private const float AnnotationOpacity = 0.45f;
    // Arcs are sorted by radius and drawn per power-of-two radius bucket, each with just enough segments.
    private readonly record struct ArcBucket(int Start, int Count, double MaxRadius);
    private readonly record struct Range(int Start, int Count);
    // One uploaded space (model or paper): its float origin and its ranges in the shared buffers.
    private sealed record Part(System.Windows.Point Origin, Range Lines, Range Fills, Range Points, Range Dots, Range Rays, ArcBucket[] Buckets);

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11VertexShader lineShader, arcShader, pointShader, rayShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout lineLayout, arcLayout, rayLayout;
    private readonly ID3D11Buffer constantBuffer;
    private readonly ID3D11RasterizerState rasterizer, scissorRasterizer;
    private readonly ID3D11BlendState blend;
    private readonly D2D.ID2D1Factory1 d2dFactory;
    private readonly D2D.ID2D1DeviceContext d2d;
    private readonly IDWriteFactory dwrite;
    private ID3D11RenderTargetView? renderTarget;
    private D2D.ID2D1Bitmap1? d2dTarget;
    private ID3D11Buffer? lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer;
    // Model-space counts and buckets; each layout's paper part follows the model ranges in every buffer.
    private int lineVertexCount,fillVertexCount,pointVertexCount,dotVertexCount,rayInstanceCount;
    private ArcBucket[] buckets = [];
    private Part? model;
    private Part[] papers = [];
    // Visibility table: one flag word per group; groupLayers maps groups to layers for per-viewport freezes.
    private ID3D11Buffer? groupBuffer;
    private ID3D11ShaderResourceView? groupView;
    private uint[] groupFlags = [GroupVisible];
    private int[] groupLayers = [];
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
        lineLayout = device.CreateInputLayout([
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 8, 0),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 12, 0)], lineCode.Span);
        arcLayout = device.CreateInputLayout([
            new InputElementDescription("CENTER", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("RADIUS", 0, Format.R32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("ANGLES", 0, Format.R32G32_Float, 12, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 24, 0, InputClassification.PerInstanceData, 1)], arcCode.Span);
        var rayCode = Compiler.Compile(ShaderSource, "RayVS", "drawing.hlsl", "vs_4_0");
        rayShader = device.CreateVertexShader(rayCode.Span);
        rayLayout = device.CreateInputLayout([
            new InputElementDescription("ORIGIN", 0, Format.R32G32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("DIRECTION", 0, Format.R32G32_Float, 8, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("KIND", 0, Format.R32_Float, 16, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("GROUP", 0, Format.R32_UInt, 24, 0, InputClassification.PerInstanceData, 1)], rayCode.Span);
        constantBuffer = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // Alpha-blended antialiased lines; MSAA is not available on flip-model swap chains.
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { AntialiasedLineEnable = true });
        scissorRasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { AntialiasedLineEnable = true, ScissorEnable = true });
        blend = device.CreateBlendState(BlendDescription.NonPremultiplied);

        d2dFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>();
        using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        d2d = d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);
        dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        CreateTargets();
    }

    public void SetDrawing(IEnumerable<Curve> curves, System.Windows.Point origin) => SetDrawing([new DxfEntity("LINE","0","",curves.ToList())],origin);

    public void SetDrawing(IEnumerable<DxfEntity> drawing, System.Windows.Point origin) => SetDrawing(drawing, origin, []);

    /// <summary>
    /// Uploads model space and every paper layout once; each keeps its own float origin for precision.
    /// <paramref name="groupLayers"/> maps <see cref="DxfEntity.Group"/> to a layer index for per-viewport freezes.
    /// </summary>
    public void SetDrawing(IEnumerable<DxfEntity> modelEntities, System.Windows.Point origin,
        IReadOnlyList<(IEnumerable<DxfEntity> Entities, System.Windows.Point Origin)> layouts, IReadOnlyList<int>? groupLayers = null)
    {
        Origin = origin;
        var lines = new List<Vertex>();
        var arcs = new List<ArcInstance>();
        var rays = new List<RayInstance>();
        var dots = new List<Vertex>();
        var triangles = new List<Vertex>();
        var points = new List<Vertex>();
        int maxGroup = 0;
        Part Collect(DxfEntity[] entities, System.Windows.Point o)
        {
            int l = lines.Count, a = arcs.Count, r = rays.Count, d = dots.Count, f = triangles.Count, p = points.Count;
            Vector2 F(System.Windows.Point q) => new((float)(q.X - o.X), (float)(q.Y - o.Y));
            foreach (var e in entities)
            {
                uint color = e.Color, group = (uint)e.Group; maxGroup = Math.Max(maxGroup, e.Group);
                Vertex V(System.Windows.Point q) => new(F(q), color, group);
                if (e.Kind == "POINT") { points.AddRange(Enumerable.Repeat(V(e.Curves[0].Start), 4)); continue; }
                foreach (var c in e.RenderCurves ?? e.Curves)
                    if (c.IsUnbounded) rays.Add(new RayInstance(F(c.Start), new((float)(c.End.X - c.Start.X), (float)(c.End.Y - c.Start.Y)), c.Extent == LineExtent.Infinite ? 1 : 0, color, group));
                    else if (c.IsLine) { lines.Add(V(c.Start)); lines.Add(V(c.End)); }
                    else arcs.Add(new ArcInstance(F(c.Center), (float)c.Radius, (float)c.Angle, (float)c.Sweep, color, group));
                foreach (var q in e.RenderDots) dots.AddRange(Enumerable.Repeat(V(q), 4));
                foreach (var q in e.FillTriangles ?? DxfPrimitives.Triangulate(e.FillContours)) triangles.Add(V(q));
            }
            arcs.Sort(a, arcs.Count - a, Comparer<ArcInstance>.Create((x, y) => x.Radius.CompareTo(y.Radius)));
            var bucketList = new List<ArcBucket>();
            for (int i = a; i < arcs.Count;)
            {
                var key = Math.ILogB(arcs[i].Radius); int start = i;
                while (i < arcs.Count && Math.ILogB(arcs[i].Radius) == key) i++;
                bucketList.Add(new ArcBucket(start, i - start, arcs[i - 1].Radius));
            }
            return new Part(o, new(l, lines.Count - l), new(f, triangles.Count - f), new(p, points.Count - p), new(d, dots.Count - d),
                new(r, rays.Count - r), [.. bucketList]);
        }
        model = Collect(modelEntities.ToArray(), origin);
        papers = [.. layouts.Select(l => Collect(l.Entities.ToArray(), l.Origin))];
        ID3D11Buffer? Upload<T>(List<T> data) where T : unmanaged =>
            data.Count == 0 ? null : device.CreateBuffer(CollectionsMarshal.AsSpan(data), BindFlags.VertexBuffer, ResourceUsage.Immutable);
        foreach (var old in new[] { lineBuffer, arcBuffer, rayBuffer, dotBuffer, fillBuffer, pointBuffer }) old?.Dispose();
        lineBuffer = Upload(lines); arcBuffer = Upload(arcs); rayBuffer = Upload(rays);
        dotBuffer = Upload(dots); fillBuffer = Upload(triangles); pointBuffer = Upload(points);
        lineVertexCount = model.Lines.Count; rayInstanceCount = model.Rays.Count; dotVertexCount = model.Dots.Count;
        fillVertexCount = model.Fills.Count; pointVertexCount = model.Points.Count; buckets = model.Buckets;
        // Visibility table: every group starts visible until the viewer applies its layer and category state.
        this.groupLayers = [.. groupLayers ?? []];
        var count = Math.Max(maxGroup + 1, this.groupLayers.Length);
        groupView?.Dispose(); groupBuffer?.Dispose();
        groupBuffer = device.CreateBuffer(new BufferDescription((uint)(count * sizeof(uint)), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        groupView = device.CreateShaderResourceView(groupBuffer, new ShaderResourceViewDescription(groupBuffer, Format.R32_UInt, 0, (uint)count));
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
    /// Renders one frame. The camera maps world (x, y) to DIP screen (x*scale + offset.X, -y*scale + offset.Y);
    /// <paramref name="overlay"/> then draws on the Direct2D context in DIPs. With a <paramref name="layout"/> index
    /// the frame shows that layout: each viewport's model view clipped to its window, then its paper space on top.
    /// </summary>
    public void Render(int pixelWidth, int pixelHeight, double dipScale, double scale, System.Windows.Vector offset,
        Color4 background, Color4 lineColor, Action<D2D.ID2D1DeviceContext> overlay,
        int layout = -1, IReadOnlyList<DxfViewportWindow>? viewports = null, bool monochrome = false)
    {
        if (pixelWidth < 1 || pixelHeight < 1) return;
        Resize(pixelWidth, pixelHeight);
        context.ClearRenderTargetView(renderTarget!, background);
        var paper = layout >= 0 && layout < papers.Length ? papers[layout] : null;
        if (model != null)
        {
            context.OMSetRenderTargets(renderTarget!);
            context.RSSetViewport(0, 0, width, height);
            context.OMSetBlendState(blend);
            context.PSSetShader(pixelShader);
            context.VSSetConstantBuffer(0, constantBuffer);
            context.PSSetConstantBuffer(0, constantBuffer);
            context.VSSetShaderResource(0, groupView!);
            var camera = new System.Windows.Media.Matrix(scale, 0, 0, -scale, offset.X, offset.Y);
            if (paper == null) { context.RSSetState(rasterizer); Draw(model, camera, dipScale, lineColor, 1, monochrome); }
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
                    if (viewport.FrozenLayers.Length > 0)
                        WriteGroups([.. groupFlags.Select((f, g) => g < groupLayers.Length && viewport.FrozenLayers.Contains(groupLayers[g]) ? f & ~GroupVisible : f)]);
                    Draw(model, viewport.ModelToPaper * camera, dipScale, lineColor, 1, monochrome);
                    if (viewport.FrozenLayers.Length > 0) WriteGroups(groupFlags);
                }
                context.RSSetState(rasterizer);
                Draw(paper, camera, dipScale, lineColor, 1, monochrome);
            }
        }
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
            Style = new Vector4(monochrome ? 1 : 0, AnnotationOpacity, opacity, 0),
        };
        var stride = (uint)Marshal.SizeOf<Vertex>();
        if(fillBuffer!=null&&part.Fills.Count>0)
        {
            SetConstants(constants);context.IASetInputLayout(lineLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0,fillBuffer,stride);
            context.VSSetShader(lineShader);context.Draw((uint)part.Fills.Count,(uint)part.Fills.Start);
        }
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
            var linearScale = Math.Sqrt(Math.Abs(m.Determinant));
            foreach (var bucket in part.Buckets)
            {
                // Segment count per full turn that keeps the chord error under ~1/4 pixel for the bucket's largest arc.
                var radiusPx = bucket.MaxRadius * linearScale * dipScale;
                var step = 2 * Math.Acos(Math.Max(-1, 1 - 0.25 / Math.Max(radiusPx, 1e-9)));
                constants.Segments = (float)Math.Clamp(Math.Ceiling(Math.Tau / step), 8, 1024);
                SetConstants(constants);
                context.DrawInstanced((uint)constants.Segments + 1, (uint)bucket.Count, 0, (uint)bucket.Start);
            }
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
        d2d.Target = null;
        foreach (IDisposable? d in new IDisposable?[] { groupView, groupBuffer, d2dTarget, renderTarget, lineBuffer, arcBuffer, fillBuffer, pointBuffer, dotBuffer, rayBuffer, pointShader, rayShader, dwrite, d2d, d2dFactory,
            blend, rasterizer, scissorRasterizer, constantBuffer, rayLayout, arcLayout, lineLayout, pixelShader, arcShader, lineShader, swapChain, context, device })
            d?.Dispose();
    }
}
