using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// D3D11 minimap renderer. UI thread pushes a projected snapshot
/// (<see cref="UpdateSnapshot"/>) and the render thread presents changes at vsync.
/// No paint work on the UI thread; mirrors <see cref="GridRenderer"/>'s
/// thread + swap-chain structure.
/// </summary>
internal sealed class MinimapRenderer : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;
    private const int VsyncInterval = 1;
    private const int CbAlignmentMask = 15;

    // Up to 256 windows shown. Anything past that is dropped silently — a
    // minimap that dense isn't useful anyway.
    private const int WindowBufferCapacity = 32700;
    private const int WindowStructBytes = 16; // two float2 = (min, max)

    private D3DRenderSurface? _surface;
    private ID3D11Device? _device => _surface?.Device;
    private ID3D11DeviceContext? _context => _surface?.Context;
    private IDXGISwapChain? _swapChain => _surface?.SwapChain;
    private ID3D11RenderTargetView? _rtv => _surface?.Target;
    private readonly bool _preferFlip;
    private readonly RenderFramePump _pump;
    internal bool UsesFlipModel => _surface?.UsesFlipModel == true;

    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Buffer? _windowsBuffer;
    private ID3D11ShaderResourceView? _windowsSrv;
    private int _width, _height;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinimapConstants
    {
        public float ScreenW, ScreenH;
        public float MapOriginX, MapOriginY;
        public float MapW, MapH;
        public int WindowCount;
        public int _pad0;
        public float ViewportMinX, ViewportMinY, ViewportMaxX, ViewportMaxY;
    }

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
cbuffer MinimapCB : register(b0)
{
    float screenW;
    float screenH;
    float mapOriginX;
    float mapOriginY;
    float mapW;
    float mapH;
    int windowCount;
    int _pad0;
    float4 viewportRect; // minX, minY, maxX, maxY in map-local pixels
};

