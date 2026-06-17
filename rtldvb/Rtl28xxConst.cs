namespace RtlDvb;

/// <summary>
/// RTL2832U register addresses + control-message command/index flags.
/// Direct port of AndroidDvbDriver Rtl28xxConst.java (V4L2 rtl28xxu kernel driver).
/// </summary>
internal static class Rtl28xxConst
{
    // Block selectors (high byte of ctrlMsg index)
    public const int DEMOD   = 0x0000;
    public const int USB     = 0x0100;
    public const int SYS     = 0x0200;
    public const int I2C     = 0x0300;
    public const int I2C_DA  = 0x0600;

    // ctrlMsg index command flags
    public const int CMD_WR_FLAG   = 0x0010;
    public const int CMD_DEMOD_RD  = 0x0000;
    public const int CMD_DEMOD_WR  = 0x0010;
    public const int CMD_USB_RD    = 0x0100;
    public const int CMD_USB_WR    = 0x0110;
    public const int CMD_SYS_RD    = 0x0200;
    public const int CMD_IR_RD     = 0x0201;
    public const int CMD_IR_WR     = 0x0211;
    public const int CMD_SYS_WR    = 0x0210;
    public const int CMD_I2C_RD    = 0x0300;
    public const int CMD_I2C_WR    = 0x0310;
    public const int CMD_I2C_DA_RD = 0x0600;
    public const int CMD_I2C_DA_WR = 0x0610;

    // USB registers
    public const int USB_SYSCTL       = 0x2000;
    public const int USB_SYSCTL_0     = 0x2000;
    public const int USB_EPA_CFG      = 0x2144;
    public const int USB_EPA_CTL      = 0x2148;
    public const int USB_EPA_MAXPKT   = 0x2158;
    public const int USB_EPA_FIFO_CFG = 0x2160;

    // SYS / demod control registers
    public const int SYS_DEMOD_CTL    = 0x3000;
    public const int SYS_GPIO_OUT_VAL = 0x3001;
    public const int SYS_GPIO_IN_VAL  = 0x3002;
    public const int SYS_GPIO_OUT_EN  = 0x3003;
    public const int SYS_GPIO_DIR     = 0x3004;
    public const int SYS_DEMOD_CTL1   = 0x300B;
}
