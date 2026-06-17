---
name: dvb-tv
description: DVB-T live TV viewer in C#/WinForms at C:\Claude\dvb_tv — reads the RTL2832U DVB-T USB stick via DirectShow BDA and plays Greek terrestrial TV with GPU (NVDEC / d3d11va) decode through LibVLCSharp. Clean DI architecture + Serilog logging. Use when working on dvb_tv, the DVB-T tuner / BDA graph, channel scan, the LibVLC player, or anything about watching terrestrial TV on the PC.
---

# DvbTv — DVB-T live TV στο PC (C# / WinForms)

`C:\Claude\dvb_tv\` — .NET 9 WinForms app που διαβάζει το **RTL2832U DVB-T USB stick** μέσω **DirectShow BDA** και παίζει ελληνική επίγεια TV με **GPU decode** (NVDEC της RTX 3060) μέσω **LibVLCSharp**. Καθαρή **DI** αρχιτεκτονική (.NET Generic Host) + **Serilog** logging από την πρώτη γραμμή, ώστε το debug να γίνεται διαβάζοντας log, όχι μαντεύοντας.

## 🔵 2026-06-16 — WinUSB hardware-demod path (ΝΕΟ DEFAULT, αντικαθιστά BDA)
Ο stick **δεν είναι πια σε BDA mode** — γυρίστηκε σε **WinUSB (Zadig)**. Νέα διαδρομή: μιλάμε **απευθείας στον RTL2832U μέσω libusb/WinUSB** και ενεργοποιούμε τον **ενσωματωμένο hardware DVB-T demodulator του chip** (όπως το `dvb_android` AndroidDvbDriver), που βγάζει έτοιμο MPEG-TS από bulk EP `0x81`. **ΟΧΙ** software OFDM, **ΟΧΙ** SDR/raw-IQ (το `librtlsdr` το χρησιμοποιούμε μόνο για το `libusb-1.0.dll`).
- **`rtldvb/` = class library** (port του AndroidDvbDriver, R820T path μόνο, ~2500 γρ.): `LibUsb.cs` (P/Invoke libusb-1.0), `RtlDevice.cs` (USB ctrl/I2C/power/detect/bulk), `Rtl2832Frontend(+Data).cs` (demod), `R820tTuner(+Data).cs` (tuner + IMR calibration), `Dvb.cs`/`I2cAdapter.cs` (foundations). `SelfTest.Run()` = standalone regression. `libusb-1.0.dll` ships δίπλα στο exe.
- **`Services/RtlSdrDvbTuner.cs` : IDvbTuner** — adapter: background thread διαβάζει bulk TS → ίδιο `TsPipeStream` + `EitCollector` που τάιζε ο BDA grabber. **Όλο το υπόλοιπο stack (VLC/scanner/EPG/UI) αναλλοίωτο.**
- **Config switch** `appsettings.json` → `Tv:Tuner` = `"WinUsb"` (default) ή `"Bda"`. Ο stick είναι **WinUSB XOR BDA** (όπως [[dvb-fm-win]]) — για να γυρίσει BDA θέλει αλλαγή driver στο Device Manager.
- 🔴 **R820T quirks (hard-won, port από dvb_android):** detect = I2C read `0x0034`==`0x69` @ addr `0x1a`· XTAL **28.8 MHz**· **IMR calibration ΥΠΟΧΡΕΩΤΙΚΟ** στο init (~2.8s, μία φορά)· **manual gain** (AGC νεκρό για DVB-T)· RTL2832 built-in AGC OFF· IF 3.57/4.57 MHz ανά BW.
- 🔴 **Cold-start «needs 2 clicks» (fixed):** το πρώτο play αμέσως μετά IMR έβγαζε **μαύρο** (VLC probe-άρει πριν σταθεροποιηθούν τα PIDs → πιάνει audio-only, `decoded=0`, λάθος audio fourcc `540242029` αντί `1634168941`). Fix στο `RtlSdrDvbTuner.TuneAsync`: σε cold start, **warm-up cycle** (tune+lock+stream 2s+stop+clear) ΠΡΙΝ το κανονικό play που βλέπει ο VLC. Verified: ένα κλικ παίζει.
- ✅ **VERIFIED live (2026-06-16):** lock SNR 28-37 dB, scan 7 muxes/60 services με ονόματα, εικόνα+ήχος (1080p25), zapping ~1s, EPG, ~22 Mbps, TS sync 99.96%. exec/build → `claude.cmd.winpc`.
- ⚠️ lost frames ~50% (`displayed≈decoded/2`) = ΙΔΙΟ deinterlace double-rate με το BDA path (δεν ενοχλεί οπτικά στα 25fps, βλ. smooth-motion section παρακάτω). Όχι regression.

### 2026-06-16 βράδυ — playback tuning (stutter/desync) — ✅ ΛΥΜΕΝΟ (δουλεύει ΟΚ)
Ο Pantelis παραπονέθηκε «πολύ κολλάει η εικόνα» + αργότερα «ξεσυγχρόνισε ήχος/εικόνα». **Reader instrumentation (στο `RtlSdrDvbTuner.ReaderLoop`) απέδειξε ότι το WinUSB streaming είναι ΑΘΩΟ:** `bulk avg 92-93ms σταθερό`, `zeroReads 0`, `eit-parse 0ms`, ring αδειάζει επειδή ο VLC το ρουφάει (όχι starve). **Η αιτία ήταν `decoded ≈ 2× displayed` = deinterlace double-rate** (50fps που το display δεν προλάβαινε → drops → stutter + A/V desync).
- ✅ **FIX που δούλεψε:** `media.AddOption(":deinterlace=0")` στο `VlcVideoPlayer.PlayStream` (ΟΧΙ το παλιό `:deinterlace=-1`+yadif που έτρεχε ακόμα double-rate). Η εικόνα **έστρωσε** (Pantelis: «έφτιαξε η εικόνα»). ⚠️ Αν εμφανιστεί combing σε interlaced κανάλι → δοκίμασε single-rate mode αντί off.
- ✅ **NVDEC — ΔΟΥΛΕΥΕΙ ΟΚ, ΜΗΝ ΤΟ ΠΕΙΡΑΞΕΙΣ (απόφαση Pantelis 2026-06-17).** Το GPU decode (d3d11va/NVDEC) μένει ενεργό· η εικόνα είναι ΟΚ έτσι. Το «κλείσιμο του NVDEC» (επειδή ανοίγει το NVIDIA game overlay) **εγκαταλείφθηκε** — δεν αξίζει, η λειτουργία είναι μια χαρά. Το build έχει `:avcodec-hw=none` per-media (αβλαβές/ανενεργό) + `:deinterlace=0`. 🔴 **ΜΗΝ ξανακυνηγήσεις forced software decode** — δουλεύει, άστο.
  - Ιστορικό (γιατί εγκαταλείφθηκε): global `--avcodec-hw=none`/`--codec=avcodec`/`--vout=gdi` ΑΓΝΟΟΥΝΤΑΙ (log: ακόμα `hardware acceleration picture allocation failed`)· per-media `:avcodec-hw=none` δεν το έκλεισε· `--vout=gdi` πάγωσε το UI thread (κόλλησε κουμπιά). Cosmetic μόνο → δεν το πειράζουμε.
- 🔴 **Μάθημα (δικό μου):** κυνήγησα το hw accel (θεωρία Pantelis) ενώ η **μέτρηση** (decoded 2× displayed) + το SKILL έδειχναν deinterlace. Έκανα 4 guess-builds (gdi/avcodec-hw/codec) που χάλασαν το app πριν ακούσω τη μέτρηση. [[feedback_dont_flail_config_ground_in_measurement]] + [[feedback_read_docs_before_guess_loop]].

## Status (2026-06-10 — BDA path, τώρα secondary): build verified (0 errors, 0 warnings), app τρέχει. ✅ **Player layer VERIFIED end-to-end από τον Pantelis**: εικόνα + **κρυστάλλινος ήχος** + **GPU decode (NVDEC) επιβεβαιωμένο από το NVIDIA stats overlay** (το GPU κάνει το decode, το CPU idle). DI + Serilog λειτουργικά. ✅ **LIVE TV + 60 ΚΑΝΑΛΙΑ ΜΕ ΟΝΟΜΑΤΑ (2026-06-10)** — tune → lock → TS → VLC → εικόνα+ήχος ομαλά (1080i hardware deinterlace, ~18.5 Mbps, μηδέν discontinuities στη σταθερή). Scan → **60 services με πραγματικά ονόματα** (ALPHA/SKAI/ANT1/MEGA/STAR/ΕΡΤ/RIK…) μέσω SDT/PAT parse· διπλό κλικ → `:program=<sid>` παίζει το συγκεκριμένο κανάλι.
- ✅ **EPG/UI ολοκληρωμένα & VERIFIED (2026-06-10):** (a) ξεχωριστό παράθυρο **EPG τώρα/μετά** (`EpgForm`) με ◄/► navigation + περιγραφή ανά εκπομπή· (b) ξεχωριστό **εβδομαδιαίο grid** (`WeeklyEpgForm`) grouped ανά μέρα· (c) **control bar κάτω από την εικόνα** (⏹ Stop · ▶ Play · 💬 Υπότιτλοι · ⛶ Full screen) **χωρίς να αγγίζει το video window**· (d) **fullscreen** (κουμπί/F11/double-click, Esc exit)· (e) **app icon** (TV, `app.ico` multi-size)· (f) **υπότιτλοι/CC** (DVB + teletext) live dropdown. Όλα τα EPG = ξεχωριστά top-level παράθυρα (hide-on-X), ΟΧΙ overlay.
- Μένει: **fast-rescan** μόνο γνωστών muxes· PID filtering· subtitle persistence across zap· IR remote zapper. Βλ. recipes παρακάτω.
- **PC: .NET 10 SDK** εγκατεστημένο· target `net9.0-windows` χτίζεται καθαρά (forward-compat, 0 warnings). Packages: Microsoft.Extensions.* 10.0.x, Serilog (Console 6.1.1/File 7.0.0/Ext.Hosting 10.0.0), LibVLCSharp 3.9.7.1 + WinForms 3.9.7.1, VideoLAN.LibVLC.Windows 3.0.23.1.
- **Shortcut:** `C:\Users\pantg\Desktop\DvbTv.lnk` → το exe (WorkingDirectory = bin ώστε τα logs να μένουν στο `bin\Debug\net9.0-windows\logs\`).
- **Test files** στον φάκελο: `test_video.mp4` (silent, video-only — test-videos.co.uk samples ΔΕΝ έχουν audio) + `test_sound.mp4` (BBB full, H.264+AAC, με ήχο). Άνοιγμα: TV → Open file.

## 🔴 Code-review fixes (2026-06-10 απόγευμα — build 0W/0E, backup: `C:\Claude\dvb_tv_backup_2026-06-10`)
Full review όλου του κώδικα + log analysis. Τα fixes (μην τα αναιρέσεις):
1. **Zap serialization (MainForm `ZapTo`)** — νέο zap ΑΚΥΡΩΝΕΙ το in-flight (`_zapCts`) και ΠΕΡΙΜΕΝΕΙ το `_zapTask` πριν στείλει tune. Πριν: 2 concurrent `ChangeChannelAsync` στο ίδιο BDA graph → ο 1ος έβλεπε το lock του 2ου → λάθος sid σε λάθος mux. Scan↔zap mutually exclusive (`_scanning`)· το Scan κάνει cancel zap + `_tv.Stop()` πριν ξεκινήσει. Stop button = `StopAll()` (cancel zap + `_tv.Stop()` μία φορά).
2. **`TuneParameters(freq, bw, LockTimeoutMs=5000, Prebuffer=true)`** — zap: 5s lock window (κρύο lock ΜΕΤΡΗΘΗΚΕ έως 3.35s, το παλιό 3s ήταν οριακό)· scanner: 3000ms + `Prebuffer:false` (το prebuffer στο scan ήταν ~2.5-5s σπατάλη/locked mux — το CaptureTsAsync το πετούσε).
   - **Prebuffer = TIME-based (2026-06-10 βράδυ):** στόχος **3.5s mux** (VLC cache 3s + 0.5s), μετρώντας live το fill rate, clamp 6-12MB, deadline 6s. Γιατί: το σταθερό 6MB ήταν ~2.1s στα ΠΡΑΓΜΑΤΙΚΑ 22.5 Mbps (μετρημένα: CH26=22.5, CH23=20.4· DIGEA 64-QAM 3/4 GI 1/8 = 24.88 Mbps θεωρητικό, έως 27.1 ανά ζώνη) → ο VLC ξεκινούσε με έλλειμμα ~1s. Το log τώρα γράφει `Prebuffered X KB (~Y.Ys of mux @ Z.Z Mbps)` = ground truth ανά zap.
3. **🔴 `_player.Stop()` στην ΑΡΧΗ του `ChangeChannelAsync`** — σε zap-ενώ-παίζει, ο VLC reader του παλιού καναλιού άδειαζε το κοινό pipe όσο γέμιζε το prebuffer → **«Prebuffered 0 KB before play»** στα logs + 5s χαμένο deadline + start σε άδειο buffer. Tradeoff: μαύρο ~2-4s στο zap (αν ενοχλεί: μελλοντικά fresh pipe ανά tune ώστε το παλιό να παίζει μέχρι το switch).
4. **`TvController.SetChannels()` μετά το scan** + `WeeklyEpgForm.RefreshChannels()` (και στο re-show) — πριν, μετά από scan το Weekly EPG έμενε με την παλιά/άδεια λίστα μέχρι restart. `Current` τίθεται ΜΟΝΟ μετά από επιτυχημένο tune+play.
5. **VLC log rate-limit (`ForwardVlcError`)** — το «Could not convert timestamp X for FFmpeg» έγραψε **60.5k/65k γραμμές (93%)** στο log της 2026-06-10 (per-frame, ώρες). Τώρα: 1η εμφάνιση logάρεται αμέσως, repeats (ίδιο σχήμα, digits stripped) → max 1 γραμμή/10s με `(+N repeats suppressed)`. Τίποτα δεν κρύβεται. ⚠️ Αν το μήνυμα ξαναφανεί στο νέο binary = πιθανό πραγματικό σύμπτωμα (συνέπιπτε με empty-buffer starts) — κυνήγα το, μην το αγνοήσεις.
6. **Media/StreamMediaInput leak** — `PlayStream` τώρα κάνει dispose το `Media` αμέσως (pattern του `Play`) και κρατά το `StreamMediaInput` μέχρι το επόμενο zap/Dispose (`_streamInput`)· τα `_player.Media` wrappers στα LogStats/LogVideoFormat = `using` (νέο wrapper + native ref ανά access). `ResetPlayDiagnostics()` ανά play → `VIDEO format` logάρεται σε ΚΑΘΕ zap, `lost (+delta)` χωρίς αρνητικά.
7. **appsettings.json wired** — `Tv:ChannelsFile` (JsonChannelStore) + `Tv:DefaultBandwidthMhz` (ChannelScanner) μέσω IConfiguration· νεκρό Serilog section αφαιρέθηκε (Serilog ρυθμίζεται στον κώδικα). Stale «UDP loopback» comments καθαρίστηκαν (DvbTuner)· stale init log «no-drop-late-frames» διορθώθηκε· ψεύτικο `snr=0dB` έφυγε από το log.

## 🔴 Hardware — confirmed (probe 2026-06-10, βλ. `HARDWARE.md`)
- Stick: **`REALTEK 2832U Device`**, `USB\VID_0BDA&PID_2838`, driver service **`RTL2832UUSB`** = κανονικό **DVB/BDA mode** (ΟΧΙ Zadig/SDR — μην το γυρίσεις σε WinUSB, χάνεις το TV path).
- MI_01 = `RTL2832U_IRHID` → IR τηλεχειριστήριο (μελλοντικός zapper).
- Κανένα δεύτερο demod chip → ο RTL2832U κάνει **DVB-T demod μόνο, ΟΧΙ DVB-T2**.

## 🔴 Σήμα — Ελλάδα = DVB-T (ΟΧΙ T2)
- Ελληνική επίγεια = **DVB-T** με **MPEG-4 / H.264** (κάποια τοπικά ακόμα MPEG-2). DVB-T2 = μελλοντικό, χωρίς timeline. Γι' αυτό το RTL2832U stick **τα παίζει όλα** (επιβεβαιωμένο εμπειρικά από Pantelis). UHF, 8 MHz channels. Sources στο `HARDWARE.md`.
- ⚠️ Στο πρώτο draft είχα πει λάθος «Ελλάδα = DVB-T2 HEVC». **Είναι DVB-T/H.264.** Μην το ξαναμπερδέψεις.

## Αρχιτεκτονική (DI, κάθε layer πίσω από interface — βλ. `ARCHITECTURE.md`)
| Interface | Impl | Δουλειά | Κατάσταση |
|---|---|---|---|
| `IDvbTuner` | `DvbTuner` | DirectShow BDA graph — `TuneAsync`, `GetSignalStats`, `TransportStream`, `CaptureTsAsync`, `GetSchedule` | **λειτουργικό** |
| `IVideoPlayer` | `VlcVideoPlayer` | LibVLCSharp, GPU decode (d3d11va), subtitles (SPU) | **λειτουργικό** |
| `IChannelStore` | `JsonChannelStore` | persistence καναλιών (channels.json) | λειτουργικό |
| `IChannelScanner` | `ChannelScanner` | UHF sweep + SI/PSI parse → 60 services | **λειτουργικό** |
| `ITvController` | `TvController` | orchestrator: tune + feed player (με Stopwatch timing) | λειτουργικό |
| — | `EitCollector` | live EPG collector (PID 0x12, p/f + schedule) fed από τον grabber | **λειτουργικό** |
| — | `MainForm` + `EpgForm` + `WeeklyEpgForm` | WinForms: video + control bar + 2 ξεχωριστά EPG παράθυρα | **λειτουργικό** |

Data flow: `RTL2832U (demod σε hw) → MPEG-TS → [BDA demux] → LibVLCSharp → NVDEC decode → D3D11 render`. Το CPU κάνει μόνο TS/PID parsing (μηδαμινό).

## Files
- `DvbTv.csproj` — net9.0-windows, `UseWindowsForms`, x64. Packages **δεν** έχουν version (μπαίνουν με `dotnet add package` = πάντα latest compatible, ΟΧΙ guess).
- `Program.cs` — Generic Host, DI registrations, Serilog (Console + `logs/dvbtv-.log` daily). Explicit `[STAThread] Main`.
- `Models/` — `Channel`, `SignalStats`, `TuneParameters`, `DvbEvent` (EPG).
- `Services/` — interface+impl ζεύγη (πίνακας πάνω) + `TsPipeStream` (ring buffer), `PsiParser` (SDT/PAT), `EitCollector` (EPG), `TsGrabberCallback`.
- `UI/` — `MainForm.cs` (video + control bar), `EpgForm.cs` (now/next), `WeeklyEpgForm.cs` (grid). Όλα προγραμματικά (χωρίς Designer).
- `app.ico` + `make_icon.ps1` — το TV icon (multi-size· το ps1 το (ανα)παράγει με System.Drawing → ICO container με embedded PNGs).
- `appsettings.json` — Tv config (channels file, bandwidth).

## Build / Run (μέσω **winpc** Kafka agent, ΟΧΙ direct Bash)
```
# Packages (μία φορά, latest compatible):
dotnet add C:\Claude\dvb_tv\DvbTv.csproj package Microsoft.Extensions.Hosting
dotnet add ... package Serilog.Extensions.Hosting
dotnet add ... package Serilog.Sinks.Console
dotnet add ... package Serilog.Sinks.File
dotnet add ... package LibVLCSharp
dotnet add ... package LibVLCSharp.WinForms
dotnet add ... package VideoLAN.LibVLC.Windows
# Build / run:
dotnet build C:\Claude\dvb_tv\DvbTv.csproj -c Debug
C:\Claude\dvb_tv\bin\Debug\net9.0-windows\DvbTv.exe   # ως pantg (GUI)
```
- ⚠️ Πριν κάθε build, kill running instance αν είναι ανοιχτό: `Get-Process DvbTv | Stop-Process -Force` (αλλιώς locked .exe, MSB3027 — δες vu-meter skill).
- Logs: `C:\Claude\dvb_tv\bin\Debug\net9.0-windows\logs\dvbtv-<date>.log`.

## Design decisions (κλειδωμένα — μην τα re-litigate)
- **BDA + DirectShow, ΟΧΙ SDR.** Ο RTL2832U σε BDA mode κάνει το COFDM demod σε hardware → MPEG-TS. SDR mode (Zadig) = raw IQ → θα έπρεπε να γράψουμε όλο το DVB-T demod σε software (ανέφικτο). 
- **Hybrid playback: BDA tune → LibVLCSharp render** (αντί full-DirectShow EVR). Πολύ λιγότερο COM boilerplate, λύνει αυτόματα H.264/MPEG-2/audio/subs. Το full-DirectShow θα έκανε ίδιο GPU decode με 5× κόπο.
- **GPU decode: `--avcodec-hw=d3d11va`** στο LibVLC init → NVDEC της 3060. CPU ~0% στο decode, ελεύθερο για UI/zapping. Pantelis το ζήτησε ρητά.
- **DI + Serilog πρώτα** (Pantelis: «καθαρός κώδικας + logs για εύκολο debug»). Κάθε tune/lock/demux/decode logάρεται → ξέρεις ΠΟΙΟ layer φταίει.
- ⚠️ Zapping bottleneck = **tuner lock time** (~μερικά 100ms, φυσικό όριο DVB), ΟΧΙ το decode. Το GPU offload δεν το επιταχύνει αυτό.

## 🔴 BDA tuning — WORKING recipe (verified 2026-06-10)
Δουλεύει — **7 ελληνικά muxes locked, quality 100%**: CH23/490, CH26/514, CH29/538, CH32/562, CH34/578, CH35/586, CH40/626 MHz (strength ~42-53%). Το σωστό recipe (κόστισε ~10 iterations — μην το ξαναβρείς από την αρχή):
- **NuGet: `DirectShowLib.Standard` 2.1.0** (netstandard2.0, έχει `DirectShowLib.BDA`).
- **🔴 Specific Microsoft DVBT Network Provider (`216C62DF-6D7F-4E9A-8571-05F14EDB766A`), ΟΧΙ ο generic (`B2F3A67C-...`)** — ο generic απορρίπτει ad-hoc tuning space με **E_INVALIDARG (0x80070057)** στο `put_TuningSpace`. Ο κώδικας δοκιμάζει generic→specific με fallback (`ConnectNetworkProvider`).
- **Tuning space:** `new DVBTuningSpace()` → set ΚΑΙ τις 2 μορφές network type (`put_NetworkType(string BSTR "{216C62DF...}")` + `put__NetworkType(Guid)` — το fork έχει διπλό) + `put_SystemType(Terrestrial)` + **`put_DefaultLocator`** (REQUIRED, αλλιώς E_INVALIDARG).
- **Graph:** NetworkProvider → Tuner (`REALTEK DTV Filter`, BDASourceFiltersCategory· δεν έχει separate receiver, all-in-one) → **MPEG-2 Demux** (`AFB6C280-...`) → **TIF** (BDATransportInformationRenderersCategory). **🔴 Χωρίς Demux+TIF το `Run()` πετάει 0x8007048F "device not connected".**
- **Carrier frequency = kHz** στο `IDVBTLocator.put_CarrierFrequency` (όχι Hz/MHz)· bandwidth = 8.
- **Signal lock:** `IBDA_Topology.GetNodeTypes` → `GetControlNode` → `IBDA_SignalStatistics.get_SignalLocked/Strength/Quality`.
- Όλα τα COM στο WinForms STA (UI) thread· lock = await-poll του SignalLocked (~1.5s/freq). Debug = granular `BDA step:` logging στο `BuildGraph`.

## 🔴 TS → VLC bridge — WORKING (verified 2026-06-10)
Live playback: Sample Grabber inline (**tuner → grabber → demux**), `MediaSubType.BdaMpeg2Transport`, `ISampleGrabberCB.BufferCB` → **in-memory pull pipe** (`TsPipeStream`, 16 MB ring) → `VlcVideoPlayer.PlayStream` (`StreamMediaInput`, network/live-caching 1000ms).
- 🔴 **PULL (in-memory), ΟΧΙ UDP push.** Δοκίμασα πρώτα UDP loopback (`udp://@127.0.0.1:1234`): ο grabber δίνει **~235 KB bursts ~10/s** που ξεχείλιζαν το VLC receive buffer → χαμένα datagrams → **TS discontinuities σε ΟΛΑ τα PIDs** = σοβαρό stutter. network-caching / SNDBUF / connected-socket **ΔΕΝ** το έλυσαν (hard loss, όχι jitter). Το pull model (VLC τραβάει με τον ρυθμό του από ring buffer, drop-oldest μόνο αν μείνει πίσω >~1s) το έλυσε τελείως, ~18.5 Mbps σταθερό. **Μην ξαναγυρίσεις σε UDP.** (Pantelis το είχε πει εξαρχής «γιατί όχι memory buffer».)
- Zero-allocation `BufferCB`: `ReadOnlySpan` πάνω στο native buffer (`<AllowUnsafeBlocks>`), όχι `new byte[]`/`Marshal.Copy` ανά buffer (GC churn → drops).

