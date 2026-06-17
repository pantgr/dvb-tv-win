using static RtlDvb.Rtl28xxConst;

namespace RtlDvb;

public sealed class RtlUsbException(string message) : Exception(message);

/// <summary>
/// RTL2832U USB device: control-message register access (ctrlMsg/wrReg/rdReg),
/// power control, I2C-gated tuner detection.
/// Port of AndroidDvbDriver Rtl28xxDvbDevice + Rtl2832DvbDevice (R820T path).
///
/// Milestone 1: open device, power on, detect tuner (no full I2C adapter / frontend yet).
/// </summary>
public sealed class RtlDevice : IDisposable
{
    public const ushort VID = 0x0BDA;
    public const ushort PID = 0x2838;
    public const byte BULK_EP = 0x81;
    private const uint TIMEOUT_MS = 1000;

    private IntPtr _ctx;
    private IntPtr _dev;
    private bool _claimed;

    public string TunerName { get; private set; } = "unknown";

    internal I2cAdapter Adapter { get; }
    internal I2GateControl Gate { get; }

    private Rtl2832Frontend? _frontend;
    private R820tTuner? _tuner;

    public RtlDevice()
    {
        Adapter = new Rtl28xxI2cAdapter(this);
        Gate = new RtlGate(this);
    }

    /// <summary>Full bring-up after Open(): power, detect, attach demod+tuner, IMR calibrate, init EP.
    /// Port of DvbUsbDevice.open() body (R820T path).</summary>
    public void Initialize()
    {
        PowerOn();
        ReadConfig();
        if (TunerName != "R820T")
            throw new RtlUsbException($"expected R820T, detected {TunerName}");

        _frontend = new Rtl2832Frontend(Adapter);
        _frontend.Attach();

        _tuner = new R820tTuner(0x1a, Adapter, RafaelChip.CHIP_R820T, 28_800_000L, Gate);
        _tuner.Attach();

        _frontend.Init(_tuner);   // also runs tuner.Init() → IMR calibration
        InitEndpoints();
    }

    public void Tune(long frequencyHz, long bandwidthHz)
    {
        if (_frontend == null) throw new RtlUsbException("not initialized");
        _frontend.SetParams(frequencyHz, bandwidthHz, DeliverySystem.DVBT);
    }

    public ISet<DvbStatus> GetStatus()
        => _frontend?.GetStatus() ?? throw new RtlUsbException("not initialized");

    public int ReadSnr() => _frontend?.ReadSnr() ?? throw new RtlUsbException("not initialized");
    public int ReadRfStrength() => _frontend?.ReadRfStrengthPercentage() ?? 0;
    public int ReadBer() => _frontend?.ReadBer() ?? 0;

    public void DisablePidFilter() => _frontend?.DisablePidFilter();

    /// <summary>Read MPEG-TS from the bulk endpoint. Returns bytes read (0 on timeout).</summary>
    public int ReadBulk(byte[] buf, int timeoutMs)
    {
        int rc = LibUsb.libusb_bulk_transfer(_dev, BULK_EP, buf, buf.Length, out int got, (uint)timeoutMs);
        if (rc == LIBUSB_ERROR_TIMEOUT) return got; // partial data on timeout is fine
        if (rc < 0) throw new RtlUsbException($"bulk_transfer failed: {LibUsb.ErrName(rc)}");
        return got;
    }

    private const int LIBUSB_ERROR_TIMEOUT = -7;

    public void Open()
    {
        if (LibUsb.libusb_init(out _ctx) < 0)
            throw new RtlUsbException("libusb_init failed");

        _dev = LibUsb.libusb_open_device_with_vid_pid(_ctx, VID, PID);
        if (_dev == IntPtr.Zero)
            throw new RtlUsbException($"device {VID:X4}:{PID:X4} not found / not opened (WinUSB driver bound? in use by another app?)");

        // Windows/WinUSB: no kernel driver to detach, but harmless to try on libusb backends that support it.
        try
        {
            if (LibUsb.libusb_kernel_driver_active(_dev, 0) == 1)
                LibUsb.libusb_detach_kernel_driver(_dev, 0);
        }
        catch { /* not supported on WinUSB backend */ }

        int rc = LibUsb.libusb_claim_interface(_dev, 0);
        if (rc < 0)
            throw new RtlUsbException($"claim_interface(0) failed: {LibUsb.ErrName(rc)}");
        _claimed = true;
    }

    // ---- control-message register access (port of Rtl28xxDvbDevice) ----

