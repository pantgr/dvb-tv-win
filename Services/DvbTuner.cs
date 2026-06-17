using System.Runtime.InteropServices;
using DirectShowLib;
using DirectShowLib.BDA;
using DvbTv.Models;
using Microsoft.Extensions.Logging;

namespace DvbTv.Services;

/// <summary>
/// DirectShow BDA implementation for the RTL2832U DVB-T stick.
/// Graph: Network Provider -> BDA Tuner -> Sample Grabber -> MPEG-2 Demux -> TIF.
/// The sample grabber taps the raw MPEG-TS into an in-memory pull pipe
/// (TsPipeStream ring buffer) which the VLC player drains at its own pace —
/// UDP loopback push was tried and rejected (bursts overflowed the receive
/// buffer → hard packet loss → TS discontinuities on every PID).
/// Signal lock/strength is read from IBDA_SignalStatistics on the tuner topology.
///
/// All COM calls run on the WinForms STA (UI) thread; the lock wait awaits-polls
/// the real SignalLocked flag, not an arbitrary sleep.
/// </summary>
public sealed class DvbTuner : IDvbTuner
{
    // Microsoft generic Network Provider (Win7+) — adapts to the tuning space network type.
    private static readonly Guid CLSID_NetworkProvider = new("B2F3A67C-29DA-4C78-8831-091ED509A475");
    // Specific Microsoft DVB-T Network Provider (older but accepts an ad-hoc tuning space).
    private static readonly Guid CLSID_DVBTNetworkProvider = new("216C62DF-6D7F-4E9A-8571-05F14EDB766A");
    // Microsoft MPEG-2 Demultiplexer.
    private static readonly Guid CLSID_MPEG2Demultiplexer = new("AFB6C280-2C41-11D3-8A60-0000F81E0E4A");
    // CLSID_DVBTNetworkProvider as the tuning space network type (both BSTR + GUID forms).
    private const string DVBT_NetworkTypeString = "{216C62DF-6D7F-4E9A-8571-05F14EDB766A}";
    private static readonly Guid DVBT_NetworkType = new("216C62DF-6D7F-4E9A-8571-05F14EDB766A");

    private readonly ILogger<DvbTuner> _log;

    private IFilterGraph2? _graph;
    private IMediaControl? _control;
    private ICaptureGraphBuilder2? _capture;
    private IBaseFilter? _networkProvider;
    private IBaseFilter? _tuner;
    private IBaseFilter? _receiver;
    private IBaseFilter? _grabber;
    private ISampleGrabber? _grabberIface;
    private IBaseFilter? _demux;
    private IBaseFilter? _tif;
    private ITuner? _tunerIface;
    private IDVBTuningSpace2? _tuningSpace;
    private TsPipeStream? _pipe;
    private readonly EitCollector _eit = new();
    private bool _running;

    public DvbTuner(ILogger<DvbTuner> log) => _log = log;

    /// <summary>Live TS pipe the player reads — the grabber writes the raw TS here.</summary>
    public Stream? TransportStream => _pipe;

    private void EnsureGraph()
    {
        if (_graph != null) return;
        try
        {
            BuildGraph();
        }
        catch
        {
            Cleanup(); // discard the half-built graph so the next attempt starts clean
            throw;
        }
    }

