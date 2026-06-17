namespace RtlDvb;

/// <summary>
/// Rafael Micro R820T tuner. Port of R820tTuner.java.
/// Manual gain only (AGC is dead for DVB-T on this combo); IMR calibration mandatory at init.
/// </summary>
internal sealed class R820tTuner : IDvbTuner
{
    private const int MAX_I2C_MSG_LEN = 2;
    private const int VCO_POWER_REF = 0x02;
    private const int VER_NUM = 49;
    private const int NUM_REGS = 27;
    private const int REG_SHADOW_START = 5;
    private const int NUM_IMR = 5;
    private const int IMR_TRIAL = 9;

    private readonly int _i2cAddress;
    private readonly I2cAdapter _i2c;
    private readonly RafaelChip _chip;
    private readonly long _xtal;
    private readonly I2GateControl _gate;

    private readonly byte[] _regs = new byte[NUM_REGS];
    private readonly SectType[] _imrData = SectType.NewArray(NUM_IMR);

    private XtalCapValue _xtalCapValue;
    private bool _hasLock;
    private bool _imrDone;
    private bool _initDone;
    private long _intFreq;
    private long _mBw;
    private int _filCalCode;

    public R820tTuner(int i2cAddress, I2cAdapter i2c, RafaelChip chip, long xtal, I2GateControl gate)
    {
        _i2cAddress = i2cAddress;
        _i2c = i2c;
        _chip = chip;
        _xtal = xtal;
        _gate = gate;
    }

    // ---- IO ----

    private static byte BitRev8(byte x)
    {
        int v = x & 0xff;
        v = ((v & 0x55) << 1) | ((v >> 1) & 0x55);
        v = ((v & 0x33) << 2) | ((v >> 2) & 0x33);
        v = ((v & 0x0f) << 4) | ((v >> 4) & 0x0f);
        return (byte)v;
    }

    private void ShadowStore(int reg, byte[] val)
    {
        int len = val.Length;
        int r = reg - REG_SHADOW_START;
        if (r < 0) { len += r; r = 0; }
        if (len <= 0) return;
        if (len > NUM_REGS) len = NUM_REGS;
        Array.Copy(val, 0, _regs, r, len);
    }

    private void Write(int reg, byte[] val)
    {
        ShadowStore(reg, val);

        int len = val.Length;
        int pos = 0;
        var buf = new byte[len + 1];
        do
        {
            int size = len > MAX_I2C_MSG_LEN - 1 ? MAX_I2C_MSG_LEN - 1 : len;
            buf[0] = (byte)reg;
            Array.Copy(val, pos, buf, 1, size);
            _i2c.Transfer(_i2cAddress, 0, buf, size + 1);
            reg += size;
            len -= size;
            pos += size;
        } while (len > 0);
    }

    private void WriteReg(int reg, int val) => Write(reg, [(byte)val]);

    private void WriteRegMask(int reg, int val, int bitMask)
    {
        int rc = ReadCacheReg(reg);
        val = (rc & ~bitMask) | (val & bitMask);
        Write(reg, [(byte)val]);
    }

    private void Read(int reg, byte[] val, int len)
    {
        _i2c.Transfer(_i2cAddress, 0, [(byte)reg], _i2cAddress, I2cMessage.I2C_M_RD, val);
        for (int i = 0; i < len; i++) val[i] = BitRev8(val[i]);
    }

    private void Read(int reg, byte[] val) => Read(reg, val, val.Length);

    private int ReadCacheReg(int reg)
    {
        reg -= REG_SHADOW_START;
        if (reg < 0 || reg >= NUM_REGS) throw new ArgumentException("reg out of shadow range");
        return _regs[reg] & 0xff;
    }

    private int MultiRead()
    {
        int sum = 0, min = 255, max = 0;
        var data = new byte[2];

        SleepUtils.Usleep(5_000);
        for (int i = 0; i < 6; i++)
        {
            Read(0, data);
            int dataVal = data[1] & 0xff;
            sum += dataVal;
            if (dataVal < min) min = dataVal;
            if (dataVal > max) max = dataVal;
        }

        int rc = sum - max - min;
        if (rc < 0) throw new RtlUsbException("failed calibration step");
        return rc;
    }

    // ---- logic ----

