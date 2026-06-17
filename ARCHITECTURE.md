# Architecture — DvbTv

## Why these choices

| Decision | Why |
|---|---|
| **WinUSB hardware demod** (default) | The RTL2832U has a built-in DVB-T demodulator. Talking to it directly over WinUSB/libusb gives a ready MPEG-TS — no software OFDM, no raw-IQ SDR work. |
| **LibVLCSharp for playback** | VLC handles demux + decode + render + audio + subtitles on its own. A full DirectShow EVR graph would be the same result with 5× the COM boilerplate. |
| **GPU decode `--avcodec-hw=d3d11va`** | Uses the GPU's video decoder (NVDEC on NVIDIA). The CPU stays near 0% on decode, free for UI/zapping. |
| **DI (Generic Host) + Serilog** | Every layer is mockable/swappable, and every step is logged → you know *which* layer failed without guesswork. |

A legacy **DirectShow / BDA** backend is also included for sticks left on the vendor
driver (`Tv:Tuner = "Bda"`). The stick is WinUSB **xor** BDA.

## Data flow

```
RTL2832U stick :  RF → COFDM demod  →  MPEG-TS        [hardware on the stick, ~0% PC CPU]
WinUSB / libusb:  USB bulk transfer → TS, PID parsing [trivial CPU]
LibVLCSharp    :  TS → H.264/MPEG-2 decode → NVDEC    [GPU]
                  decoded frame → D3D11 render → screen [GPU, zero-copy]
```

## DI graph (Program.cs)

```
MainForm
 ├─ ITvController ── TvController
 │     ├─ IDvbTuner ───── RtlSdrDvbTuner (WinUSB)  | DvbTuner (BDA, legacy)
 │     ├─ IVideoPlayer ── VlcVideoPlayer  (LibVLC, GPU)
 │     └─ IChannelStore ─ JsonChannelStore
 ├─ IChannelScanner ──── ChannelScanner
 ├─ IVideoPlayer  (shared singleton)
 └─ IChannelStore (shared singleton)
```

Everything is registered as a singleton (one stick, one player, one channel list).

## Components

| Interface | Implementation | Responsibility |
|---|---|---|
| `IDvbTuner` | `RtlSdrDvbTuner` (WinUSB) / `DvbTuner` (BDA) | Tune, signal stats, transport stream, TS capture, EPG schedule |
| `IVideoPlayer` | `VlcVideoPlayer` | LibVLCSharp, GPU decode, subtitles, volume |
| `IChannelStore` | `JsonChannelStore` | Channel persistence (`channels.json`) |
| `IChannelScanner` | `ChannelScanner` | UHF sweep + SI/PSI parse → services |
| `ITvController` | `TvController` | Orchestrator: tune + feed the player |
| — | `EitCollector` | Live EPG collector (PID 0x12, present/following + schedule) |
| — | `MainForm` + `EpgForm` + `WeeklyEpgForm` | WinForms UI: video + control bar + two EPG windows |

The `rtldvb/` class library is a focused port of the RTL2832U + R820T driver
(`LibUsb`, `RtlDevice`, `Rtl2832Frontend`, `R820tTuner`, I2C/DVB foundations) that
turns on the chip's hardware demodulator and reads MPEG-TS from the bulk endpoint.

## Logging strategy

- Serilog → Console + a rolling daily file `logs/dvbtv-<date>.log`, minimum level Debug.
- Each layer gets an injected `ILogger<T>`.
- Key events: tune attempt → lock success/fail + signal %, channel-change timing,
  demux/PID, decode/VLC events (LibVLC's own log is forwarded into Serilog).
- Debug rule: "no picture" → the log shows whether **lock** (tuner/signal),
  **demux** (PID), or **decode** (codec/VLC) failed.