## 🔴 SI/PSI (channel names) — WORKING (verified 2026-06-10)
`PsiParser.cs`: parse **SDT (PID 0x11, table 0x42)** → service names, **PAT (PID 0x00)** → program list. Ο scanner ανά locked mux καλεί `IDvbTuner.CaptureTsAsync` (6 MB / 7s, με Clear+settle 700ms για φρέσκο TS), parse → ένα `Channel` ανά service (Name + ServiceId). Playback: `:program=<ServiceId>` στο VLC διαλέγει το σωστό service από το MPTS. 60 ελληνικά κανάλια με ονόματα (ALPHA/SKAI/ANT1/MEGA/STAR/ΕΡΤ/RIK/TV5…).
- 🔴 **TS SYNC ALIGNMENT — το κρίσιμο bug (κόστισε ~5 iterations).** Ο sample grabber δίνει buffers **shifted** μετά από re-tune → το captured buffer ΔΕΝ ξεκινά σε όριο TS packet (188). Ο parser ΠΡΕΠΕΙ να βρει το sync offset (`FindSyncOffset`: πού επαναλαμβάνεται 0x47 κάθε 188), ΟΧΙ να υποθέσει offset 0. Σύμπτωμα ήταν: **μόνο ο 1ος mux (φρέσκο graph) έδινε services, όλοι οι re-tune έδιναν 0**. (Το VLC playback δούλευε γιατί κάνει sync detection μόνο του.)
- Ονόματα: τα ελληνικά κανάλια στέλνουν Latin service names στο SDT — μηδέν encoding issue (το `System.Text.Encoding.CodePages` + ISO-8859-7 handling υπάρχει αν χρειαστεί).
- `diag CH..: aligned=N/M` log στον scanner = TS alignment health (κράτησε το για debug· aligned από offset-0 είναι ~0 στα re-tune, αλλά ο parser βρίσκει το σωστό offset).