struct MapRect
{
    float2 mn;
    float2 mx;
};
StructuredBuffer<MapRect> windows : register(t0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

// Signed distance from p to a rounded rect [mn, mx] with corner radius r.
// Negative inside, positive outside, zero on the edge.
float sdfRoundedRect(float2 p, float2 mn, float2 mx, float r)
{
    float2 halfSize = (mx - mn) * 0.5;
    float2 center = (mx + mn) * 0.5;
    // Clamp radius to the smaller half-extent so small rects don't invert.
    r = min(r, min(halfSize.x, halfSize.y));
    float2 q = abs(p - center) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 screenPos = input.uv * float2(screenW, screenH);
    float2 mapPos = screenPos - float2(mapOriginX, mapOriginY);
    float2 mapSize = float2(mapW, mapH);

    bool insideMap = mapPos.x >= 0 && mapPos.y >= 0 && mapPos.x <= mapSize.x && mapPos.y <= mapSize.y;

    float3 color = float3(0.118, 0.118, 0.118);
    if (insideMap)
    {
        color = float3(0.078, 0.078, 0.078);

        // Windows are iterated topmost-first (matching canvas z-order). First
        // window containing the pixel wins — higher windows visually occlude
        // the ones below.
        const float cornerRadius = 2.5;
        float3 winFill = float3(0.31, 0.63, 1.0) * 0.55;
        float3 winEdge = float3(0.39, 0.71, 1.0);
        for (int i = 0; i < windowCount; i++)
        {
            MapRect w = windows[i];
            float sd = sdfRoundedRect(mapPos, w.mn, w.mx, cornerRadius);
            if (sd >= 0.5) continue; // fully outside this window (with AA margin)
            float f = 1.0 - smoothstep(-0.5, 0.5, sd);
            float e = 1.0 - smoothstep(0.0, 1.5, abs(sd + 0.75));
            color = lerp(color, lerp(winFill, winEdge, e), f);
            break;
        }

        // Viewport outline (sharp corners).
        float2 vmn = viewportRect.xy;
        float2 vmx = viewportRect.zw;
        if (mapPos.x >= vmn.x && mapPos.y >= vmn.y &&
            mapPos.x <= vmx.x && mapPos.y <= vmx.y)
        {
            float2 dMn = mapPos - vmn;
            float2 dMx = vmx - mapPos;
            float d = min(min(dMn.x, dMx.x), min(dMn.y, dMx.y));
            if (d < 1.5) color = float3(1.0, 0.78, 0.20);
        }

        // Inner border.
        float2 dMnB = mapPos;
        float2 dMxB = mapSize - mapPos;
        float dB = min(min(dMnB.x, dMxB.x), min(dMnB.y, dMxB.y));
        if (dB < 1.0) color = float3(0.5, 0.5, 0.5);
    }

    return float4(color, 1.0);
}
";

    public bool Initialize(IntPtr hwnd, int width, int height, int mapOriginX, int mapOriginY, int mapW, int mapH)
    {
        _width = width;
        _height = height;
        _mapOriginX = mapOriginX;
        _mapOriginY = mapOriginY;
        _mapW = mapW;
        _mapH = mapH;

        _surface = D3DRenderSurface.Create(hwnd, width, height, _preferFlip);
        if (_surface == null) return false;
        try
        {
            if (!CreateShaders()) { ReleaseResources(); return false; }
            CreateConstantBuffer();
            CreateWindowsBuffer();
            return true;
        }
        catch { ReleaseResources(); return false; }
    }

    private bool CreateShaders()
    {
        if (_vsBytecode == null || _psBytecode == null) return false;
        _vertexShader = _device!.CreateVertexShader(_vsBytecode);
        _pixelShader = _device.CreatePixelShader(_psBytecode);
        return true;
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<MinimapConstants>() + CbAlignmentMask) & ~CbAlignmentMask;
        _constantBuffer = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void CreateWindowsBuffer()
    {
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(WindowBufferCapacity * WindowStructBytes),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)WindowStructBytes
        };
        _windowsBuffer = _device!.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)WindowBufferCapacity }
        };
        _windowsSrv = _device.CreateShaderResourceView(_windowsBuffer, srvDesc);
    }

    public Action? OnFrameTick;

    public MinimapRenderer(bool preferFlip = false)
    {
        _preferFlip = preferFlip;
        _pump = new RenderFramePump("MinimapRenderer", RenderFrame, () => false, ReleaseResources);
    }

    public void RequestFrame() => _pump.Invalidate();

    // Fixed from Initialize. Set once before any render, so no volatile.
    private int _mapOriginX, _mapOriginY, _mapW, _mapH;

    // Shader-facing state (UI thread writes, render thread reads).
    private volatile float _vpMinX, _vpMinY, _vpMaxX, _vpMaxY;
    private readonly float[] _windowRects = new float[WindowBufferCapacity * 4];
    private volatile int _windowCount;
    private readonly object _windowsLock = new();

    /// <summary>
    /// Project the canvas into map-pixel rects and hand them to the renderer.
    /// Called from the UI thread; the render thread picks up the new data on
    /// the next vsync. <paramref name="windows"/> should be topmost-first so
    /// the shader's first-hit wins matches OS z-order.
    /// </summary>
    public void UpdateSnapshot(
        IReadOnlyList<WorldRect> windows,
        (double minX, double minY, double maxX, double maxY)? extents,
        (double x, double y, double w, double h) viewport,
        double extentsPadding = 0.10,
        int minRectSizePx = 2)
    {
        lock (_windowsLock)
        {
            // Frame = extents ∪ viewport, padded. If there are no windows yet,
            // viewport alone defines the frame.
            double minX = viewport.x;
            double minY = viewport.y;
            double maxX = viewport.x + viewport.w;
            double maxY = viewport.y + viewport.h;
            if (extents is var (eMinX, eMinY, eMaxX, eMaxY))
            {
                minX = Math.Min(minX, eMinX);
                minY = Math.Min(minY, eMinY);
                maxX = Math.Max(maxX, eMaxX);
                maxY = Math.Max(maxY, eMaxY);
            }

            double worldW = maxX - minX;
            double worldH = maxY - minY;
            minX -= worldW * extentsPadding; maxX += worldW * extentsPadding;
            minY -= worldH * extentsPadding; maxY += worldH * extentsPadding;
            worldW = maxX - minX;
            worldH = maxY - minY;

            if (worldW < 1 || worldH < 1)
            {
                _windowCount = 0;
                _vpMinX = _vpMinY = _vpMaxX = _vpMaxY = 0;
                RequestFrame();
                return;
            }

            double scale = Math.Min((_mapW - 2) / worldW, (_mapH - 2) / worldH);
            double offX = (_mapW - worldW * scale) / 2;
            double offY = (_mapH - worldH * scale) / 2;

            _vpMinX = (float)(offX + (viewport.x - minX) * scale);
            _vpMinY = (float)(offY + (viewport.y - minY) * scale);
            _vpMaxX = (float)(offX + (viewport.x + viewport.w - minX) * scale);
            _vpMaxY = (float)(offY + (viewport.y + viewport.h - minY) * scale);

            int count = 0;
            foreach (var w in windows)
            {
                if (count >= WindowBufferCapacity) break;
                if (w.State != WindowState.Normal) continue;

                float mnX = (float)(offX + (w.X - minX) * scale);
                float mnY = (float)(offY + (w.Y - minY) * scale);
                float mxX = (float)(offX + (w.X + w.W - minX) * scale);
                float mxY = (float)(offY + (w.Y + w.H - minY) * scale);
                if (mxX - mnX < minRectSizePx) mxX = mnX + minRectSizePx;
                if (mxY - mnY < minRectSizePx) mxY = mnY + minRectSizePx;

                int o = count * 4;
                _windowRects[o]     = mnX;
                _windowRects[o + 1] = mnY;
                _windowRects[o + 2] = mxX;
                _windowRects[o + 3] = mxY;
                count++;
            }
            _windowCount = count;
        }
        RequestFrame();
    }

    public void Start() => _pump.Start();

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

    private bool RenderFrame()
    {
        if (_context == null || _swapChain == null) return false;
        long frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        lock (_windowsLock)
        {
            var cbMapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
            var constants = new MinimapConstants
            {
                ScreenW = _width,
                ScreenH = _height,
                MapOriginX = _mapOriginX,
                MapOriginY = _mapOriginY,
                MapW = _mapW,
                MapH = _mapH,
                WindowCount = _windowCount,
                ViewportMinX = _vpMinX,
                ViewportMinY = _vpMinY,
                ViewportMaxX = _vpMaxX,
                ViewportMaxY = _vpMaxY
            };
            Marshal.StructureToPtr(constants, cbMapped.DataPointer, false);
            _context.Unmap(_constantBuffer!);

            if (_windowCount > 0)
            {
                var mapped = _context.Map(_windowsBuffer!, MapMode.WriteDiscard);
                Marshal.Copy(_windowRects, 0, mapped.DataPointer, _windowCount * 4);
                _context.Unmap(_windowsBuffer!);
            }
        }

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);

        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _windowsSrv!);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.Draw(FullscreenTriangleVertexCount, 0);

        var present = _swapChain.Present(VsyncInterval, PresentFlags.None);
        PerformanceDiagnostics.Shared.Complete("MinimapFrameIncludingPresent", IntPtr.Zero, frameStarted, present.Success, present.Code);
        present.CheckError();
        OnFrameTick?.Invoke();
        return present.Code == 0;
    }

    public void Dispose() => _pump.Dispose();

    private void ReleaseResources()
    {
        _constantBuffer?.Dispose(); _constantBuffer = null;
        _windowsSrv?.Dispose(); _windowsSrv = null;
        _windowsBuffer?.Dispose(); _windowsBuffer = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _surface?.Dispose();
        _surface = null;
    }
}
