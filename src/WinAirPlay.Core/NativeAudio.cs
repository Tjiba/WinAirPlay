using System.Runtime.InteropServices;

namespace WinAirPlay.Core;

public sealed record AudioEndpoint(string Id, string Name)
{
    public override string ToString() => Name;
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioStats
{
    public ulong Captured;
    public ulong Delivered;
    public ulong Discontinuities;
    public ulong Overflows;
    public uint Queued;
    public int Error;
}

public sealed class NativeAudio : IDisposable
{
    private const string Library = "winairplay_audio";
    private IntPtr handle;
    public NativeAudio(string? deviceId)
    {
        if (wa_version() != 2) throw new InvalidOperationException("Incompatible audio engine version");
        handle = wa_create(deviceId);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var result = wa_start(handle);
        if (result == 0) return;
        Dispose();
        throw new IOException($"WASAPI: 0x{result:X8}");
    }
    public int Read(byte[] output, uint frames)
    {
        if (frames > output.Length / 4) throw new ArgumentException("PCM buffer too small", nameof(output));
        return wa_read(handle, output, frames, 100);
    }
    public AudioStats Stats { get { wa_stats(handle, out var stats); return stats; } }
    public void Prime(uint frames, CancellationToken cancellation)
    {
        var deadline = wa_clock() + 1;
        while (wa_clock() < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = wa_prime(handle, frames, 20);
            if (result > 0) return;
            if (result < 0) throw new IOException($"Capture initialization failed (0x{Stats.Error:X8})");
        }
        throw new TimeoutException("Capture buffer did not fill within one second");
    }
    public void SetDrift(double value) => wa_drift(handle, Math.Clamp(value, -0.002, 0.002));
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        wa_destroy(handle);
        handle = IntPtr.Zero;
    }
    public static IReadOnlyList<AudioEndpoint> Devices()
    {
        var result = new List<AudioEndpoint> { new("", "Default Windows output") };
        for (uint i = 0; i < 256; i++)
        {
            var id = new char[1024];
            var name = new char[1024];
            var status = wa_device(i, id, (uint)id.Length, name, (uint)name.Length);
            if (status == 0) break;
            if (status < 0) throw new IOException("Unable to enumerate WASAPI outputs");
            result.Add(new(new string(id).TrimEnd('\0'), new string(name).TrimEnd('\0')));
        }
        return result;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern uint wa_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern IntPtr wa_create(string? deviceId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int wa_start(IntPtr engine);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int wa_prime(IntPtr engine, uint frames, uint timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void wa_destroy(IntPtr engine);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int wa_read(IntPtr engine, [Out] byte[] output, uint frames, uint timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void wa_stats(IntPtr engine, out AudioStats stats);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void wa_drift(IntPtr engine, double drift);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int wa_device(uint index, [Out] char[] id, uint idSize, [Out] char[] name, uint nameSize);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern double wa_clock();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr wa_timer_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void wa_timer_wait(IntPtr timer, double deadline);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void wa_timer_destroy(IntPtr timer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr wa_priority_begin();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void wa_priority_end(IntPtr priority);
}
