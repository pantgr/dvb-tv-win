using System.Diagnostics;

namespace RtlDvb;

/// <summary>
/// Standalone driver self-test (regression). Kept as a callable method now that the
/// driver is a library consumed by DvbTv. Opens the stick, brings up the demod+R820T,
/// tunes a known mux, waits for hardware lock, dumps TS and checks 188-byte sync.
/// </summary>
public static class SelfTest
{
    public static int Run(long freqHz = 514_000_000L, long bwHz = 8_000_000L,
                          string outPath = @"C:\Claude\dvb_tv\rtldvb\capture.ts")
    {
        Console.WriteLine($"RtlDvb self-test @ {freqHz / 1_000_000.0:F1} MHz, BW {bwHz / 1_000_000} MHz");

        using var dev = new RtlDevice();
        try
        {
            dev.Open();
            Console.WriteLine("  opened + claimed OK");

            var sw = Stopwatch.StartNew();
            dev.Initialize();
            Console.WriteLine($"  initialized + IMR calibrated ({sw.ElapsedMilliseconds} ms), tuner={dev.TunerName}");

            sw.Restart();
            dev.Tune(freqHz, bwHz);
            Console.WriteLine($"  tuned ({sw.ElapsedMilliseconds} ms)");

            bool locked = false;
            sw.Restart();
            for (int i = 0; i < 40 && !locked; i++)
            {
                locked = dev.GetStatus().Contains(DvbStatus.FE_HAS_LOCK);
                if (!locked) Thread.Sleep(100);
            }
            Console.WriteLine($"  lock={locked} ({sw.ElapsedMilliseconds} ms)  SNR={dev.ReadSnr() / 10.0:F1} dB  RF={dev.ReadRfStrength()}%");
            if (!locked) return 2;

            dev.DisablePidFilter();
            var buf = new byte[256 * 1024];
            long total = 0;
            int zeroReads = 0;
            sw.Restart();
            using (var fs = File.Create(outPath))
            {
                while (sw.Elapsed.TotalSeconds < 4.0 && total < 8_000_000)
                {
                    int n = dev.ReadBulk(buf, 1000);
                    if (n <= 0) { if (++zeroReads > 5) break; continue; }
                    zeroReads = 0;
                    fs.Write(buf, 0, n);
                    total += n;
                }
            }

            var head = new byte[16384];
            int headLen;
            using (var fs = File.OpenRead(outPath)) headLen = fs.Read(head, 0, head.Length);
            int best = 0, bestOff = -1;
            for (int off = 0; off < 188 && off < headLen; off++)
            {
                int h = 0;
                for (int i = off; i + 188 < headLen; i += 188) if (head[i] == 0x47) h++;
                if (h > best) { best = h; bestOff = off; }
            }
            int packets = headLen / 188;
            Console.WriteLine($"  dumped {total / 1024} KB, TS sync offset={bestOff}, hits={best}/{packets}");
            return total > 0 && best >= packets * 9 / 10 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