    private void InitRegs() => Write(REG_SHADOW_START, R820tTunerData.INIT_REGS);

    private void ImrPrepare()
    {
        Array.Copy(R820tTunerData.INIT_REGS, 0, _regs, 0, R820tTunerData.INIT_REGS.Length);
        WriteRegMask(0x05, 0x20, 0x20); // lna off (air-in off)
        WriteRegMask(0x07, 0, 0x10);    // mixer gain mode = manual
        WriteRegMask(0x0a, 0x0f, 0x0f); // filter corner = lowest
        WriteRegMask(0x0b, 0x60, 0x6f); // filter bw=+2cap, hp=5M
        WriteRegMask(0x0c, 0x0b, 0x9f); // adc=on, vga code mode, gain 26.5dB
        WriteRegMask(0x0f, 0, 0x08);    // ring clk = on
        WriteRegMask(0x18, 0x10, 0x10); // ring power = on
        WriteRegMask(0x1c, 0x02, 0x02); // from ring = ring pll in
        WriteRegMask(0x1e, 0x80, 0x80); // sw_pdect = det3
        WriteRegMask(0x06, 0x20, 0x20); // set filt_3dB
    }

    private void VgaAdjust()
    {
        for (int vgaCount = 12; vgaCount < 16; vgaCount++)
        {
            WriteRegMask(0x0c, vgaCount, 0x0f);
            SleepUtils.Usleep(10_000L);
            int rc = MultiRead();
            if (rc > 40 * 4) break;
        }
    }

    private bool ImrCross(SectType[] iqPoint)
    {
        var cross = SectType.NewArray(5);
        int reg08 = ReadCacheReg(0x08) & 0xc0;
        int reg09 = ReadCacheReg(0x09) & 0xc0;

        var tmp = new SectType { Value = 255 };

        for (int i = 0; i < 5; i++)
        {
            switch (i)
            {
                case 0: cross[i].GainX = reg08; cross[i].PhaseY = reg09; break;
                case 1: cross[i].GainX = reg08; cross[i].PhaseY = reg09 + 1; break;
                case 2: cross[i].GainX = reg08; cross[i].PhaseY = (reg09 | 0x20) + 1; break;
                case 3: cross[i].GainX = reg08 + 1; cross[i].PhaseY = reg09; break;
                default: cross[i].GainX = (reg08 | 0x20) + 1; cross[i].PhaseY = reg09; break;
            }

            WriteReg(0x08, cross[i].GainX);
            WriteReg(0x09, cross[i].PhaseY);

            cross[i].Value = MultiRead();
            if (cross[i].Value < tmp.Value) tmp.CopyFrom(cross[i]);
        }

        if ((tmp.PhaseY & 0x1f) == 1) // y-direction
        {
            iqPoint[0].CopyFrom(cross[0]);
            iqPoint[1].CopyFrom(cross[1]);
            iqPoint[2].CopyFrom(cross[2]);
            return false;
        }
        else // (0,0) or x-direction
        {
            iqPoint[0].CopyFrom(cross[0]);
            iqPoint[1].CopyFrom(cross[3]);
            iqPoint[2].CopyFrom(cross[4]);
            return true;
        }
    }

    private static void CompreCor(SectType[] iq)
    {
        for (int i = 3; i > 0; i--)
        {
            int otherId = i - 1;
            if (iq[0].Value > iq[otherId].Value)
            {
                var temp = new SectType();
                temp.CopyFrom(iq[0]);
                iq[0].CopyFrom(iq[otherId]);
                iq[otherId].CopyFrom(temp);
            }
        }
    }

    private void CompreSep(SectType[] iq, int reg)
    {
        var tmp = new SectType { PhaseY = iq[0].PhaseY, GainX = iq[0].GainX };

        while (((tmp.GainX & 0x1f) < IMR_TRIAL) && ((tmp.PhaseY & 0x1f) < IMR_TRIAL))
        {
            if (reg == 0x08) tmp.GainX++; else tmp.PhaseY++;

            WriteReg(0x08, tmp.GainX);
            WriteReg(0x09, tmp.PhaseY);

            tmp.Value = MultiRead();

            if (tmp.Value <= iq[0].Value)
            {
                iq[0].GainX = tmp.GainX;
                iq[0].PhaseY = tmp.PhaseY;
                iq[0].Value = tmp.Value;
            }
            else return;
        }
    }

