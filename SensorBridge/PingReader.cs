// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Net.NetworkInformation;

namespace SensorBridge;

/// <summary>
/// Round-trip time to a public DNS resolver, as a stand-in for "is my connection lagging".
///
/// Pings on its own timer and caches the result: the telemetry loop must never block on the
/// network, and a ping can take up to its timeout to fail.
/// </summary>
public sealed class PingReader : IDisposable
{
    // Cloudflare's resolver: anycast, so it answers from a nearby PoP almost everywhere.
    private const string Host = "1.1.1.1";
    private const int TimeoutMs = 1000;
    private const int IntervalMs = 1000;

    private readonly System.Timers.Timer _timer = new(IntervalMs);
    private readonly Ping _ping = new();
    private volatile int _latestMs = -1;

    public void Start()
    {
        _timer.Elapsed += (_, _) => Probe();
        _timer.AutoReset = true;
        _timer.Start();
        Probe();
    }

    private void Probe()
    {
        try
        {
            var reply = _ping.Send(Host, TimeoutMs);
            _latestMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch
        {
            _latestMs = -1; // no route, DNS down, adapter asleep — report "unknown", not a number
        }
    }

    /// <summary>Latest round-trip in ms, or null when the last probe failed.</summary>
    public float? Read() => _latestMs >= 0 ? _latestMs : null;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _ping.Dispose();
    }
}
