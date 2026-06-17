# DvbTv — DVB-T live TV on Windows

A clean, native Windows app that watches **digital terrestrial TV (DVB-T)** from a
cheap **RTL2832U USB stick**, written in **C# / .NET 9 (WinForms)**.

It talks to the RTL2832U's built-in hardware DVB-T demodulator over **WinUSB**, gets a
ready MPEG-TS stream, and plays it with **LibVLC** using **GPU decode (NVDEC / d3d11va)**.
Architecture is **dependency-injected** (.NET Generic Host) with **Serilog** logging from
line one, so debugging is reading a log — not guessing.

## Features

- 📺 **Live DVB-T TV** — tune, lock, and play terrestrial channels.
- 🔎 **Channel scan** — UHF sweep with SI/PSI parsing → real service names (PAT/SDT).
- 📡 **Live signal status bar** — frequency, signal %, quality %, SNR (dB), BER, lock state.
- 🗓️ **EPG** — a *now/next* window and a *weekly grid*, fed live from the EIT. The
  now/next view **auto-tracks** the current programme.
- 💬 **Subtitles / closed captions** — DVB bitmap subs and teletext subs.
- 🔊 **Volume controls** — mute / down / up, persisted across channel changes.
- 🖥️ **Fullscreen** (F11 / double-click), **app icon**, channel list, control bar.
- ⚡ **GPU decode** — NVDEC via `d3d11va`; the CPU only does TS/PID parsing.

## Hardware

- An **RTL2832U** USB stick (VID `0x0BDA`, PID `0x2838`), typically paired with an
  **R820T/R820T2** tuner.
- The stick must be bound to **WinUSB** (e.g. via [Zadig](https://zadig.akeo.ie/)), not
  the vendor BDA driver, so the app can talk to it directly with libusb.
- The chip's on-board demodulator is **DVB-T only** (not DVB-T2).

> A legacy **DirectShow / BDA** tuner backend is also included (`Tv:Tuner = "Bda"` in
> `appsettings.json`) for sticks left on the vendor driver. The stick is WinUSB **xor** BDA.

See [HARDWARE.md](HARDWARE.md) for the full device probe and broadcast-standard notes.

## Build & run

Requires the **.NET 9 SDK** (the project targets `net9.0-windows`, x64).

```powershell
dotnet build -c Debug
bin\Debug\net9.0-windows\DvbTv.exe
```

`libusb-1.0.dll` ships next to the executable. For LibVLC, the
`VideoLAN.LibVLC.Windows` NuGet package places the native runtime under `libvlc/`
in the output folder automatically.

### Pre-built release

A self-contained Windows x64 build (no .NET install needed) is published under
[**Releases**](../../releases) — unzip and run `DvbTv.exe`.

## Architecture

```
RTL2832U stick :  RF → COFDM demod  →  MPEG-TS     [hardware on the stick, ~0% CPU]
WinUSB / libusb:  USB bulk → TS, PID parsing       [trivial CPU]
LibVLCSharp    :  TS → H.264/MPEG-2 decode → NVDEC [GPU]
                  decoded frame → D3D11 render      [GPU, zero-copy]
```

Every layer sits behind an interface (`IDvbTuner`, `IVideoPlayer`, `IChannelScanner`,
`IChannelStore`, `ITvController`) and is wired with DI. See
[ARCHITECTURE.md](ARCHITECTURE.md).

## License

[MIT](LICENSE) © 2026 Pantelis.

This software uses **LibVLC** (via LibVLCSharp, LGPL-2.1) and **libusb** (LGPL-2.1).
DVB-T reception of free-to-air broadcasts only.