    /// <param name="value">wValue — register address or I2C-encoded (addr&lt;&lt;1)|reg</param>
    /// <param name="index">wIndex — one of CMD_*_RD/WR (selects block + R/W direction)</param>
    public void CtrlMsg(int value, int index, byte[] data) => CtrlMsg(value, index, data, data.Length);

    /// <param name="len">bytes to transfer from/into the start of <paramref name="data"/></param>
    public void CtrlMsg(int value, int index, byte[] data, int len)
    {
        // Register/I2C transfers are small (<=27 bytes); libusb moves the whole buffer
        // in a single control transfer (no offset chunking needed, unlike the Android API).
        byte requestType = (index & CMD_WR_FLAG) != 0 ? LibUsb.VENDOR_OUT : LibUsb.VENDOR_IN;
        int n = LibUsb.libusb_control_transfer(_dev, requestType, 0,
            (ushort)value, (ushort)index, data, (ushort)len, TIMEOUT_MS);
        if (n < 0)
            throw new RtlUsbException($"control_transfer(val=0x{value:X4},idx=0x{index:X4}) failed: {LibUsb.ErrName(n)}");
    }

    public void WrReg(int reg, byte[] val)
    {
        int index = reg < 0x3000 ? CMD_USB_WR : reg < 0x4000 ? CMD_SYS_WR : CMD_IR_WR;
        CtrlMsg(reg, index, val);
    }

    public void WrReg(int reg, int oneByte) => WrReg(reg, [(byte)oneByte]);

    public void WrReg(int reg, int val, int mask)
    {
        if (mask != 0xff)
        {
            int tmp = RdReg(reg);
            val &= mask;
            tmp &= ~mask;
            val |= tmp;
        }
        WrReg(reg, val & 0xff);
    }

    private void RdRegInto(int reg, byte[] val)
    {
        int index = reg < 0x3000 ? CMD_USB_RD : reg < 0x4000 ? CMD_SYS_RD : CMD_IR_RD;
        CtrlMsg(reg, index, val);
    }

    public int RdReg(int reg)
    {
        var result = new byte[1];
        RdRegInto(reg, result);
        return result[0] & 0xff;
    }

    // ---- power control (Rtl2832DvbDevice.powerControl) ----

    public void PowerOn()
    {
        WrReg(SYS_GPIO_OUT_VAL, 0x08, 0x18); // GPIO3=1, GPIO4=0
        WrReg(SYS_DEMOD_CTL1, 0x00, 0x10);   // suspend?
        WrReg(SYS_DEMOD_CTL, 0x80, 0x80);    // enable PLL
        WrReg(SYS_DEMOD_CTL, 0x20, 0x20);    // disable reset
        WrReg(USB_EPA_CTL, [0x00, 0x00]);    // streaming EP: clear stall & reset
        WrReg(SYS_DEMOD_CTL, 0x48, 0x48);    // enable ADC
    }

    public void PowerOff()
    {
        WrReg(SYS_GPIO_OUT_VAL, 0x10, 0x10);
        WrReg(SYS_DEMOD_CTL, 0x00, 0x80);
        WrReg(USB_EPA_CTL, [0x10, 0x02]);
        WrReg(SYS_DEMOD_CTL, 0x00, 0x48);
    }

    // ---- USB endpoint init (Rtl28xxDvbDevice.init), call after frontend+tuner attach ----

    public void InitEndpoints()
    {
        int val = RdReg(USB_SYSCTL_0);
        val |= 0x09; // enable DMA and Full Packet Mode
        WrReg(USB_SYSCTL_0, val);
        WrReg(USB_EPA_MAXPKT, [0x00, 0x02, 0x00, 0x00]); // EPA max packet size 0x0200
        WrReg(USB_EPA_FIFO_CFG, [0x14, 0x00, 0x00, 0x00]);
    }

    // ---- I2C demod gate (Rtl2832DvbDevice.i2GateController) ----

    public void I2cGate(bool open) => CtrlMsg(0x0120, 0x0011, [(byte)(open ? 0x18 : 0x10)]);

    private sealed class RtlGate(RtlDevice d) : I2GateControl
    {
        private bool _state;
        protected override void I2cGateCtrl(bool enable)
        {
            if (_state == enable) return;
            d.I2cGate(enable);
            _state = enable;
        }
    }

    // ---- tuner detection (Rtl2832DvbDevice.readConfig + Rtl28xxTunerType) ----

    public void ReadConfig()
    {
        // enable GPIO3 and GPIO6 as output
        WrReg(SYS_GPIO_DIR, 0x00, 0x40);
        WrReg(SYS_GPIO_OUT_EN, 0x48, 0x48);

        I2cGate(true);
        try
        {
            TunerName = DetectTuner();
        }
        finally { I2cGate(false); }
    }