    private void IqTree(SectType[] iq, int fixVal, int varVal, int fixReg)
    {
        int varReg = fixReg == 0x08 ? 0x09 : 0x08;

        for (int i = 0; i < 3; i++)
        {
            WriteReg(fixReg, fixVal);
            WriteReg(varReg, varVal);

            iq[i].Value = MultiRead();

            if (fixReg == 0x08) { iq[i].GainX = fixVal; iq[i].PhaseY = varVal; }
            else { iq[i].PhaseY = fixVal; iq[i].GainX = varVal; }

            if (i == 0) varVal++;
            else if (i == 1)
            {
                if ((varVal & 0x1f) < 0x02)
                {
                    int tmp = 2 - (varVal & 0x1f);
                    if ((varVal & 0x20) != 0) { varVal &= 0xc0; varVal |= tmp; }
                    else varVal |= 0x20 | tmp;
                }
                else varVal -= 2;
            }
        }
    }

    private void Section(SectType iqPoint)
    {
        var compareIq = SectType.NewArray(3);
        var compareBet = SectType.NewArray(3);

        if ((iqPoint.GainX & 0x1f) == 0)
            compareIq[0].GainX = (iqPoint.GainX & 0xdf) + 1; // Q-path, Gain=1
        else
            compareIq[0].GainX = iqPoint.GainX - 1;          // left point
        compareIq[0].PhaseY = iqPoint.PhaseY;

        IqTree(compareIq, compareIq[0].GainX, compareIq[0].PhaseY, 0x08);
        CompreCor(compareIq);
        compareBet[0].CopyFrom(compareIq[0]);

        compareIq[0].GainX = iqPoint.GainX;
        compareIq[0].PhaseY = iqPoint.PhaseY;
        IqTree(compareIq, compareIq[0].GainX, compareIq[0].PhaseY, 0x08);
        CompreCor(compareIq);
        compareBet[1].CopyFrom(compareIq[0]);

        if ((iqPoint.GainX & 0x1f) == 0x00)
            compareIq[0].GainX = (iqPoint.GainX | 0x20) + 1; // I-path, Gain=1
        else
            compareIq[0].GainX = iqPoint.GainX + 1;
        compareIq[0].PhaseY = iqPoint.PhaseY;
        IqTree(compareIq, compareIq[0].GainX, compareIq[0].PhaseY, 0x08);
        CompreCor(compareIq);
        compareBet[2].CopyFrom(compareIq[0]);
        CompreCor(compareBet);

        iqPoint.CopyFrom(compareBet[0]);
    }

    private SectType Iq()
    {
        VgaAdjust();

        var compareIq = SectType.NewArray(3);
        bool xDirection = ImrCross(compareIq);

        int dirReg, otherReg;
        if (xDirection) { dirReg = 0x08; otherReg = 0x09; }
        else { dirReg = 0x09; otherReg = 0x08; }

        CompreCor(compareIq);
        CompreSep(compareIq, dirReg);
        IqTree(compareIq, compareIq[0].GainX, compareIq[0].PhaseY, dirReg);
        CompreCor(compareIq);
        CompreSep(compareIq, otherReg);
        IqTree(compareIq, compareIq[0].GainX, compareIq[0].PhaseY, otherReg);
        CompreCor(compareIq);
        Section(compareIq[0]);

        WriteRegMask(0x08, 0, 0x3f);
        WriteRegMask(0x09, 0, 0x3f);

        return compareIq[0];
    }

    private void FImr(SectType iqPoint)
    {
        VgaAdjust();
        Section(iqPoint);
    }

