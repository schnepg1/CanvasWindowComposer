using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Renders an adaptive infinite grid using D3D11 + HLSL pixel shader.
/// Uses Vortice.Windows for clean DirectX interop.
/// </summary>
internal sealed class GridRenderer : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;
    private const int VsyncInterval = 1;
    private const int CbAlignmentMask = 15; // 16-byte alignment

    private D3DRenderSurface? _surface;
    private ID3D11Device? _device => _surface?.Device;
    private ID3D11DeviceContext? _context => _surface?.Context;
    private IDXGISwapChain? _swapChain => _surface?.SwapChain;
    private ID3D11RenderTargetView? _rtv => _surface?.Target;
    private readonly bool _preferFlip;
    private readonly RenderFramePump _pump;
    internal bool UsesFlipModel => _surface?.UsesFlipModel == true;
    internal string RenderingBackend => _surface is not { } surface ? "Unavailable" :
        surface.UsesFlipModel ? "FlipDiscard" : "CompatibilityDiscard";

    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Buffer? _monitorBuffer;
    private ID3D11ShaderResourceView? _monitorSrv;
    private int _width, _height;

    private const int MonitorBufferCapacity = 16;
    private const int MonitorStructBytes = 16;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GridConstants
    {
        public float CamX, CamY;
        public float Zoom;
        public float ScreenW, ScreenH;
        public float Time;
        public float DpiScale;
        public float PanAccumX, PanAccumY;
        public float PassOffX, PassOffY;
        public int MonitorCount;
        public int IsPrimary;
        public int LowQuality, _pad1;
    }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private static readonly object ShaderLock = new();
    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;

    public static bool CompileShaders()
    {
        lock (ShaderLock)
        {
            if (_vsBytecode != null && _psBytecode != null) return true;
            Compiler.Compile(ShaderSource, "VSMain", "", "vs_5_0", out var vsBlob, out var vsErr);
            using (vsBlob)
            using (vsErr)
            {
                if (vsBlob == null) return false;
                Compiler.Compile(ShaderSource, "PSMain", "", "ps_5_0", out var psBlob, out var psErr);
                using (psBlob)
                using (psErr)
                {
                    if (psBlob == null) return false;
                    var vertex = vsBlob.AsSpan().ToArray();
                    var pixel = psBlob.AsSpan().ToArray();
                    _vsBytecode = vertex;
                    _psBytecode = pixel;
                    return true;
                }
            }
        }
    }

    private const string ShaderSource = @"
cbuffer GridCB : register(b0)
{
    float2 camPos;
    float zoom;
    float screenW;
    float screenH;
    float time;
    float dpiScale;
    float panAccumX;
    float panAccumY;
    float passOffX;
    float passOffY;
    int monitorCount;
    int isPrimary;
    int lowQuality;
    int _pad1;
};

struct MonitorRect
{
    float2 offset;
    float2 size;
};
StructuredBuffer<MonitorRect> monitors : register(t0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

// Anti-aliased grid line
float gridLine(float coord, float spacing, float lineWidth)
{
    float d = abs(frac(coord / spacing + 0.5) - 0.5) * spacing;
    float aa = fwidth(coord) * 1.5;
    return 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d);
}

// Dashed pattern: returns 0 or 1
float dashPattern(float coord, float dashLen, float offset)
{
    return step(0.5, frac((coord + offset) / dashLen));
}

// X mark at grid intersections: two diagonal lines crossing
float xMark(float2 localPos, float size, float lineWidth)
{
    float aa = fwidth(localPos.x) * 1.5;
    float d1 = abs(localPos.x - localPos.y);
    float d2 = abs(localPos.x + localPos.y);
    float line1 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d1);
    float line2 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d2);
    float mask = step(abs(localPos.x), size) * step(abs(localPos.y), size);
    return saturate(line1 + line2) * mask;
}

// Tiled X marks at regular spacing
float xGrid(float2 wp, float spacing, float xSize, float lineWidth)
{
    float2 cell = frac(wp / spacing + 0.5) - 0.5;
    float2 localPos = cell * spacing;
    return xMark(localPos, xSize, lineWidth);
}

// Grid level of a line: coarsest spacing that aligns to this position.
// Uses trailing zeros of the grid index to find which zoom level the line belongs to.
float lineLevel(float coord, float spacing)
{
    uint n = (uint)abs(round(coord / spacing));
    if (n == 0) return spacing * 32.0;
    uint tz = min(firstbitlow(n), 5u);
    return spacing * pow(2.0, (float)tz);
}