## 🔴 Playback robustness — Stop/Play + «needs 2 clicks» (fixed 2026-06-10)
Δύο πραγματικά bugs που τα έλυσε **το log, όχι μάντεμα** (κράτα τα fixes):
- **Stop→Play crash/freeze:** το Stop button έκανε `_control.Stop()` (graph stopped) αλλά το επόμενο Play (`EnsureGraph`) έβλεπε `_graph != null` → ΔΕΝ ξανα-`Run()` → ο grabber δεν έτρεχε → το pipe έμενε άδειο → ο VLC reader μπλόκαρε **για πάντα** στο `TsPipeStream.Read` (Monitor.Wait). Fix: (a) `TuneAsync` κάνει `_control.Run()` αν `!_running`· (b) `TsPipeStream.Read` έχει **idle timeout 3s → επιστρέφει 0 (EOF)** αντί να μπλοκάρει αιώνια.
- **Play button αγνοούσε την επιλογή λίστας:** το `PlayCurrent` είχε `_tv.Current ?? selected` → προτιμούσε το τρέχον. Fix → `_channels.SelectedItem ?? _tv.Current` (η επιλογή λίστας έχει προτεραιότητα· fallback στο τρέχον μόνο αν δεν έχει επιλεγεί τίποτα, π.χ. Play μετά Stop).
- **🔴 «Πάντα 2 κλικ» για να παίξει:** root cause = **pre-lock garbage TS**. Το `TuneAsync` καθάριζε το pipe **ΠΡΙΝ** το tune, μετά περίμενε lock. Σε **κρύο/πρώτο tune το lock θέλει 2.6–3.3s** (PLL+AGC settle) — μέσα σε αυτά ο grabber γέμιζε το pipe με δευτερόλεπτα pre-lock junk (χωράει στο 16-20MB ring, δεν evict-άρεται). Το VLC διάβαζε το junk πρώτο → δεν έβρισκε valid TS/PMT → **καμία εικόνα**. Στο 2ο κλικ το lock ήταν ~160ms → μηδέν junk → έπαιζε. Διάγνωση από log correlation: lock 161/157/158ms → εικόνα ✅· lock 2644/3351ms → no picture ❌. **Fix: `_pipe?.Clear()` ΜΕΤΑ το lock** (μέσα στο `if (s.Locked)`), όπως κάνει ήδη το `CaptureTsAsync`. Επίσης το lock-poll window ανέβηκε 1.5s→3s (15→30 iters) γιατί ο κρύος tuner θέλει >1.5s για να κλειδώσει καν.