    private void SetMux(long freq)
    {
        freq /= 1_000_000L; // MHz

        var range = R820tTunerData.FREQ_RANGES[^1];
        for (int i = 0; i < R820tTunerData.FREQ_RANGES.Length - 1; i++)
        {
            if (freq < R820tTunerData.FREQ_RANGES[i + 1].Freq) { range = R820tTunerData.FREQ_RANGES[i]; break; }
        }

        WriteRegMask(0x17, range.OpenD, 0x08);     // Open Drain
        WriteRegMask(0x1a, range.RfMuxPloy, 0xc3); // RF_MUX, Polymux
        WriteReg(0x1b, range.TfC);                 // TF BAND

        int val;
        switch (_xtalCapValue)
        {
            case XtalCapValue.XTAL_LOW_CAP_30P:
            case XtalCapValue.XTAL_LOW_CAP_20P: val = range.XtalCap20p | 0x08; break;
            case XtalCapValue.XTAL_LOW_CAP_10P: val = range.XtalCap10p | 0x08; break;
            case XtalCapValue.XTAL_HIGH_CAP_0P: val = range.XtalCap0p; break;
            default: val = range.XtalCap0p | 0x08; break; // XTAL_LOW_CAP_0P
        }
        WriteRegMask(0x10, val, 0x0b);

        int reg08, reg09;
        if (_imrDone) { reg08 = _imrData[range.ImrMem].GainX; reg09 = _imrData[range.ImrMem].PhaseY; }
        else { reg08 = 0; reg09 = 0; }
        WriteRegMask(0x08, reg08, 0x3f);
        WriteRegMask(0x09, reg09, 0x3f);
    }

    private void SetPll(long freq)
    {
        freq /= 1_000L; // kHz
        long pllRef = _xtal / 1_000L;

        WriteRegMask(0x10, 0x00, 0x10); // refdiv2 disabled
        WriteRegMask(0x1a, 0x00, 0x0c); // pll autotune = 128kHz
        WriteRegMask(0x12, 0x80, 0xe0); // VCO current = 100

        int mixDiv = 2;
        int divNum = 0;
        long vcoMin = 1_770_000L;
        long vcoMax = vcoMin * 2;

        while (mixDiv <= 64)
        {
            if ((freq * mixDiv) >= vcoMin && (freq * mixDiv) < vcoMax)
            {
                int divBuf = mixDiv;
                while (divBuf > 2) { divBuf >>= 1; divNum++; }
                break;
            }
            mixDiv <<= 1;
        }

        var data = new byte[5];
        Read(0x00, data);

        int vcoFineTune = (data[4] & 0x30) >> 4;

        if (_chip != RafaelChip.CHIP_R828D)
        {
            if (vcoFineTune > VCO_POWER_REF) divNum--;
            else if (vcoFineTune < VCO_POWER_REF) divNum++;
        }
        WriteRegMask(0x10, divNum << 5, 0xe0);

        long vcoFreq = freq * mixDiv;
        long nint = vcoFreq / (2 * pllRef);
        long vcoFra = vcoFreq - 2 * pllRef * nint;

        // boundary spur prevention
        if (vcoFra < pllRef / 64) vcoFra = 0;
        else if (vcoFra > pllRef * 127 / 64) { vcoFra = 0; nint++; }
        else if (vcoFra > pllRef * 127 / 128 && vcoFra < pllRef) vcoFra = pllRef * 127 / 128;
        else if (vcoFra > pllRef && vcoFra < pllRef * 129 / 128) vcoFra = pllRef * 129 / 128;

        long ni = (nint - 13) / 4;
        long si = nint - 4 * ni - 13;

        WriteReg(0x14, (int)(ni + (si << 6)));

        int pwSdm = vcoFra == 0 ? 0x08 : 0x00;
        WriteRegMask(0x12, pwSdm, 0x08);

        int nSdm = 2;
        int sdm = 0;
        while (vcoFra > 1)
        {
            if (vcoFra > (2 * pllRef / nSdm))
            {
                sdm += 32768 / (nSdm / 2);
                vcoFra -= 2 * pllRef / nSdm;
                if (nSdm >= 0x8000) break;
            }
            nSdm <<= 1;
        }
        WriteReg(0x16, sdm >> 8);
        WriteReg(0x15, sdm & 0xff);

        for (int i = 0; i < 2; i++)
        {
            SleepUtils.Usleep(10_000L);
            Read(0x00, data, 3);
            if ((data[2] & 0x40) != 0) break;
            if (i == 0) WriteRegMask(0x12, 0x60, 0xe0); // didn't lock, increase VCO current
        }

        _hasLock = (data[2] & 0x40) != 0;
        if (_hasLock) WriteRegMask(0x1a, 0x08, 0x08);
    }

