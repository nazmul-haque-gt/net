using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetSpeedTray;

// Per-app byte counting via Windows kernel ETW (TCP/UDP send/recv events). Needs admin.
sealed class AppTracker : IDisposable
{
    readonly History hist;
    readonly Dictionary<int, string> names = new();
    TraceEventSession session;
    Thread thread;

    public AppTracker(History h) { hist = h; }

    public bool Start()
    {
        try
        {
            session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var k = session.Source.Kernel;
            k.TcpIpRecv += d => Add(d.ProcessID, d.size, 0, d.daddr);
            k.TcpIpSend += d => Add(d.ProcessID, 0, d.size, d.daddr);
            k.UdpIpRecv += d => Add(d.ProcessID, d.size, 0, d.daddr);
            k.UdpIpSend += d => Add(d.ProcessID, 0, d.size, d.daddr);
            thread = new Thread(() => { try { session.Source.Process(); } catch { } })
            { IsBackground = true, Name = "EtwNet" };
            thread.Start();
            return true;
        }
        catch { Dispose(); return false; }
    }

    void Add(int pid, long down, long up, IPAddress remote)
    {
        if (remote != null && IPAddress.IsLoopback(remote)) return;
        hist.Add(Name(pid), down, up);
    }

    string Name(int pid)
    {
        if (names.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById(pid).ProcessName; }
        catch { n = pid == 4 ? "System" : $"PID {pid} (exited)"; }
        if (names.Count > 5000) names.Clear();
        names[pid] = n;
        return n;
    }

    public void Dispose()
    {
        try { session?.Dispose(); } catch { }
        session = null;
    }
}