## 🔴 Crash στο κλείσιμο — `0xc0000005 / "Exception Processing Message ... Unexpected parameters"` (fixed 2026-06-10)
Native AV στο exit, ΟΧΙ στο Serilog (uncatchable). **Διάγνωση = .NET Runtime crash stack από το Windows Event Log** (`Get-WinEvent -LogName Application`, ProviderName `.NET Runtime`) — ΟΧΙ μάντεμα:
```
at LibVLCSharp...LibVLCLogUnset → LibVLC.Dispose → VlcVideoPlayer.Dispose() → Host.Dispose() → Program.Main
```
- **Root cause = DOUBLE-DISPOSE του LibVLC.** Το `MainForm.FormClosing` κάνει `_player.Dispose()` (1η φορά, STA thread), μετά ο **DI host** (`using var host` στο Main) κάνει dispose το ΙΔΙΟ singleton **ξανά** → `LibVLC.Dispose` 2η φορά → `LibVLCLogUnset` σε freed handle → 0xc0000005.
- **Fix = idempotent `Dispose` (`_disposed` guard)** σε `VlcVideoPlayer` **ΚΑΙ** `DvbTuner` → η 2η κλήση no-op. Επίσης το BDA COM teardown γίνεται στο `FormClosing` (`_tuner.Dispose()`, STA thread — releasing τον graph μετά το `Application.Run` crash-άρει).
- **Μάθημα:** για native crash στο exit → **Event Log (.NET Runtime / Application Error) για το faulting module/stack**, μην μαντεύεις VLC-vs-DirectShow. Το stack έδειξε αμέσως LibVLC, όχι DirectShow.