    private void Imr(int imrMem, bool imFlag)
    {
        long ringRef = _xtal > 24_000_000L ? _xtal / 2_000L : _xtal / 1_000L;
        int nRing = 15;
        for (int n = 0; n < 16; n++)
        {
            if ((16L + n) * 8L * ringRef >= 3_100_000L) { nRing = n; break; }
        }

        int reg18 = ReadCacheReg(0x18);
        int reg19 = ReadCacheReg(0x19);
        int reg1f = ReadCacheReg(0x1f);

        reg18 &= 0xf0;
        reg18 |= nRing;

        long ringVco = (16L + nRing) * 8L * ringRef;

        reg18 &= 0xdf;
        reg19 &= 0xfc;
        reg1f &= 0xfc;

        long ringFreq;
        switch (imrMem)
        {
            case 0: ringFreq = ringVco / 48; reg18 |= 0x20; reg19 |= 0x03; reg1f |= 0x02; break;
            case 1: ringFreq = ringVco / 16; reg18 |= 0x00; reg19 |= 0x02; reg1f |= 0x00; break;
            case 2: ringFreq = ringVco / 8;  reg18 |= 0x00; reg19 |= 0x01; reg1f |= 0x03; break;
            case 3: ringFreq = ringVco / 6;  reg18 |= 0x20; reg19 |= 0x00; reg1f |= 0x03; break;
            default: ringFreq = ringVco / 4; reg18 |= 0x00; reg19 |= 0x00; reg1f |= 0x01; break;
        }

        WriteReg(0x18, reg18);
        WriteReg(0x19, reg19);
        WriteReg(0x1f, reg1f);

        SetMux((ringFreq - 5_300L) * 1_000L);
        SetPll((ringFreq - 5_300L) * 1_000L);
        if (!_hasLock) throw new RtlUsbException("cannot calibrate tuner");

        SectType imrPoint;
        if (imFlag) imrPoint = Iq();
        else { imrPoint = new SectType(); imrPoint.CopyFrom(_imrData[3]); FImr(imrPoint); }

        _imrData[imrMem >= NUM_IMR ? NUM_IMR - 1 : imrMem].CopyFrom(imrPoint);
    }

    private XtalCapValue XtalCheck()
    {
        InitRegs();
        WriteRegMask(0x10, 0x0b, 0x0b); // cap 30pF & Drive Low
        WriteRegMask(0x1a, 0x00, 0x0c); // pll autotune = 128kHz
        WriteRegMask(0x13, 0x7f, 0x7f); // manual initial reg = 111111
        WriteRegMask(0x13, 0x00, 0x40); // set auto

        var data = new byte[3];
        foreach (var xtalCap in R820tTunerData.XTAL_CAPS)
        {
            WriteRegMask(0x10, xtalCap.Cap, 0x1b);
            SleepUtils.Usleep(6_000L);
            Read(0x00, data);
            if ((data[2] & 0x40) == 0) continue;

            int val = data[2] & 0x3f;
            if (_xtal == 16_000_000L && (val > 29 || val < 23)) return xtalCap.Value;
            if (val != 0x3f) return xtalCap.Value;
        }

        throw new RtlUsbException("cannot calibrate tuner (xtal check)");
    }

    private void ImrCalibrate()
    {
        if (_initDone) return;

        if (_chip is RafaelChip.CHIP_R820T or RafaelChip.CHIP_R828S or RafaelChip.CHIP_R820C)
            _xtalCapValue = XtalCapValue.XTAL_HIGH_CAP_0P;
        else
        {
            for (int i = 0; i < 3; i++)
            {
                XtalCapValue detectedCap = XtalCheck();
                if (i == 0 || (int)detectedCap > (int)_xtalCapValue) _xtalCapValue = detectedCap;
            }
        }
        InitRegs();

        ImrPrepare();
        Imr(3, true);
        Imr(1, false);
        Imr(0, false);
        Imr(2, false);
        Imr(4, false);

        _imrDone = true;
        _initDone = true;
    }

    private void Standby()
    {
        if (!_initDone) return;
        WriteReg(0x06, 0xb1);
        WriteReg(0x05, 0x03);
        WriteReg(0x07, 0x3a);
        WriteReg(0x08, 0x40);
        WriteReg(0x09, 0xc0);
        WriteReg(0x0a, 0x36);
        WriteReg(0x0c, 0x35);
        WriteReg(0x0f, 0x68);
        WriteReg(0x11, 0x03);
        WriteReg(0x17, 0xf4);
        WriteReg(0x19, 0x0c);
    }

