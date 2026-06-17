# Architecture — DvbTv

## Γιατί αυτές οι επιλογές

| Απόφαση | Γιατί |
|---|---|
| **BDA + DirectShow** (όχι SDR) | Ο RTL2832U σε BDA mode κάνει COFDM demod σε hardware → MPEG-TS. SDR mode (Zadig) δίνει raw IQ → θα απαιτούσε όλο το DVB-T demod σε software (ανέφικτο). |
| **Hybrid: BDA tune → LibVLCSharp** | Το VLC κάνει demux + decode + render + audio + subs μόνο του. Full-DirectShow EVR = ίδιο αποτέλεσμα με 5× COM boilerplate. |
| **GPU decode `--avcodec-hw=d3d11va`** | NVDEC της RTX 3060. CPU ~0% στο decode → ελεύθερο για UI/zapping. |
| **DI (Generic Host) + Serilog** | Κάθε layer mockable/swappable· κάθε βήμα logάρεται → ξέρεις ΠΟΙΟ layer φταίει χωρίς ψάξιμο. |

## Data flow
```
RTL2832U stick :  RF → COFDM demod  →  MPEG-TS        [hardware στο stick, 0% PC CPU]
BDA/DirectShow :  USB → TS, demux PID                  [ασήμαντο CPU, σκέτο parsing]
LibVLCSharp    :  TS → H.264/MPEG-2 decode → NVDEC     [RTX 3060, GPU]
                  decoded frame → D3D11 render → οθόνη  [GPU, zero-copy]
```

## DI graph (Program.cs)
```
MainForm
 └─ ITvController ── TvController
      ├─ IDvbTuner ───── DvbTuner        (BDA — skeleton)
      ├─ IVideoPlayer ── VlcVideoPlayer  (LibVLC GPU)
      └─ IChannelStore ─ JsonChannelStore
 ├─ IChannelScanner ── ChannelScanner    (skeleton· εξαρτάται από IDvbTuner)
 ├─ IVideoPlayer  (shared singleton)
 └─ IChannelStore (shared singleton)
```
Όλα `AddSingleton` (ένα stick, ένας player, μία λίστα).

## Logging strategy
- Serilog → Console + rolling daily file `logs/dvbtv-<date>.log`, MinimumLevel Debug.
- Σε κάθε layer ένα `ILogger<T>` (constructor injection).
- Κρίσιμα events: tune attempt → lock success/fail + signal %, channel-change timing (Stopwatch ms), demux/PID, decode/VLC events (το `LibVLC.Log` γίνεται forward στο `ILogger`).
- Debug rule: «δεν παίζει κανάλι» → log δείχνει αν έσπασε **lock** (tuner/σήμα), **demux** (PID), ή **decode** (codec/VLC).

## Next milestones (λεπτομέρεια στο SKILL.md)
1. `DvbTuner` BDA graph (NetworkProvider→Tuner→Receiver→Demux, IDVBTLocator tune, IBDA_SignalStatistics lock). Χρειάζεται DirectShow.NET wrapper **με BDA** — ερεύνησε το σωστό NuGet πρώτα.
2. TS → VLC bridge (`StreamMediaInput`, ήδη stubbed).
3. `ChannelScanner` UHF sweep + SI/PSI.
4. Channel list UI + IR remote zapper.
