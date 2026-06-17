using static RtlDvb.Rtl2832FrontendData;

namespace RtlDvb;

/// <summary>
/// RTL2832U built-in DVB-T demodulator. Port of Rtl2832Frontend.java (R820T path only).
/// This is the hardware demod that turns RF into MPEG-TS — no software OFDM.
/// </summary>
internal sealed class Rtl2832Frontend(I2cAdapter i2c) : IDvbFrontend
{
    private const int I2C_ADDRESS = 0x10;
    private const long XTAL = 28_800_000L;

    private readonly I2cAdapter _i2c = i2c;
    private IDvbTuner? _tuner;

    public DvbCapabilities GetCapabilities() => CAPABILITIES;

    // ---- demod register IO (page-managed) ----

    private void Wr(int reg, byte[] val) => Wr(reg, val, val.Length);

    private void Wr(int reg, byte[] val, int length)
    {
        var buf = new byte[length + 1];
        buf[0] = (byte)reg;
        Array.Copy(val, 0, buf, 1, length);
        _i2c.Transfer(I2C_ADDRESS, 0, buf);
    }

    private void Wr(int reg, int page, byte[] val) => Wr(reg, page, val, val.Length);
    private void Wr(int reg, int page, int val) => Wr(reg, page, [(byte)val]);

    private void Wr(int reg, int page, byte[] val, int length)
    {
        if (page != _i2c.Page)
        {
            Wr(0x00, [(byte)page]);
            _i2c.Page = page;
        }
        Wr(reg, val, length);
    }

    private void WrMask(int reg, int page, int mask, int val)
    {
        int orig = Rd(reg, page);
        int tmp = (orig & ~mask) | (val & mask);
        Wr(reg, page, [(byte)tmp]);
    }

    private static int CalcRegMask(int val) => (1 << (val + 1)) - 1;

    private void WrDemodReg(RegBit reg, long val)
    {
        int len = (reg.Msb >> 3) + 1;
        var reading = new byte[len];
        var writing = new byte[len];
        int mask = CalcRegMask(reg.Msb - reg.Lsb);

        Rd(reg.StartAddress, reg.Page, reading);

        int readingTmp = 0;
        for (int i = 0; i < len; i++) readingTmp |= (reading[i] & 0xff) << ((len - 1 - i) * 8);

        int writingTmp = readingTmp & ~(mask << reg.Lsb);
        writingTmp |= (int)((val & mask) << reg.Lsb);

        for (int i = 0; i < len; i++) writing[i] = (byte)(writingTmp >> ((len - 1 - i) * 8));

        Wr(reg.StartAddress, reg.Page, writing);
    }

    private void WrDemodRegs(RegValue[] values)
    {
        foreach (var rv in values) WrDemodReg(rv.Reg, rv.Val);
    }

    private void Rd(int reg, byte[] val)
        => _i2c.Transfer(I2C_ADDRESS, 0, [(byte)reg], I2C_ADDRESS, I2cMessage.I2C_M_RD, val);

    private void Rd(int reg, int page, byte[] val)
    {
        if (page != _i2c.Page)
        {
            Wr(0x00, [(byte)page]);
            _i2c.Page = page;
        }
        Rd(reg, val);
    }

    private int Rd(int reg, int page)
    {
        var result = new byte[1];
        Rd(reg, page, result);
        return result[0] & 0xff;
    }

    private long RdDemodReg(RegBit reg)
    {
        int len = (reg.Msb >> 3) + 1;
        var reading = new byte[len];
        int mask = CalcRegMask(reg.Msb - reg.Lsb);

        Rd(reg.StartAddress, reg.Page, reading);

        long readingTmp = 0;
        for (int i = 0; i < len; i++) readingTmp |= ((long)(reading[i] & 0xff)) << ((len - 1 - i) * 8);

        return (readingTmp >> reg.Lsb) & mask;
    }