## 🔴 Smooth motion — fps/resolution symmetry (THE big one, fixed 2026-06-10, ~12 builds)
Το χειρότερο rabbit-hole της μέρας. Σύμπτωμα: «παίρνει φόρα και σταματάει» / θολούρα σε γρήγορη κίνηση. **Root cause βρέθηκε με VLC track-info + nvidia-smi, ΟΧΙ εικασία:**
- **Το περιεχόμενο (DIGEA) είναι 1920×1080 @ 25fps interlaced (1080i25 = 50 πεδία/s)** — επιβεβαιωμένο από `media.Tracks` (`VIDEO format` log· `player.Fps=25`).
- **Ο ένοχος ήταν το DOUBLE-RATE deinterlace:** το `:deinterlace=1` (default mode) έβγαζε **50fps**, αλλά το display παρουσίαζε μόνο ~30fps (decoded≈50/s, displayed≈30/s, **lost≈10-20/s** στα VLC `media.Statistics`). Τα late frames πετιόντουσαν → judder/blur. **Ασυμμετρία source-vs-output.**
- **🔴 FIX = συμμετρία: output rate = source rate.** `:deinterlace-mode=yadif` (**SINGLE-rate**) → 25fps output = όσο το source → χωράει → καθαρή ομαλή εικόνα. (Pantelis: «κλείδωσέ το στα 25 όπως το original· διάβαζε τι fps βγάζει & κλείδωνε ανάλογα».) Resolution = **native 1920×1080** (κανένα forced scale → 1:1). VLC ήδη παίζει source-rate (PTS) + native res· το μόνο που χρειαζόταν ήταν να ΜΗΝ διπλασιάζουμε το rate.
- **Τι ΔΕΝ έφταιγε** (αποκλείστηκαν με μετρήσεις, μην τα ξαναψάξεις): decode method (GPU d3d11va vs `--avcodec-hw=none` — το `lost` ήταν ΙΔΙΟ· σημείωση: `--avcodec-hw=none` ΔΕΝ έκλεινε το NVDEC, nvidia-smi `dec`>0, χρειάζεται `--codec=avcodec` — αλλά άχρηστο εδώ), deinterlace on/off, caching (1000 vs 3000), pre-buffer, vout.
- **`--no-drop-late-frames` = ΛΑΘΟΣ** (κρατάει late frames αλλά **desync ήχου/εικόνας** «αλλού ήχος αλλού εικόνα») — μην το ξαναβάλεις.
- **Buffer/clock config που έμεινε:** `--avcodec-hw=d3d11va` + `--vout=direct3d11` (zero-copy, έλυσε το `hardware acceleration picture allocation failed` + recurring `buffer deadlock prevented` στο παλιό d3d11va-χωρίς-vout)· media `:network/live/file-caching=3000` (StreamMediaInput μετριέται σαν stream/file)· **pre-buffer ~2.5s του ring ΠΡΙΝ το PlayStream** (`DvbTuner.TuneAsync` μετά το lock, μέχρι `_pipe.Available≥6MB`) ώστε το VLC να ξεκινά με γεμάτο buffer, όχι σε live-edge.
- ⚠️ **Display cap ~30fps σε 160Hz παραμένει ανεξήγητο** (decoded 50→displayed 30 ακόμα στα stats) αλλά **ΔΕΝ ενοχλεί οπτικά** στα 25fps single-rate — ο `lost` counter ΔΕΝ μεταφράζεται σε ορατό stutter εδώ. Full 50fps fluidity θα ήθελε οθόνη σε **100/50Hz** (το 160 δεν διαιρείται με 25/50 → cadence). Όχι τώρα.

