# Hardware & Signal — DvbTv

## RTL2832U stick (probe 2026-06-10, winpc `Get-PnpDevice`)

Ένα USB Composite device με δύο interfaces + IR remote HID collections:

```
USB\VID_0BDA&PID_2838                          (USB Composite, usbccgp)
├─ MI_00  REALTEK 2832U Device   Class MEDIA   Service RTL2832UUSB     ← ο BDA tuner
├─ MI_01  HID Infrared Remote    Class HID     Service RTL2832U_IRHID  ← IR remote
├─ COL01  HID Keyboard Device                  Service kbdhid          ← από το remote
├─ COL02  HID consumer control                                        ← από το remote
└─ COL03  HID system controller                                       ← από το remote
```

**Συμπεράσματα:**
- **VID 0x0BDA = Realtek**, **PID 0x2838** = κλασικό RTL2832U DVB-T dongle (συνήθως + R820T2 tuner).
- Driver service **`RTL2832UUSB`** = ο **Realtek DVB/BDA driver** → το stick είναι σε **κανονικό TV mode**, ΟΧΙ Zadig/WinUSB (SDR). 🔴 Μην το γυρίσεις σε WinUSB με Zadig — χάνεις το BDA TV path.
- **Μόνο ένα MEDIA interface, κανένα MN88472/73** → ο RTL2832U κάνει το demod, που σημαίνει **DVB-T only (όχι DVB-T2)**. Αυτό είναι hardware όριο του chip.
- IR remote (`RTL2832U_IRHID`) = bonus, μπορεί να γίνει zapper.

## Σήμα — Ελλάδα = DVB-T / H.264

- Πρότυπο μετάδοσης: **DVB-T** (όχι T2), συμπίεση **MPEG-4 part 10 / AVC (H.264)**· κάποια τοπικά ακόμα **MPEG-2**.
- Digea: 7 εθνικά FTA (Alpha, ANT1, Mega, Skai, Star, Makedonia, κ.ά.), 156 transmitter sites, 96% κάλυψη.
- DVB-T2 = «επόμενη γενιά», **χωρίς timeline** (μέχρι 2025/2026).
- **Γι' αυτό το RTL2832U DVB-T stick παίζει όλα τα ελληνικά κανάλια απρόβλητα** (επιβεβαιωμένο εμπειρικά από τον Pantelis 2026-06-10).
- UHF band, **8 MHz** channel bandwidth.

### ⚠️ Διόρθωση
Στο πρώτο μου draft είπα με σιγουριά «Ελλάδα = DVB-T2 HEVC» — **λάθος**. Είναι DVB-T/H.264. Ο Pantelis είχε δίκιο ότι το stick παίζει· η αντίφαση λύθηκε εμπειρικά (αφού παίζει & ο RTL2832U κάνει μόνο DVB-T → άρα εκπέμπεται DVB-T).

## Sources
- Digea — The Evolution of the Digital Era: https://www.digea.gr/en/technological-evolution/the-evolution-of-the-digital-era
- Digea (Wikipedia): https://en.wikipedia.org/wiki/Digea
- Digea FAQ: https://digea.gr/en/frequently-asked-questions