// Dashed grid — lineLevel affects brightness, not spacing
float dashedGrid(float2 wp, float spacing, float lw, float z, int spMul)
{
    float logZ = log2(z * 100.0 / 80.0);
    float blend = logZ - floor(logZ);
    float ps = spacing * 0.03125 * pow(2.0, (float)spMul);
    float lineX = gridLine(wp.x, spacing, lw);
    float lineY = gridLine(wp.y, spacing, lw);
    float dashX = dashPattern(wp.y, ps, ps * 0.25);
    float dashY = dashPattern(wp.x, ps, ps * 0.25);
    float lvlX = lineLevel(wp.x, spacing);
    float lvlY = lineLevel(wp.y, spacing);
    float tzX = log2(lvlX / spacing);
    float tzY = log2(lvlY / spacing);
    float fadeX = smoothstep(0.0, 0.4, blend + tzX * 0.2);
    float fadeY = smoothstep(0.0, 0.4, blend + tzY * 0.2);
    float brightX = 1.0 + tzX * 0.3;
    float brightY = 1.0 + tzY * 0.3;
    return lineX * dashX * fadeX * brightX + lineY * dashY * fadeY * brightY;
}

// === Nebula effect (from shadertoy.com/view/sdlyz8) ===
float nebRandom(float2 st)
{
    return frac(sin(dot(st, float2(12.9898, 78.233))) * 43758.5453123);
}