## Υπότιτλοι / CC (DVB + teletext) — WORKING (2026-06-10)
`IVideoPlayer.GetSubtitleTracks()/CurrentSubtitle/SetSubtitle(id)` πάνω στο VLC SPU API (`MediaPlayer.SpuDescription` / `Spu` / `SetSpu(int)`, id=-1 = off). UI: dropdown **💬 Υπότιτλοι** στο control bar, γεμίζει **live** στο `DropDownOpening` (tracks υπάρχουν μόνο αφού το κανάλι τα εκπέμπει + το VLC ανιχνεύσει το ES). DVB bitmap subs + teletext subs εμφανίζονται και τα δύο ως SPU tracks (το VLC build έχει zvbi). ⚠️ SPU γίνεται reset σε κάθε `PlayStream` (νέο media ανά zap) → re-select μετά από αλλαγή καναλιού (persistence = μελλοντικό).

## Log noise — filtered στο VLC Log handler (2026-06-10)
Το `VlcVideoPlayer.Initialize` φιλτράρει 2 **benign** VLC Error-level μηνύματα που σπαμάρουν σε κάθε υγιές live-TS play (αλλιώς το log γίνεται άχρηστο):
- `libdvbpsi error (EIT decoder): 'version_number' differs whereas no discontinuity has occurred` — ο πομπός αυξάνει το EIT `version_number` σε κάθε EPG update (PID 0x12, EPG metadata μόνο, ΟΧΙ A/V)· εμείς κάνουμε EPG μόνοι μας (`EitCollector`), άρα άσχετο. Τεκμηρίωση: forum.videolan.org/viewtopic.php?t=141970.
- `imem: Invalid get/release function pointers` — harmless `StreamMediaInput` probe note (εμφανίζεται και στα επιτυχημένα plays).
- Τα υπόλοιπα VLC Error μηνύματα ΠΕΡΝΟΥΝ (forward ως `[VLC]` WRN). `buffer deadlock prevented` = κανονικό startup hiccup, σημάδι ότι ξεκίνησε το playback (το αφήνουμε).