    private void SetTvStandard(long bw)
    {
        long ifKhz, filtCalLo;
        int filtGain, imgR, filtQ, hpCor, extEnable, loopThrough, ltAtt, fltExtWidest, polyfilCur;

        if (bw <= 6)
        {
            ifKhz = 3570; filtCalLo = 56000; filtGain = 0x10; imgR = 0x00; filtQ = 0x10;
            hpCor = 0x6b; extEnable = 0x60; loopThrough = 0x00; ltAtt = 0x00; fltExtWidest = 0x00; polyfilCur = 0x60;
        }
        else if (bw == 7)
        {
            ifKhz = 4570; filtCalLo = 63000; filtGain = 0x10; imgR = 0x00; filtQ = 0x10;
            hpCor = 0x2a; extEnable = 0x60; loopThrough = 0x00; ltAtt = 0x00; fltExtWidest = 0x00; polyfilCur = 0x60;
        }
        else
        {
            ifKhz = 4570; filtCalLo = 68500; filtGain = 0x10; imgR = 0x00; filtQ = 0x10;
            hpCor = 0x0b; extEnable = 0x60; loopThrough = 0x00; ltAtt = 0x00; fltExtWidest = 0x00; polyfilCur = 0x60;
        }

        Array.Copy(R820tTunerData.INIT_REGS, 0, _regs, 0, R820tTunerData.INIT_REGS.Length);

        WriteRegMask(0x0c, _imrDone ? 1 | ((int)_xtalCapValue << 1) : 0, 0x0f); // init flag & xtal-check result
        WriteRegMask(0x13, VER_NUM, 0x3f);  // version
        WriteRegMask(0x1d, 0x00, 0x38);     // LT gain test
        SleepUtils.Usleep(1_000);
        _intFreq = ifKhz * 1_000;

        bool needCalibration = bw != _mBw;
        if (needCalibration)
        {
            var data = new byte[5];
            for (int i = 0; i < 2; i++)
            {
                WriteRegMask(0x0b, hpCor, 0x60);  // filt_cap
                WriteRegMask(0x0f, 0x04, 0x04);   // cali clk = on
                WriteRegMask(0x10, 0x00, 0x03);   // xtal cap 0pF for PLL
                SetPll(filtCalLo * 1_000L);
                if (!_hasLock) throw new RtlUsbException($"cannot tune to {filtCalLo / 1000} for filter cal");

                WriteRegMask(0x0b, 0x10, 0x10);   // start trigger
                SleepUtils.Usleep(1_000L);
                WriteRegMask(0x0b, 0x00, 0x10);   // stop trigger
                WriteRegMask(0x0f, 0x00, 0x04);   // cali clk = off
                Read(0x00, data);

                _filCalCode = data[4] & 0x0f;
                if (_filCalCode != 0 && _filCalCode != 0x0f) break;
            }
            if (_filCalCode == 0x0f) _filCalCode = 0;
        }

        WriteRegMask(0x0a, filtQ | _filCalCode, 0x1f);
        WriteRegMask(0x0b, hpCor, 0xef);       // BW, filter gain, HP corner
        WriteRegMask(0x07, imgR, 0x80);        // Img_R
        WriteRegMask(0x06, filtGain, 0x30);    // filt_3dB, V6MHz
        WriteRegMask(0x1e, extEnable, 0x60);   // channel filter extension
        WriteRegMask(0x05, loopThrough, 0x80); // loop through
        WriteRegMask(0x1f, ltAtt, 0x80);       // loop through attenuation
        WriteRegMask(0x0f, fltExtWidest, 0x80);// filter extension widest
        WriteRegMask(0x19, polyfilCur, 0x60);  // RF poly filter current

        _mBw = bw;
    }

