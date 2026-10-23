using System.ComponentModel;
using System.Runtime.InteropServices;
using SmartMacro.Core.Ocr;
using SmartMacro.Services.Input;

namespace SmartMacro.Services.Vision;

/// <summary>
/// ONE reusable GDI capture buffer for the whole process: a memory DC with a 32-bit top-down DIB section selected
/// into it. Every BitBlt + pixel copy happens inside <see cref="_gate"/>, so any thread may capture (the playback
/// engine's async continuations hop between thread-pool threads) and the unmanaged handles still exist exactly once.
///
/// Why not [ThreadStatic]: a per-thread cache gave EVERY thread-pool thread that ever captured its own HDC + HBITMAP,
/// and nothing freed them when the pool retired / parked those threads (GDI handles are a per-process quota of 10,000).
///
/// - The buffer is re-created only when the requested (width, height) changes; same size = zero GDI allocations.
/// - <see cref="TryCapture"/> copies the pixels into a NEW managed array before the lock is released (~0.1–0.3 ms for
///   a Hot-Spot sized area), so callers never see a buffer another thread is writing into.
/// - <see cref="Dispose"/> (App.OnExit via <see cref="Ocr.ScreenCapture.ReleaseAllResources"/>) frees the handles; a
///   capture after that simply creates them again.
/// </summary>
internal sealed class ReusableGdiSurface : IDisposable
{
    private readonly object _gate = new();
    private readonly string _name;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits; // pixel memory of the DIB section (owned by GDI; valid while _bitmap lives)
    private int _width;
    private int _height;

    // Diagnostics: GDI surfaces (reused + one-shot) that exist right now, process-wide.
    private static int _liveSurfaces;

    public ReusableGdiSurface(string name)
    {
        _name = name;
    }

    /// <summary>How many GDI capture surfaces exist right now (reused + one-shot), for diagnostics / tests by hand.</summary>
    public static int LiveSurfaceCount => Volatile.Read(ref _liveSurfaces);

    /// <summary>The size the buffer currently has (0 × 0 = no GDI objects allocated).</summary>
    public (int Width, int Height) CurrentSize
    {
        get { lock (_gate) return (_width, _height); }
    }

    /// <summary>
    /// Copies <paramref name="area"/> (physical virtual-screen pixels) from <paramref name="screenDc"/> into a new BGRA
    /// array. <paramref name="wait"/> false = don't wait if another thread is capturing right now (returns false; the
    /// caller then uses a one-shot surface) – the mouse-hook thread must never block.
    /// </summary>
    public bool TryCapture(IntPtr screenDc, ScreenRect area, bool wait, out byte[] pixels)
    {
        pixels = Array.Empty<byte>();
        var lockTaken = false;
        try
        {
            if (wait) Monitor.Enter(_gate, ref lockTaken);
            else Monitor.TryEnter(_gate, ref lockTaken);
            if (!lockTaken) return false;

            try
            {
                EnsureSize(screenDc, area.Width, area.Height);
                pixels = BlitAndCopy(screenDc, _memoryDc, _bits, area);
                return true;
            }
            catch
            {
                // Something went wrong with these handles: free them, the next capture makes fresh ones.
                FreeLocked();
                throw;
            }
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// A capture with its own temporary DC + DIB, freed before returning (used for huge areas and when the shared
    /// buffer is busy and the caller can't wait). No handle survives the call.
    /// </summary>
    public static byte[] CaptureOnce(IntPtr screenDc, ScreenRect area)
    {
        var (dc, bitmap, previous, bits) = Create(screenDc, area.Width, area.Height);
        try
        {
            return BlitAndCopy(screenDc, dc, bits, area);
        }
        finally
        {
            Free(dc, bitmap, previous);
        }
    }

    /// <summary>Frees the GDI handles (waits for a capture in progress to finish first).</summary>
    public void Dispose()
    {
        lock (_gate) FreeLocked();
    }

    public override string ToString() => $"{_name} GDI surface {_width}×{_height}";

    private void EnsureSize(IntPtr screenDc, int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && _width == width && _height == height) return;
        FreeLocked();
        (_memoryDc, _bitmap, _previousBitmap, _bits) = Create(screenDc, width, height);
        _width = width;
        _height = height;
    }

    private void FreeLocked()
    {
        if (_memoryDc == IntPtr.Zero && _bitmap == IntPtr.Zero) return;
        Free(_memoryDc, _bitmap, _previousBitmap);
        _memoryDc = _bitmap = _previousBitmap = _bits = IntPtr.Zero;
        _width = _height = 0;
    }

    private static (IntPtr Dc, IntPtr Bitmap, IntPtr Previous, IntPtr Bits) Create(IntPtr screenDc, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("The capture area is empty.");
        var dc = NativeMethods.CreateCompatibleDC(screenDc);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare a screen capture.");
        Interlocked.Increment(ref _liveSurfaces);

        var info = TopDownBgra(width, height);
        var bitmap = NativeMethods.CreateDIBSection(screenDc, ref info, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(dc);
            Interlocked.Decrement(ref _liveSurfaces);
            throw new Win32Exception(error, "Could not prepare a screen capture.");
        }

        var previous = NativeMethods.SelectObject(dc, bitmap);
        return (dc, bitmap, previous, bits);
    }

    private static void Free(IntPtr dc, IntPtr bitmap, IntPtr previous)
    {
        if (dc != IntPtr.Zero && previous != IntPtr.Zero) NativeMethods.SelectObject(dc, previous);
        if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
        if (dc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(dc);
            Interlocked.Decrement(ref _liveSurfaces);
        }
    }

    private static byte[] BlitAndCopy(IntPtr screenDc, IntPtr memoryDc, IntPtr bits, ScreenRect area)
    {
        var copied = NativeMethods.BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y,
            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        var bltError = Marshal.GetLastWin32Error();
        if (!copied)
            throw new Win32Exception(bltError, "Windows didn't allow a screen capture (the screen may be locked).");

        NativeMethods.GdiFlush(); // make sure GDI finished writing the DIB before reading its memory
        var pixels = new byte[checked(area.Width * area.Height * 4)];
        Marshal.Copy(bits, pixels, 0, pixels.Length); // 32 bpp rows have no padding: one straight copy
        return pixels;
    }

    private static NativeMethods.BITMAPINFO TopDownBgra(int width, int height) => new()
    {
        bmiHeader = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negative = rows top to bottom
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        },
    };
}
