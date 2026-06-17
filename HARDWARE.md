# Hardware & Signal — DvbTv

## RTL2832U stick

A USB composite device with a media interface plus an IR-remote HID collection:

```
USB\VID_0BDA&PID_2838                          (USB Composite)
├─ MI_00  RTL2832U (DVB-T demodulator)         ← the tuner
├─ MI_01  HID Infrared Remote                  ← IR remote
└─ COLxx  HID keyboard / consumer / system     ← from the remote
```

**Notes:**

- **VID `0x0BDA` = Realtek**, **PID `0x2838`** = a classic RTL2832U DVB-T dongle,
  usually paired with an **R820T / R820T2** tuner.
- This app drives the stick over **WinUSB** (bind it with [Zadig](https://zadig.akeo.ie/)),
  talking to the chip directly with libusb and enabling its built-in demodulator.
  A legacy DirectShow/BDA path is also included for sticks on the vendor driver.
- With a single demodulator on board, the chip does **DVB-T only — not DVB-T2**. This is
  a hardware limit of the RTL2832U.
- The IR remote can be repurposed as a channel zapper (future work).

## R820T tuner specifics (WinUSB path)

- Detected by an I2C read at address `0x1a` returning `0x69`.
- Crystal: **28.8 MHz**.
- **IMR calibration is mandatory** on init (~2.8 s, once).
- **Manual gain** — the tuner AGC is unsuitable for DVB-T here, and the RTL2832's
  built-in AGC is left off.

## Signal — terrestrial DVB-T

- Many European countries (e.g. Greece) broadcast terrestrial TV as **DVB-T** with
  **MPEG-4 / H.264** (some local muxes still MPEG-2). DVB-T2 rollout varies by region.
- UHF band, **8 MHz** channel bandwidth.
- Because the broadcast is DVB-T, a plain RTL2832U DVB-T stick receives it directly.

> If your country uses **DVB-T2**, this stick (and app) will **not** demodulate it — the
> RTL2832U only does DVB-T.
