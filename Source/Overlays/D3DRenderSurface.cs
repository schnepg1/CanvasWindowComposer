using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CanvasDesktop;

/// <summary>
/// Owns the native presentation boundary, independently of grid/minimap policy.
/// Flip presentation is experimental: these hosts also compose DWM thumbnails or
/// use layered-window opacity. API acceptance alone does not prove visual parity.
/// </summary>
internal sealed class D3DRenderSurface : IDisposable
{
    public ID3D11Device Device { get; private set; } = null!;
    public ID3D11DeviceContext Context { get; private set; } = null!;
    public IDXGISwapChain SwapChain { get; private set; } = null!;
    public ID3D11RenderTargetView Target { get; private set; } = null!;
    public bool UsesFlipModel { get; private set; }

    public static D3DRenderSurface? Create(IntPtr hwnd, int width, int height, bool preferFlip)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return null;
        // A failed attempt is fully released before creating the compatibility chain.
        if (preferFlip && TryCreate(hwnd, width, height, true) is { } flip) return flip;
        return TryCreate(hwnd, width, height, false);
    }

    private static D3DRenderSurface? TryCreate(IntPtr hwnd, int width, int height, bool flip)
    {
        var surface = new D3DRenderSurface();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string operation = flip ? "CreateFlipRenderSurface" : "CreateCompatibilityRenderSurface";
        try
        {
            var desc = new SwapChainDescription
            {
                BufferCount = flip ? 2u : 1u,
                BufferDescription = new ModeDescription((uint)width, (uint)height, Format.R8G8B8A8_UNorm),
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = hwnd,
                SampleDescription = new SampleDescription(1, 0),
                Windowed = true,
                SwapEffect = flip ? SwapEffect.FlipDiscard : SwapEffect.Discard
            };
            var result = D3D11.D3D11CreateDeviceAndSwapChain(null!, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, null!, desc, out var chain, out var device,
                out _, out var context);
            surface.Device = device!;
            surface.Context = context!;
            surface.SwapChain = chain!;
            if (result.Failure) result.CheckError();
            surface.UsesFlipModel = flip;
            using var factory = surface.SwapChain.GetParent<IDXGIFactory>();
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter).CheckError();
            surface.CreateTarget();
            PerformanceDiagnostics.Shared.Complete(operation, hwnd, started, true);
            return surface;
        }
        catch (Exception ex)
        {
            PerformanceDiagnostics.Shared.Complete(operation, hwnd, started, false, ex.HResult);
            surface.Dispose();
            return null;
        }
    }

    private void CreateTarget()
    {
        using var buffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
        Target = Device.CreateRenderTargetView(buffer);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        // The immediate context retains COM references even after the managed view
        // is released. Unbind before ResizeBuffers, for both presentation models.
        Context.ClearState();
        Target.Dispose();
        Target = null!;
        Context.Flush();
        SwapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    public void Dispose()
    {
        Context?.ClearState();
        Target?.Dispose(); Target = null!;
        SwapChain?.Dispose(); SwapChain = null!;
        Context?.Dispose(); Context = null!;
        Device?.Dispose(); Device = null!;
    }
}