    private void BuildGraph()
    {
        _log.LogInformation("Building BDA graph…");

        _graph = (IFilterGraph2)new FilterGraph();
        _control = (IMediaControl)_graph;
        _capture = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        _log.LogDebug("BDA step: SetFiltergraph");
        _capture.SetFiltergraph(_graph);

        // 1. DVB-T tuning space (+ default locator — REQUIRED, else put_TuningSpace /
        //    put_TuneRequest fail with E_INVALIDARG 0x80070057).
        _log.LogDebug("BDA step: new DVBTuningSpace");
        _tuningSpace = (IDVBTuningSpace2)new DVBTuningSpace();
        _tuningSpace.put_UniqueName("DvbTv DVBT");
        _tuningSpace.put_FriendlyName("DvbTv DVBT");
        _log.LogDebug("BDA step: NetworkType (BSTR + GUID)");
        _tuningSpace.put_NetworkType(DVBT_NetworkTypeString); // BSTR form — what the generic provider reads
        _tuningSpace.put__NetworkType(DVBT_NetworkType);       // GUID form — DVB-specific
        _log.LogDebug("BDA step: put_SystemType");
        _tuningSpace.put_SystemType(DVBSystemType.Terrestrial);

        _log.LogDebug("BDA step: default locator");
        var defaultLocator = (IDVBTLocator)new DVBTLocator();
        defaultLocator.put_CarrierFrequency(-1);
        defaultLocator.put_Bandwidth(8);
        _log.LogDebug("BDA step: put_DefaultLocator");
        _tuningSpace.put_DefaultLocator(defaultLocator);
        Marshal.ReleaseComObject(defaultLocator);

        // 2. Network provider, bound to the tuning space (generic, then specific DVB-T).
        _log.LogDebug("BDA step: connect network provider");
        ConnectNetworkProvider();

        // 3. BDA tuner device, connected NP -> tuner
        _log.LogDebug("BDA step: connect tuner");
        _tuner = AddAndConnect(FilterCategory.BDASourceFiltersCategory, _networkProvider, "tuner")
                 ?? throw new InvalidOperationException("No BDA DVB-T tuner could be connected.");

        // 4. BDA receiver/capture component (often separate; some sticks are all-in-one)
        _log.LogDebug("BDA step: connect receiver");
        _receiver = AddAndConnect(FilterCategory.BDAReceiverComponentsCategory, _tuner, "receiver");

        // 5. Sample Grabber on the raw TS -> in-memory pull pipe (for VLC). Then Demux + TIF so
        //    the graph runs (without a downstream pipeline Run() throws 0x8007048F).
        //    tuner -> grabber -> demux -> TIF.
        var upstream = _receiver ?? _tuner;

        _log.LogDebug("BDA step: add TS Sample Grabber");
        _pipe = new TsPipeStream(20 * 1024 * 1024); // 20 MB headroom for hiccups
        _grabber = (IBaseFilter)new SampleGrabber();
        _grabberIface = (ISampleGrabber)_grabber;
        var mt = new AMMediaType { majorType = MediaType.Stream, subType = MediaSubType.BdaMpeg2Transport };
        _grabberIface.SetMediaType(mt);
        DsUtils.FreeAMMediaType(mt);
        _grabberIface.SetBufferSamples(false);
        _grabberIface.SetOneShot(false);
        _grabberIface.SetCallback(new TsGrabberCallback(_pipe, _eit, _log), 1); // 1 = BufferCB
        DsError.ThrowExceptionForHR(_graph.AddFilter(_grabber, "TS Sample Grabber"));

        _log.LogDebug("BDA step: add+connect MPEG-2 Demultiplexer (via grabber)");
        _demux = AddFilter(CLSID_MPEG2Demultiplexer, "MPEG-2 Demultiplexer");
        DsError.ThrowExceptionForHR(_capture!.RenderStream(null, null, upstream, _grabber, _demux));

        _log.LogDebug("BDA step: add+connect TIF");
        _tif = AddAndConnect(FilterCategory.BDATransportInformationRenderersCategory, _demux, "TIF");

        _log.LogDebug("BDA step: Run");
        DsError.ThrowExceptionForHR(_control!.Run());
        _running = true;
        _log.LogInformation("BDA graph running (TS -> in-memory pipe)");
    }

    private IBaseFilter AddFilter(Guid clsid, string name)
    {
        var type = Type.GetTypeFromCLSID(clsid) ?? throw new InvalidOperationException($"CLSID {clsid} not registered");
        var filter = (IBaseFilter)Activator.CreateInstance(type)!;
        DsError.ThrowExceptionForHR(_graph!.AddFilter(filter, name));
        return filter;
    }