float nebNoise(float2 st)
{
    float2 i = floor(st);
    float2 f = frac(st);
    float a = nebRandom(i);
    float b = nebRandom(i + float2(1.0, 0.0));
    float c = nebRandom(i + float2(0.0, 1.0));
    float d = nebRandom(i + float2(1.0, 1.0));
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

float nebFbm(float2 st)
{
    float v = 0.0;
    float a = 0.5;
    float2 shift = float2(100.0, 100.0);
    float cs = cos(0.5);
    float sn = sin(0.5);
    float2x2 rot = float2x2(cs, sn, -sn, cs);
    for (int i = 0; i < 5; i++)
    {
        v += a * nebNoise(st);
        st = mul(rot, st) * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

float3 nebula(float2 uv, float t)
{
    float2 st = uv * 3.0;
    float2 q;
    q.x = nebFbm(st);
    q.y = nebFbm(st + float2(1.0, 0.0));
    float2 r;
    r.x = nebFbm(st + q + float2(1.7, 9.2) + 0.15 * t);
    r.y = nebFbm(st + q + float2(8.3, 2.8) + 0.126 * t);
    float f = nebFbm(st + r);
    float3 color = lerp(float3(0.05, 0.0, 0.1), float3(0.1, 0.1, 0.08), saturate(f * f * 4.0));
    color = lerp(color, float3(0.0, 0.0, 0.03), saturate(length(q)));
    color = lerp(color, float3(0.1, 0.15, 0.15), saturate(length(r.x)));
    return (f * f * f + 0.6 * f * f + 0.5 * f) * color;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 screenPos = input.uv * float2(screenW, screenH);

    float2 worldPos = (screenPos + float2(passOffX, passOffY)) / zoom + camPos;

    // A uniform early branch keeps expensive noise and decorative math out of
    // low quality frames. Preserve the world-space grid and origin axes.
    if (lowQuality != 0)
    {
        float level = floor(log2(zoom * 100.0 / 80.0));
        float spacing = 100.0 * exp2(-level);
        float lines = saturate(gridLine(worldPos.x, spacing, 0.4 / zoom)
                             + gridLine(worldPos.y, spacing, 0.4 / zoom));
        float axes = saturate(gridLine(worldPos.x, 1e6, 0.6 / zoom)
                            + gridLine(worldPos.y, 1e6, 0.6 / zoom));
        return float4(float3(0.04, 0.045, 0.05) + lines * float3(0.10, 0.13, 0.15)
                      + axes * float3(0.12, 0.30, 0.36), 1.0);
    }

    // === Adaptive grid spacing (doubles per zoom level) ===
    float logZoom = log2(zoom * 100.0 / 80.0);
    float zoomLevel = floor(logZoom);
    float levelBlend = logZoom - zoomLevel;

    float gridBase  = 100.0 * pow(2.0, -zoomLevel);
    float gridMajor = gridBase * 5.0;
    float gridSub   = gridMajor * 0.5;

    float lineWidth = 0.4 / zoom;

    // === Line detection per grid tier (highest priority wins) ===
    float minPx = dpiScale / zoom;
    float originWidth = max(0.15 / zoom, minPx * 0.5);

    float origin = saturate(
        gridLine(worldPos.x, 1e6, originWidth) +
        gridLine(worldPos.y, 1e6, originWidth));

    float major = saturate(
        gridLine(worldPos.x, gridMajor, lineWidth) +
        gridLine(worldPos.y, gridMajor, lineWidth));

    float sub = saturate(
        gridLine(worldPos.x, gridSub, lineWidth * 0.8) +
        gridLine(worldPos.y, gridSub, lineWidth * 0.8));

    float brightness = smoothstep(0.005, 0.3, zoom);

    // === Nebula background (screen-fixed, fades in at max zoom-out) ===
    float nebulaBlend = smoothstep(0.7, 0.05, zoom);
    float2 nebulaUV = input.uv * float2(screenW / screenH, 1.0) - float2(panAccumX, panAccumY) * 0.000002;
    float3 color = float3(0.04, 0.045, 0.05);
    if (nebulaBlend > 0.0) color = lerp(color, nebula(nebulaUV, time), nebulaBlend);

    // === Grid lines (exclusive priority chain — no overlaps) ===
    float3 gridGlow = float3(0, 0, 0);
    if (origin > 0.01)
    {
        float g = 0.5 + 0.5 * smoothstep(0.01, 0.5, zoom);
        gridGlow = float3(0.4, 0.85, 1.0) * origin * 0.8 * g;
    }
    else if (major > 0.01)
    {
        float p = dashedGrid(worldPos, gridMajor, lineWidth * 0.8, zoom, 0);
        gridGlow = float3(0.75, 0.85, 0.9) * saturate(p) * 0.70 * brightness;
    }
    else if (sub > 0.01)
    {
        float p = dashedGrid(worldPos, gridSub, lineWidth * 0.6, zoom, 1);
        gridGlow = float3(0.65, 0.75, 0.8) * saturate(p) * 0.40 * brightness * levelBlend;
    }
    color += gridGlow;

    {
        // Decorative marks with pan-parallax
        float2 pan = float2(panAccumX, panAccumY);
        float spacing = 0.5;
        float markSize = 0.012;
        float msqrSize = 0.008;
        float dotsSize = 0.0015;
        float markWidth = 0.001;
        float t = saturate((zoom - 0.4) / 0.4);
        float fade = t * t;
        float2 farPos = screenPos * 0.0015;
        float2 markUV = farPos - pan * 0.0005;
        float2 msqrUV = farPos - pan * 0.00045;
        float2 dotsUV = farPos - pan * 0.0004;
        float marks = xGrid(markUV,                 spacing,       markSize, markWidth);
        float msqr  = xGrid(msqrUV + spacing * 0.5, spacing,       msqrSize, msqrSize);
        float dots  = xGrid(dotsUV + spacing,       spacing * 0.5, dotsSize, dotsSize);

        float3 glow = float3(0.0, 0.9, 1.0) * saturate(marks) * 0.8
                    + float3(0.0, 0.7, 0.9) * saturate(msqr) * 0.2
                    + float3(0.0, 0.9, 1.0) * saturate(dots) * 0.4;
        color += glow * fade;
    }

    if (isPrimary != 0)
    {
        for (int mi = 0; mi < monitorCount; mi++)
        {
            float2 mOff  = monitors[mi].offset;
            float2 mSize = monitors[mi].size;

            float2 mOrigin = float2(screenW, screenH) * 0.5
                            - mSize * zoom * 0.5
                            + (mOff - float2(passOffX, passOffY)) * zoom;
            float vw = mSize.x * zoom;
            float vh = mSize.y * zoom;
            float arm = min(vw, vh) * 0.025;
            float lw = 3.0;

            float2 p = screenPos - mOrigin;
            float nearX = min(p.x, vw - p.x);
            float nearY = min(p.y, vh - p.y);
            bool inside = p.x >= 0 && p.x <= vw && p.y >= 0 && p.y <= vh;
            float onEdgeX = step(nearX, lw) * step(nearY, arm);
            float onEdgeY = step(nearY, lw) * step(nearX, arm);
            float corners = inside ? saturate(onEdgeX + onEdgeY) : 0;

            color += float3(0.0, 0.7, 1.0) * saturate(corners) * 0.4;
        }
    }


    return float4(color, 1.0);
}
";

    public bool Initialize(IntPtr hwnd, int width, int height)
    {
        _width = width;
        _height = height;

        _surface = D3DRenderSurface.Create(hwnd, width, height, _preferFlip);
        if (_surface == null) return false;
        try
        {
            if (!CreateShaders()) { ReleaseResources(); return false; }
            CreateConstantBuffer();
            CreateMonitorBuffer();
            return true;
        }
        catch { ReleaseResources(); return false; }
    }

    private bool CreateShaders()
    {
        if (_vsBytecode == null || _psBytecode == null) return false;
        _vertexShader = _device!.CreateVertexShader(_vsBytecode);
        _pixelShader = _device!.CreatePixelShader(_psBytecode);
        return true;
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<GridConstants>() + CbAlignmentMask) & ~CbAlignmentMask;
        _constantBuffer = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void CreateMonitorBuffer()
    {
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(MonitorBufferCapacity * MonitorStructBytes),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)MonitorStructBytes
        };
        _monitorBuffer = _device!.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)MonitorBufferCapacity }
        };
        _monitorSrv = _device.CreateShaderResourceView(_monitorBuffer, srvDesc);
    }

    private volatile bool _drawGrid = true;
    public bool DrawGrid
    {
        get => _drawGrid;
        set { if (_drawGrid == value) return; _drawGrid = value; RequestFrame(); }
    }
    private volatile bool _frameTicksRequired;
    private volatile bool _lowQuality;
    public bool LowQuality
    {
        get => _lowQuality;
        set { if (_lowQuality == value) return; _lowQuality = value; RequestFrame(); }
    }
    public bool FrameTicksRequired
    {
        get => _frameTicksRequired;
        set { _frameTicksRequired = value; RequestFrame(); }
    }

    /// <summary>
    /// Fired on the render thread after each Present. Use for vsync-paced ticks.
    /// </summary>
    public Action? OnFrameTick;

    private float _dpiScale = 1.0f;
    public GridRenderer(bool preferFlip = false)
    {
        _preferFlip = preferFlip;
        _pump = new RenderFramePump("GridRenderer", RenderFrame,
            () => GridFrameSchedule.NeedsContinuousFrames(DrawGrid, _renderZoom, _frameTicksRequired, LowQuality), ReleaseResources);
    }

    private volatile float _renderCamX, _renderCamY, _renderZoom;
    private float _panAccumX, _panAccumY;

    private volatile float _passOffX, _passOffY;
    private volatile int _isPrimary;
    private readonly float[] _monitors = new float[MonitorBufferCapacity * 4];
    private readonly float[] _uploadMonitors = new float[MonitorBufferCapacity * 4];
    private volatile int _monitorCount;
    private readonly object _snapshotLock = new();

    public void RequestFrame() => _pump.Invalidate();
    public void SetDpiScale(float scale) { lock (_snapshotLock) _dpiScale = scale; RequestFrame(); }
    public void ResetClock() { lock (_snapshotLock) _clock.Restart(); RequestFrame(); }

    public void SetScreenLayout(int passOffX, int passOffY, bool isPrimary, System.Drawing.Rectangle[] monitors)
    {
        int count = Math.Min(monitors.Length, MonitorBufferCapacity);
        lock (_snapshotLock)
        {
            _passOffX = passOffX;
            _passOffY = passOffY;
            _isPrimary = isPrimary ? 1 : 0;
            for (int i = 0; i < count; i++)
            {
                _monitors[i * 4 + 0] = monitors[i].X;
                _monitors[i * 4 + 1] = monitors[i].Y;
                _monitors[i * 4 + 2] = monitors[i].Width;
                _monitors[i * 4 + 3] = monitors[i].Height;
            }
            _monitorCount = count;
        }
        RequestFrame();
    }

    public void AccumulatePan(double dx, double dy)
    {
        lock (_snapshotLock)
        {
            _panAccumX += (float)dx;
            _panAccumY += (float)dy;
        }
        RequestFrame();
    }

    /// <summary>Render a single frame synchronously (legacy API).</summary>
    public void Render(double camX, double camY, double zoom)
    {
        lock (_snapshotLock)
        {
            _renderCamX = (float)camX;
            _renderCamY = (float)camY;
            _renderZoom = (float)zoom;
        }
        _pump.Synchronize(() => RenderFrame());
    }

    /// <summary>Start the render loop (wakes the suspended thread).</summary>
    public void Start(double camX, double camY, double zoom)
    {
        lock (_snapshotLock)
        {
            _renderCamX = (float)camX;
            _renderCamY = (float)camY;
            _renderZoom = (float)zoom;
        }
        lock (_snapshotLock) _clock.Restart();
        _pump.Start();
    }

    /// <summary>Update camera for next frame (call from any thread).</summary>
    public void UpdateCamera(double camX, double camY, double zoom, double panDx = 0, double panDy = 0)
    {
        lock (_snapshotLock)
        {
            if (_renderCamX == (float)camX && _renderCamY == (float)camY && _renderZoom == (float)zoom && panDx == 0 && panDy == 0) return;
            _renderCamX = (float)camX;
            _renderCamY = (float)camY;
            _renderZoom = (float)zoom;
            _panAccumX += (float)panDx;
            _panAccumY += (float)panDy;
        }
        RequestFrame();
    }

    /// <summary>Stop rendering and go back to sleep.</summary>
    public void Stop() => _pump.Stop();

    public void StartThread() => _pump.StartThread();

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _pump.Synchronize(() =>
        {
            if (_surface == null || (_width == width && _height == height)) return;
            _surface.Resize(width, height);
            _width = width; _height = height;
        });
        RequestFrame();
    }

    // The UI only shares managed state. Map/Unmap/Present must never run under
    // this lock: a busy driver must not hold up publishing the next camera.
    internal GridConstants CaptureSnapshot(Span<float> monitors)
    {
        lock (_snapshotLock)
        {
            _monitors.AsSpan(0, _monitorCount * 4).CopyTo(monitors);
            return new GridConstants
            {
                CamX = _renderCamX, CamY = _renderCamY, Zoom = _renderZoom,
                ScreenW = _width, ScreenH = _height, Time = (float)_clock.Elapsed.TotalSeconds,
                DpiScale = _dpiScale, PanAccumX = _panAccumX, PanAccumY = _panAccumY,
                PassOffX = _passOffX, PassOffY = _passOffY, MonitorCount = _monitorCount,
                IsPrimary = _isPrimary, LowQuality = LowQuality ? 1 : 0
            };
        }
    }

    private bool RenderFrame()
    {
        if (_context == null || _swapChain == null) return false;
        long frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        var constants = CaptureSnapshot(_uploadMonitors);
        var cbMapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, cbMapped.DataPointer, false);
        _context.Unmap(_constantBuffer!);
        if (constants.MonitorCount > 0)
        {
            var mapped = _context.Map(_monitorBuffer!, MapMode.WriteDiscard);
            Marshal.Copy(_uploadMonitors, 0, mapped.DataPointer, constants.MonitorCount * 4);
            _context.Unmap(_monitorBuffer!);
        }

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);

        if (DrawGrid)
        {
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetConstantBuffer(0, _constantBuffer);
            _context.PSSetShaderResource(0, _monitorSrv!);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(FullscreenTriangleVertexCount, 0);
        }
        else
        {
            _context.ClearRenderTargetView(_rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        }

        PerformanceDiagnostics.Shared.Complete("GridUploadAndDraw", IntPtr.Zero, frameStarted, true);
        long presentStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var present = _swapChain.Present(VsyncInterval, PresentFlags.None);
        PerformanceDiagnostics.Shared.Complete("GridPresentCall", IntPtr.Zero, presentStarted, present.Success, present.Code);
        PerformanceDiagnostics.Shared.Complete("GridFrameIncludingPresent", IntPtr.Zero, frameStarted, present.Success, present.Code);
        present.CheckError();
        OnFrameTick?.Invoke();
        return present.Code == 0;
    }

    public void Dispose() => _pump.Dispose();

    private void ReleaseResources()
    {
        _constantBuffer?.Dispose(); _constantBuffer = null;
        _monitorSrv?.Dispose(); _monitorSrv = null;
        _monitorBuffer?.Dispose(); _monitorBuffer = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _surface?.Dispose();
        _surface = null;
    }
}