    private string DetectTuner()
    {
        // Each probe: raw I2C read (value = (addr<<1)|reg), expect known chip id.
        if (ProbeI2c(0x02c8, 0x40)) return "E4000";
        if (ProbeI2c(0x00c6, 0xa1)) return "FC0012";
        if (ProbeI2c(0x00c6, 0xa3)) return "FC0013";
        if (ProbeI2c(0x0034, 0x69)) return "R820T";   // addr 0x1a, reg 0x00 => 0x69
        if (ProbeI2c(0x0074, 0x69)) return "R828D";   // addr 0x3a, reg 0x00 => 0x69
        throw new RtlUsbException("unrecognized tuner on device");
    }

    private bool ProbeI2c(int value, int expected)
    {
        try
        {
            var data = new byte[1];
            CtrlMsg(value, CMD_I2C_RD, data);
            return (data[0] & 0xff) == expected;
        }
        catch (RtlUsbException)
        {
            return false; // wrong tuner → NACK → control transfer errors, that's expected
        }
    }

    // ---- I2C adapter (Rtl28xxDvbDevice.Rtl28xxI2cAdapter) — 3 access methods ----

    private sealed class Rtl28xxI2cAdapter(RtlDevice d) : I2cAdapter
    {
        protected override int MasterXfer(I2cMessage[] msg)
        {
            // It is not known which are real I2C bus xfer limits, but testing with
            // RTL2831U + MT2060 gives max RD 24 and max WR 22 bytes.
            if (msg.Length == 2 && (msg[0].Flags & I2cMessage.I2C_M_RD) == 0 &&
                (msg[1].Flags & I2cMessage.I2C_M_RD) != 0)
            {
                if (msg[0].Len > 24 || msg[1].Len > 24)
                    throw new RtlUsbException("unsupported I2C operation");
                if (msg[0].Addr == 0x10)
                    // method 1 - integrated demod
                    d.CtrlMsg(((msg[0].Buf[0] & 0xff) << 8) | (msg[0].Addr << 1), Page, msg[1].Buf, msg[1].Len);
                else if (msg[0].Len < 2)
                    // method 2 - old I2C
                    d.CtrlMsg(((msg[0].Buf[0] & 0xff) << 8) | (msg[0].Addr << 1), CMD_I2C_RD, msg[1].Buf, msg[1].Len);
                else
                {
                    // method 3 - new I2C
                    d.CtrlMsg(msg[0].Addr << 1, CMD_I2C_DA_WR, msg[0].Buf, msg[0].Len);
                    d.CtrlMsg(msg[0].Addr << 1, CMD_I2C_DA_RD, msg[1].Buf, msg[1].Len);
                }
            }
            else if (msg.Length == 1 && (msg[0].Flags & I2cMessage.I2C_M_RD) == 0)
            {
                if (msg[0].Len > 22)
                    throw new RtlUsbException("unsupported I2C operation");
                if (msg[0].Addr == 0x10)
                {
                    // method 1 - integrated demod
                    if (msg[0].Buf[0] == 0x00)
                        Page = msg[0].Buf[1] & 0xff; // save demod page for later demod access
                    else
                    {
                        var newdata = new byte[msg[0].Len - 1];
                        Array.Copy(msg[0].Buf, 1, newdata, 0, newdata.Length);
                        d.CtrlMsg(((msg[0].Buf[0] & 0xff) << 8) | (msg[0].Addr << 1), CMD_DEMOD_WR | Page, newdata);
                    }
                }
                else if (msg[0].Len < 23)
                {
                    // method 2 - old I2C
                    var newdata = new byte[msg[0].Len - 1];
                    Array.Copy(msg[0].Buf, 1, newdata, 0, newdata.Length);
                    d.CtrlMsg(((msg[0].Buf[0] & 0xff) << 8) | (msg[0].Addr << 1), CMD_I2C_WR, newdata);
                }
                else
                    // method 3 - new I2C
                    d.CtrlMsg(msg[0].Addr << 1, CMD_I2C_DA_WR, msg[0].Buf, msg[0].Len);
            }
            else
                throw new RtlUsbException("unsupported I2C operation");

            return msg.Length;
        }
    }

    public void Dispose()
    {
        try { if (_claimed) PowerOff(); } catch { }
        if (_claimed) { LibUsb.libusb_release_interface(_dev, 0); _claimed = false; }
        if (_dev != IntPtr.Zero) { LibUsb.libusb_close(_dev); _dev = IntPtr.Zero; }
        if (_ctx != IntPtr.Zero) { LibUsb.libusb_exit(_ctx); _ctx = IntPtr.Zero; }
    }
}