    /// <summary>
    /// Add a network provider and bind the tuning space to it. Tries the generic
    /// Microsoft Network Provider first, then the specific DVB-T provider — a generic
    /// provider may reject an ad-hoc tuning space with E_INVALIDARG. Logs which one accepts.
    /// </summary>
    private void ConnectNetworkProvider()
    {
        (Guid clsid, string name)[] providers =
        {
            (CLSID_NetworkProvider, "Microsoft Network Provider (generic)"),
            (CLSID_DVBTNetworkProvider, "Microsoft DVBT Network Provider"),
        };

        foreach (var (clsid, name) in providers)
        {
            IBaseFilter? np = null;
            try
            {
                var type = Type.GetTypeFromCLSID(clsid);
                if (type == null) { _log.LogWarning("Provider not registered: {Name}", name); continue; }

                np = (IBaseFilter)Activator.CreateInstance(type)!;
                DsError.ThrowExceptionForHR(_graph!.AddFilter(np, name));

                var tuner = (ITuner)np;
                int hr = tuner.put_TuningSpace(_tuningSpace);
                if (hr == 0)
                {
                    _networkProvider = np;
                    _tunerIface = tuner;
                    _log.LogInformation("Network provider accepted tuning space: {Name}", name);
                    return;
                }

                _log.LogWarning("{Name} rejected tuning space: 0x{Hr:X8}", name, hr);
                _graph.RemoveFilter(np);
                Marshal.ReleaseComObject(np);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Provider {Name} failed", name);
                if (np != null) { try { _graph!.RemoveFilter(np); Marshal.ReleaseComObject(np); } catch { } }
            }
        }
        throw new InvalidOperationException("No network provider accepted the DVB-T tuning space.");
    }