    private void SetIf(long ifFreq)
    {
        int enBbin = ifFreq == 0 ? 0x1 : 0x0;

        // PSET_IFFREQ = - floor((IfFreq % XTAL) * 2^22 / XTAL)
        long psetIffreq = ifFreq % XTAL;
        psetIffreq *= 0x400000;
        psetIffreq = DvbMath.DivU64(psetIffreq, XTAL);
        psetIffreq = -psetIffreq;
        psetIffreq &= 0x3fffff;

        WrDemodReg(DVBT_EN_BBIN, enBbin);
        WrDemodReg(DVBT_PSET_IFFREQ, psetIffreq);
    }

    public void Attach()
    {
        Rd(0, 0); // check the demod is there
        WrDemodReg(DVBT_SOFT_RST, 0x1);
    }

    public void Release()
    {
        try { WrDemodReg(DVBT_SOFT_RST, 0x1); } catch { }
    }

    public void Init(IDvbTuner tuner)
    {
        _tuner = tuner;

        UnsetSdrMode();

        WrDemodReg(DVBT_SOFT_RST, 0x0);
        WrDemodRegs(INITIAL_REGS);
        WrDemodRegs(TUNER_INIT_R820T);

        // r820t NIM software reset at the demod (also done at setParams)
        WrDemodReg(DVBT_SOFT_RST, 0x1);
        WrDemodReg(DVBT_SOFT_RST, 0x0);

        tuner.Init();
    }

    private void UnsetSdrMode()
    {
        Wr(0x61, 0, 0xe0);                         // PID filter
        Wr(0x19, 0, 0x20);                         // mode
        Wr(0x17, 0, [0x11, 0x10]);
        Wr(0x92, 1, [0x00, 0x0f, 0xff]);           // FSM
        Wr(0x3e, 1, [0x40, 0x00]);
        Wr(0x15, 1, [0x06, 0x3f, 0xce, 0xcc]);
    }

    public void SetParams(long frequency, long bandwidthHz, DeliverySystem deliverySystem)
    {
        if (deliverySystem != DeliverySystem.DVBT)
            throw new RtlUsbException("unsupported delivery system (DVB-T only)");
        if (_tuner == null) throw new RtlUsbException("frontend not initialized");

        _tuner.SetParams(frequency, bandwidthHz, deliverySystem);
        SetIf(_tuner.GetIfFrequency());

        int i;
        long bwMode;
        switch ((int)bandwidthHz)
        {
            case 6000000: i = 0; bwMode = 48000000; break;
            case 7000000: i = 1; bwMode = 56000000; break;
            case 8000000: i = 2; bwMode = 64000000; break;
            default: throw new RtlUsbException("invalid bandwidth");
        }

        var byteToSend = new byte[1];
        for (int j = 0; j < BW_PARAMS[0].Length; j++)
        {
            byteToSend[0] = BW_PARAMS[i][j];
            Wr(0x1c + j, 1, byteToSend);
        }

        // RSAMP_RATIO = floor(XTAL * 7 * 2^22 / bwMode)
        long num = XTAL * 7;
        num *= 0x400000L;
        num = DvbMath.DivU64(num, bwMode);
        long resampRatio = num & 0x3ffffff;
        WrDemodReg(DVBT_RSAMP_RATIO, resampRatio);

        // CFREQ_OFF_RATIO = - floor(bwMode * 2^20 / (XTAL * 7))
        num = bwMode << 20;
        long num2 = XTAL * 7;
        num = DvbMath.DivU64(num, num2);
        num = -num;
        long cfreqOffRatio = num & 0xfffff;
        WrDemodReg(DVBT_CFREQ_OFF_RATIO, cfreqOffRatio);

        WrDemodReg(DVBT_SOFT_RST, 0x1);
        WrDemodReg(DVBT_SOFT_RST, 0x0);
    }