## Next milestones
1. ~~EPG + info panel~~ ✅ DONE (now/next + weekly, ξεχωριστά παράθυρα). ~~Control bar / fullscreen / icon / subtitles~~ ✅ DONE.
2. **Fast rescan** — σάρωσε μόνο τις γνωστές frequencies (από saved channels) αντί για όλο το UHF (28 → 7, ~3× γρηγορότερο). Ο Pantelis γκρίνιαξε για το slow full sweep. **(top αίτημα)**
2β. **Keyframe-aligned zap (RAI)** — στο zap, το TS buffer ξεκινά σε τυχαίο σημείο του GOP → ~0.5-1s «χαζά» (blockiness από reference-less P/B frames) μέχρι το πρώτο I-frame, μετά στρώνει (παρατήρηση Pantelis 2026-06-10, του άρεσε η λύση). Fix: ο pipe να ξεκινά το buffer από TS packet με **random_access_indicator** στο adaptation field του video PID → καθαρό άνοιγμα. Θέλει PMT parse για video PID → δένει με το PID filtering (#4). Το `hw picture allocation failed` 1×/zap = ίδιο σκηνικό (allocation πάνω σε ατελή stream info), benign.
3. **Subtitle persistence across zap** — να θυμάται το επιλεγμένο SPU track μετά από αλλαγή καναλιού.
4. PID filtering ενός προγράμματος (MPTS ~18 Mbps → ~3-4 Mbps)· UI zap up/down· IR remote (`RTL2832U_IRHID`) ως zapper· threading (COM σε dedicated STA αν παγώνει το UI στο scan).

## 📁 md του project (το index — «η σύνδεση στα md»)
- `HARDWARE.md` — full hardware probe (USB device tree, driver, IR) + ελληνικό DVB-T standard με sources. **Διάβασέ το πρώτο σε νέο session.**
- `ARCHITECTURE.md` — layers, DI graph, data flow, logging strategy, design rationale.
- `README.md` — quick build/run.
- (`SKILL.md` = αυτό· `appsettings.json`, `channels.json` = config/data.)

## Σχετικά skills
[[vu-meter]] (το άλλο C#/.NET 9 desktop project — ίδιο build pattern, kill-before-build gotcha), [[kafka-cmd-agent]] (winpc execution), [[flussonic]] (DVB/TBS streaming, διαφορετικό hardware path).