    private void SysFreqSel(long freq, DeliverySystem deliverySystem)
    {
        int mixerTop, lnaTop, cpCur, divBufCur, lnaVthL, mixerVthL, airCable1In, cable2In, lnaDischarge, filterCur;

        switch (deliverySystem)
        {
            case DeliverySystem.DVBT:
                if (freq == 506000000L || freq == 666000000L || freq == 818000000L)
                { mixerTop = 0x14; lnaTop = 0xe5; cpCur = 0x28; divBufCur = 0x20; }
                else
                { mixerTop = 0x24; lnaTop = 0xe5; cpCur = 0x38; divBufCur = 0x30; }
                lnaVthL = 0x53; mixerVthL = 0x75; airCable1In = 0x00; cable2In = 0x00;
                lnaDischarge = 14; filterCur = 0x40;
                break;
            case DeliverySystem.DVBT2:
                mixerTop = 0x24; lnaTop = 0xe5; lnaVthL = 0x53; mixerVthL = 0x75; airCable1In = 0x00;
                cable2In = 0x00; lnaDischarge = 14; cpCur = 0x38; divBufCur = 0x30; filterCur = 0x40;
                break;
            case DeliverySystem.DVBC:
                mixerTop = 0x24; lnaTop = 0xe5; lnaVthL = 0x62; mixerVthL = 0x75; airCable1In = 0x60;
                cable2In = 0x00; lnaDischarge = 14; cpCur = 0x38; divBufCur = 0x30; filterCur = 0x40;
                break;
            default: throw new RtlUsbException("unsupported delivery system");
        }

        WriteRegMask(0x1d, lnaTop, 0xc7);
        WriteRegMask(0x1c, mixerTop, 0xf8);
        WriteReg(0x0d, lnaVthL);
        WriteReg(0x0e, mixerVthL);

        WriteRegMask(0x05, airCable1In, 0x60);
        WriteRegMask(0x06, cable2In, 0x08);

        WriteRegMask(0x11, cpCur, 0x38);
        WriteRegMask(0x17, divBufCur, 0x30);
        WriteRegMask(0x0a, filterCur, 0x60);

        // Set LNA
        WriteRegMask(0x1d, 0, 0x38);    // LNA TOP: lowest
        WriteRegMask(0x1c, 0, 0x04);    // 0: normal mode
        WriteRegMask(0x06, 0, 0x40);    // 0: PRE_DECT off
        WriteRegMask(0x1a, 0x30, 0x30); // agc clk 250hz

        SleepUtils.Mdelay(250);

        WriteRegMask(0x1d, 0x18, 0x38);     // LNA TOP = 3
        WriteRegMask(0x1c, mixerTop, 0x04); // discharge mode
        WriteRegMask(0x1e, lnaDischarge, 0x1f);
        WriteRegMask(0x1a, 0x20, 0x30);     // agc clk 60hz
    }

    private void GenericSetFreq(long freq, long bw, DeliverySystem deliverySystem)
    {
        SetTvStandard(bw);
        long loFreq = freq + _intFreq;
        SetMux(loFreq);
        SetPll(loFreq);
        if (!_hasLock) throw new RtlUsbException($"cannot tune to {freq / 1_000_000} MHz");
        SysFreqSel(freq, deliverySystem);
    }

    // ---- API ----

    public void Init()
    {
        if (_initDone) return;
        _gate.RunInOpenGate(() => { ImrCalibrate(); InitRegs(); });
    }

    public void SetParams(long frequency, long bandwidthHz, DeliverySystem deliverySystem)
    {
        _gate.RunInOpenGate(() =>
        {
            long bw = (bandwidthHz + 500_000L) / 1_000_000L;
            if (bw == 0) bw = 8;
            GenericSetFreq(frequency, bw, deliverySystem);
        });
    }

    public void Attach()
    {
        _gate.RunInOpenGate(() => { var data = new byte[5]; Read(0x00, data); Standby(); });
    }

    public void Release()
    {
        try { _gate.RunInOpenGate(Standby); } catch { }
    }

    public long GetIfFrequency() => _intFreq;

    public int ReadRfStrengthPercentage() => throw new NotSupportedException();

    // ---- helper ----

    private sealed class SectType
    {
        public int PhaseY;
        public int GainX;
        public int Value;

        public void CopyFrom(SectType src) { PhaseY = src.PhaseY; GainX = src.GainX; Value = src.Value; }

        public static SectType[] NewArray(int elements)
        {
            var array = new SectType[elements];
            for (int i = 0; i < array.Length; i++) array[i] = new SectType();
            return array;
        }
    }
}