    public int ReadSnr()
    {
        // SNR in 0.1 dB resolution
        int tmp = Rd(0x3c, 3);
        int constellation = (tmp >> 2) & 0x03;
        if (constellation >= CONSTELLATION_NUM) throw new RtlUsbException("cannot read SNR");
        int hierarchy = (tmp >> 4) & 0x07;
        if (hierarchy >= HIERARCHY_NUM) throw new RtlUsbException("cannot read SNR");

        var buf = new byte[2];
        Rd(0x0c, 4, buf);

        int tmp16 = (buf[0] & 0xff) << 8 | (buf[1] & 0xff);
        if (tmp16 == 0) return 0;
        return (SNR_CONSTANTS[constellation][hierarchy] - DvbMath.Intlog10(tmp16)) / ((1 << 24) / 100);
    }

    public int ReadRfStrengthPercentage()
    {
        long tmp = RdDemodReg(DVBT_FSM_STAGE);
        if (tmp == 10 || tmp == 11)
        {
            int u8tmp = Rd(0x05, 3);
            u8tmp = (~u8tmp) & 0xff;
            int strength = u8tmp << 8 | u8tmp;
            return (100 * strength) / 0xffff;
        }
        return 0;
    }

    public int ReadBer()
    {
        var buf = new byte[2];
        Rd(0x4e, 3, buf);
        return (buf[0] & 0xff) << 8 | (buf[1] & 0xff); // bit errors per 1MB
    }

    public ISet<DvbStatus> GetStatus()
    {
        long tmp = RdDemodReg(DVBT_FSM_STAGE);
        if (tmp == 11)
            return new HashSet<DvbStatus> { DvbStatus.FE_HAS_SIGNAL, DvbStatus.FE_HAS_CARRIER,
                DvbStatus.FE_HAS_VITERBI, DvbStatus.FE_HAS_SYNC, DvbStatus.FE_HAS_LOCK };
        if (tmp == 10)
            return new HashSet<DvbStatus> { DvbStatus.FE_HAS_SIGNAL, DvbStatus.FE_HAS_CARRIER,
                DvbStatus.FE_HAS_VITERBI };
        return new HashSet<DvbStatus>();
    }

    public void SetPids(params int[] pids) => SetPids(false, pids);
    public void DisablePidFilter() => DisablePidFilter(false);

    private void SetPids(bool slaveTs, int[] pids)
    {
        if (!HardwareSupportsPidFilterOf(pids)) { DisablePidFilter(slaveTs); return; }

        EnablePidFilter(slaveTs);

        long pidFilter = 0;
        for (int index = 0; index < pids.Length; index++) pidFilter |= 1L << index;

        var buf = new byte[]
        {
            (byte)(pidFilter & 0xff), (byte)((pidFilter >> 8) & 0xff),
            (byte)((pidFilter >> 16) & 0xff), (byte)((pidFilter >> 24) & 0xff)
        };

        if (slaveTs) Wr(0x22, 0, buf); else Wr(0x62, 0, buf);

        for (int index = 0; index < pids.Length; index++)
        {
            int pid = pids[index];
            buf[0] = (byte)((pid >> 8) & 0xff);
            buf[1] = (byte)(pid & 0xff);
            if (slaveTs) Wr(0x26 + 2 * index, 0, buf, 2); else Wr(0x66 + 2 * index, 0, buf, 2);
        }
    }

    private void DisablePidFilter(bool slaveTs)
    {
        if (slaveTs) WrMask(0x21, 0, 0xc0, 0xc0); else WrMask(0x61, 0, 0xc0, 0xc0);
    }

    private void EnablePidFilter(bool slaveTs)
    {
        if (slaveTs) WrMask(0x21, 0, 0xc0, 0x80); else WrMask(0x61, 0, 0xc0, 0x80);
    }

    private static bool HardwareSupportsPidFilterOf(int[] pids)
    {
        if (pids.Length > 32) return false;
        foreach (int p in pids) if (p < 0 || p > 0x1FFF) return false;
        return true;
    }
}
