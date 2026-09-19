using System.Diagnostics;

namespace WinAirPlay.Core;

public sealed class NetworkClock
{
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset utc = DateTimeOffset.UtcNow;
    public double Elapsed => (Stopwatch.GetTimestamp() - origin) / (double)Stopwatch.Frequency;
    public ulong Now => At(Elapsed);
    public ulong At(double seconds)
    {
        var ntp = utc.ToUnixTimeMilliseconds() / 1000.0 + 2208988800.0 + seconds;
        var whole = (ulong)ntp;
        return (whole << 32) | (uint)((ntp - whole) * 4294967296.0);
    }
}