    /// <summary>Enumerate a BDA category, add the first device whose pin connects to <paramref name="upstream"/>.</summary>
    private IBaseFilter? AddAndConnect(Guid category, IBaseFilter? upstream, string role)
    {
        foreach (var dev in DsDevice.GetDevicesOfCat(category))
        {
            IBaseFilter? f = null;
            try
            {
                if (_graph!.AddSourceFilterForMoniker(dev.Mon, null, dev.Name, out f) != 0 || f == null)
                    continue;
                if (_capture!.RenderStream(null, null, upstream, null, f) == 0)
                {
                    _log.LogInformation("{Role} connected: {Name}", role, dev.Name);
                    return f;
                }
                _graph.RemoveFilter(f);
                Marshal.ReleaseComObject(f);
                f = null;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "{Role} candidate {Name} failed", role, dev.Name);
                if (f != null) { try { _graph!.RemoveFilter(f); Marshal.ReleaseComObject(f); } catch { } }
            }
            finally { dev.Dispose(); }
        }
        if (role == "receiver") _log.LogDebug("No separate receiver component (tuner may be all-in-one)");
        return null;
    }

    public async Task<bool> TuneAsync(TuneParameters p, CancellationToken ct = default)
    {
        EnsureGraph();
        // If the graph was stopped (Stop button), restart it — otherwise the grabber
        // never runs, the pipe stays empty and the next Play blocks forever.
        if (_control != null && !_running)
        {
            DsError.ThrowExceptionForHR(_control.Run());
            _running = true;
            _log.LogInformation("BDA graph re-started after stop");
        }
        _pipe?.Clear(); // flush the old mux's TS so VLC gets a clean cut on zap
        SubmitTuneRequest(p);
        double mhz = p.FrequencyHz / 1_000_000.0;

        // Block on the real signal: poll SignalLocked until set or the lock window elapses.
        // The first/cold tune (PLL + AGC settle) has been MEASURED at up to ~3.35s — a too-short
        // window is the "needs 2 clicks" symptom (first attempt times out, second locks). The
        // window is per-request: zap path 5s (default), scanner 3s (dead frequencies stay cheap).
        int lockIters = Math.Max(1, p.LockTimeoutMs / 100);
        for (int i = 0; i < lockIters && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
            var s = GetSignalStats();
            if (s.Locked)
            {
                // 🔴 Drop the PRE-LOCK garbage TS that piled up while waiting for lock.
                // A cold/first tune can take 2.6–3.3s to lock; during that wait the grabber
                // feeds seconds of junk into the pipe. Without this clear, VLC's first read
                // starts on that junk, can't find a valid TS/PMT, and shows no picture — the
                // "needs 2 clicks" bug (the 2nd, warm retune locks in ~160ms = no junk → plays).
                _pipe?.Clear();
                _log.LogInformation("Locked @ {Mhz:F0} MHz: strength={S}% quality={Q}%", mhz, s.StrengthPercent, s.QualityPercent);
                // 🔴 PRE-BUFFER before play (Pantelis was right all along): fill the ring with
                // post-lock TS BEFORE handing the stream to VLC, so playback starts on a FULL buffer
                // instead of an empty pipe. An empty pipe (ring 0%) starves VLC → frames arrive late
                // → dropped → the fast-motion judder/blur. Fill ~2.5s first, then play.
                // Skipped for the scanner (p.Prebuffer=false) — it clears the pipe and captures its
                // own TS anyway, so prebuffering there only made the sweep slower.
                if (p.Prebuffer && _pipe != null)
                {
                    // TIME-based target: VLC's input cache is 3.0s (see VlcVideoPlayer media
                    // options) and to fill 3s of timeline it must ingest 3s × MUX rate of bytes.
                    // The mux rate varies per mux/allotment zone — measured 20.4-22.5 Mbps here,
                    // DVB-T capacity up to ~27 Mbps — so a fixed 6MB (≈2.1s @ 22.5 Mbps) left VLC
                    // starting ~1s short. Measure the actual fill rate and aim for 3.5s of mux
                    // (cache + 0.5s margin), clamped 6-12 MB (ring is 20 MB).
                    const int minBytes = 6 * 1024 * 1024, maxBytes = 12 * 1024 * 1024;
                    int targetBytes = minBytes;
                    long t0 = Environment.TickCount64;
                    long deadline = t0 + 6000;
                    long rate = 0; // bytes/s
                    while (_pipe.Available < targetBytes && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct);
                        long elapsed = Environment.TickCount64 - t0;
                        if (elapsed >= 1000) // rate estimate is meaningful after ~1s of flow
                        {
                            rate = _pipe.Available * 1000L / elapsed;
                            targetBytes = (int)Math.Clamp(rate * 35 / 10, minBytes, maxBytes);
                        }
                    }
                    double bufferedSec = rate > 0 ? (double)_pipe.Available / rate : 0;
                    _log.LogInformation("Prebuffered {KB} KB (~{Sec:F1}s of mux @ {Mbps:F1} Mbps) before play",
                        _pipe.Available / 1024, bufferedSec, rate * 8 / 1e6);
                }
                return true;
            }
        }
        _log.LogWarning("No lock @ {Mhz:F0} MHz", mhz);
        return false;
    }

    private void SubmitTuneRequest(TuneParameters p)
    {
        _tuningSpace!.CreateTuneRequest(out ITuneRequest request);
        var dvbReq = (IDVBTuneRequest)request;
        dvbReq.put_ONID(-1);
        dvbReq.put_TSID(-1);
        dvbReq.put_SID(-1);

        var locator = (IDVBTLocator)new DVBTLocator();
        locator.put_CarrierFrequency((int)(p.FrequencyHz / 1000)); // BDA carrier frequency is in kHz
        locator.put_Bandwidth(p.BandwidthMhz);
        dvbReq.put_Locator(locator);

        DsError.ThrowExceptionForHR(_tunerIface!.put_TuneRequest(request));
        Marshal.ReleaseComObject(locator);
        Marshal.ReleaseComObject(request);
    }

    public SignalStats GetSignalStats()
    {
        if (_tuner is not IBDA_Topology topology) return SignalStats.NoLock;

        var nodeTypes = new int[32];
        if (topology.GetNodeTypes(out int count, 32, nodeTypes) != 0) return SignalStats.NoLock;

        for (int i = 0; i < count; i++)
        {
            object? node = null;
            try
            {
                if (topology.GetControlNode(0, 1, nodeTypes[i], out node) != 0 || node == null) continue;
                if (node is IBDA_SignalStatistics ss)
                {
                    ss.get_SignalLocked(out bool locked);
                    ss.get_SignalStrength(out int strength);
                    ss.get_SignalQuality(out int quality);
                    return new SignalStats(locked, Clamp(strength), Clamp(quality), 0);
                }
            }
            catch { /* node without signal stats — skip */ }
            finally { if (node != null) Marshal.ReleaseComObject(node); }
        }
        return SignalStats.NoLock;
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 100 ? 100 : v;

    public IReadOnlyList<DvbEvent> GetSchedule(int serviceId) => _eit.GetSchedule(serviceId);

    public async Task<byte[]> CaptureTsAsync(int maxBytes, int timeoutMs, CancellationToken ct = default)
    {
        if (_pipe is null) return Array.Empty<byte>();
        // Drop the transition TS grabbed during tune/lock and let the tuner settle on the
        // new mux, so we capture FRESH, stable TS that actually carries the SDT/PAT.
        _pipe.Clear();
        await Task.Delay(700, ct);
        _pipe.Clear();
        var buf = new byte[maxBytes];
        int total = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (total < maxBytes && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            int n = _pipe.ReadAvailable(buf, total, maxBytes - total);
            if (n > 0) total += n;
            else await Task.Delay(30, ct);
        }
        if (total == maxBytes) return buf;
        var trimmed = new byte[total];
        Array.Copy(buf, trimmed, total);
        return trimmed;
    }

    public void Stop()
    {
        if (_control != null && _running) { _control.Stop(); _running = false; _log.LogInformation("BDA graph stopped"); }
    }

    private bool _disposed;

    public void Dispose()
    {
        // Idempotent: MainForm disposes us in FormClosing (while the STA message pump is still
        // alive — releasing the DirectShow COM graph AFTER Application.Run returns crashes with a
        // native "system error / unexpected parameters" on exit), and the DI host calls Dispose
        // again afterwards. The second call must be a no-op.
        if (_disposed) return;
        _disposed = true;
        Cleanup();
        _log.LogDebug("DvbTuner disposed");
    }

    private void Cleanup()
    {
        try { Stop(); } catch { /* best effort */ }
        Release(ref _tif);
        Release(ref _demux);
        if (_grabberIface != null) { Marshal.ReleaseComObject(_grabberIface); _grabberIface = null; }
        Release(ref _grabber);
        Release(ref _receiver);
        Release(ref _tuner);
        _tunerIface = null;
        Release(ref _networkProvider);
        if (_tuningSpace != null) { Marshal.ReleaseComObject(_tuningSpace); _tuningSpace = null; }
        if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        _control = null;
        if (_graph != null) { Marshal.ReleaseComObject(_graph); _graph = null; }
        if (_pipe != null) { try { _pipe.Dispose(); } catch { } _pipe = null; }
        _running = false;
    }

    private static void Release(ref IBaseFilter? f)
    {
        if (f != null) { Marshal.ReleaseComObject(f); f = null; }
    }

    /// <summary>Sample-grabber callback: feeds each raw TS buffer into the in-memory pull pipe (VLC) and the EPG tap.</summary>
    private sealed class TsGrabberCallback : ISampleGrabberCB
    {
        private readonly TsPipeStream _pipe;
        private readonly EitCollector _eit;
        private readonly ILogger _log;
        private long _bytes;
        private int _buffers;
        private bool _firstLogged;

        public TsGrabberCallback(TsPipeStream pipe, EitCollector eit, ILogger log)
        {
            _pipe = pipe;
            _eit = eit;
            _log = log;
        }

        public int SampleCB(double sampleTime, IMediaSample pSample) => 0;

        // Zero-allocation: feed the native TS buffer to the pull pipe (VLC) and the EPG tap.
        public unsafe int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
        {
            if (bufferLen <= 0 || pBuffer == IntPtr.Zero) return 0;
            var span = new ReadOnlySpan<byte>((void*)pBuffer, bufferLen);
            _pipe.Append(span);
            _eit.FeedBuffer(span);
            _bytes += bufferLen;
            _buffers++;
            if (!_firstLogged) { _firstLogged = true; _log.LogInformation("TS flowing to pipe (first buffer {Len} bytes)", bufferLen); }
            if (_buffers % 50 == 0)
                _log.LogInformation("TS throughput: {Buffers} buffers, {MB:F1} MB | ring {Fill}% ({AvailKB} KB)",
                    _buffers, _bytes / 1e6, _pipe.Available * 100 / _pipe.Capacity, _pipe.Available / 1024);
            return 0;
        }
    }
}
