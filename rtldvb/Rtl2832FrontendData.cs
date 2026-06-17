namespace RtlDvb;

/// <summary>A demod bit-field: which page, start address, and msb..lsb bit range.</summary>
internal readonly struct RegBit(int page, int startAddress, int msb, int lsb)
{
    public int Page { get; } = page;
    public int StartAddress { get; } = startAddress;
    public int Msb { get; } = msb;
    public int Lsb { get; } = lsb;
}

internal readonly struct RegValue(RegBit reg, long val)
{
    public RegBit Reg { get; } = reg;
    public long Val { get; } = val;
}

/// <summary>Port of Rtl2832FrontendData.java — only the RegBits + tables used by the R820T path.</summary>
internal static class Rtl2832FrontendData
{
    public static readonly DvbCapabilities CAPABILITIES =
        new(174_000_000L, 862_000_000L, 166_667L, new HashSet<DeliverySystem> { DeliverySystem.DVBT });

    // ---- demod bit-field registers (page, startAddr, msb, lsb) ----
    public static readonly RegBit DVBT_SOFT_RST          = new(0x1, 0x1, 2, 2);
    public static readonly RegBit DVBT_RSD_BER_FAIL_VAL  = new(0x1, 0x8f, 15, 0);
    public static readonly RegBit DVBT_EN_BK_TRK         = new(0x1, 0xa6, 7, 7);
    public static readonly RegBit DVBT_AD_EN_REG         = new(0x0, 0x8, 7, 7);
    public static readonly RegBit DVBT_AD_EN_REG1        = new(0x0, 0x8, 6, 6);
    public static readonly RegBit DVBT_EN_BBIN           = new(0x1, 0xb1, 0, 0);
    public static readonly RegBit DVBT_MGD_THD0          = new(0x1, 0x95, 7, 0);
    public static readonly RegBit DVBT_MGD_THD1          = new(0x1, 0x96, 7, 0);
    public static readonly RegBit DVBT_MGD_THD2          = new(0x1, 0x97, 7, 0);
    public static readonly RegBit DVBT_MGD_THD3          = new(0x1, 0x98, 7, 0);
    public static readonly RegBit DVBT_MGD_THD4          = new(0x1, 0x99, 7, 0);
    public static readonly RegBit DVBT_MGD_THD5          = new(0x1, 0x9a, 7, 0);
    public static readonly RegBit DVBT_MGD_THD6          = new(0x1, 0x9b, 7, 0);
    public static readonly RegBit DVBT_MGD_THD7          = new(0x1, 0x9c, 7, 0);
    public static readonly RegBit DVBT_EN_CACQ_NOTCH     = new(0x1, 0x61, 4, 4);
    public static readonly RegBit DVBT_AD_AV_REF         = new(0x0, 0x9, 6, 0);
    public static readonly RegBit DVBT_REG_PI            = new(0x0, 0xa, 2, 0);
    public static readonly RegBit DVBT_PIP_ON            = new(0x0, 0x21, 3, 3);
    public static readonly RegBit DVBT_SCALE1_B92        = new(0x2, 0x92, 7, 0);
    public static readonly RegBit DVBT_SCALE1_B93        = new(0x2, 0x93, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BA7        = new(0x2, 0xa7, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BA9        = new(0x2, 0xa9, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BAA        = new(0x2, 0xaa, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BAB        = new(0x2, 0xab, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BAC        = new(0x2, 0xac, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BB0        = new(0x2, 0xb0, 7, 0);
    public static readonly RegBit DVBT_SCALE1_BB1        = new(0x2, 0xb1, 7, 0);
    public static readonly RegBit DVBT_KB_P1             = new(0x1, 0x64, 3, 1);
    public static readonly RegBit DVBT_KB_P2             = new(0x1, 0x64, 6, 4);
    public static readonly RegBit DVBT_KB_P3             = new(0x1, 0x65, 2, 0);
    public static readonly RegBit DVBT_K1_CR_STEP12      = new(0x2, 0xad, 9, 4);
    public static readonly RegBit DVBT_TRK_KS_P2         = new(0x1, 0x6f, 2, 0);
    public static readonly RegBit DVBT_TRK_KS_I2         = new(0x1, 0x70, 5, 3);
    public static readonly RegBit DVBT_TR_THD_SET2       = new(0x1, 0x72, 3, 0);
    public static readonly RegBit DVBT_TRK_KC_I2         = new(0x1, 0x75, 2, 0);
    public static readonly RegBit DVBT_CR_THD_SET2       = new(0x1, 0x76, 7, 6);
    public static readonly RegBit DVBT_PSET_IFFREQ       = new(0x1, 0x19, 21, 0);
    public static readonly RegBit DVBT_SPEC_INV          = new(0x1, 0x15, 0, 0);
    public static readonly RegBit DVBT_RSAMP_RATIO       = new(0x1, 0x9f, 27, 2);
    public static readonly RegBit DVBT_CFREQ_OFF_RATIO   = new(0x1, 0x9d, 23, 4);
    public static readonly RegBit DVBT_FSM_STAGE         = new(0x3, 0x51, 6, 3);
    public static readonly RegBit DVBT_DAGC_TRG_VAL      = new(0x1, 0x12, 7, 0);
    public static readonly RegBit DVBT_AGC_TARG_VAL_0    = new(0x1, 0x2, 0, 0);
    public static readonly RegBit DVBT_AGC_TARG_VAL_8_1  = new(0x1, 0x3, 7, 0);
    public static readonly RegBit DVBT_AAGC_LOOP_GAIN    = new(0x1, 0xc7, 5, 1);
    public static readonly RegBit DVBT_LOOP_GAIN2_3_0    = new(0x1, 0x4, 4, 1);
    public static readonly RegBit DVBT_LOOP_GAIN2_4      = new(0x1, 0x5, 7, 7);
    public static readonly RegBit DVBT_LOOP_GAIN3        = new(0x1, 0xc8, 4, 0);
    public static readonly RegBit DVBT_VTOP1             = new(0x1, 0x6, 5, 0);
    public static readonly RegBit DVBT_VTOP2             = new(0x1, 0xc9, 5, 0);
    public static readonly RegBit DVBT_VTOP3             = new(0x1, 0xca, 5, 0);
    public static readonly RegBit DVBT_KRF1              = new(0x1, 0xcb, 7, 0);
    public static readonly RegBit DVBT_KRF2              = new(0x1, 0x7, 7, 0);
    public static readonly RegBit DVBT_KRF3              = new(0x1, 0xcd, 7, 0);
    public static readonly RegBit DVBT_KRF4              = new(0x1, 0xce, 7, 0);
    public static readonly RegBit DVBT_IF_AGC_MIN        = new(0x1, 0x8, 7, 0);
    public static readonly RegBit DVBT_IF_AGC_MAX        = new(0x1, 0x9, 7, 0);
    public static readonly RegBit DVBT_RF_AGC_MIN        = new(0x1, 0xa, 7, 0);
    public static readonly RegBit DVBT_RF_AGC_MAX        = new(0x1, 0xb, 7, 0);
    public static readonly RegBit DVBT_POLAR_RF_AGC      = new(0x0, 0xe, 1, 1);
    public static readonly RegBit DVBT_POLAR_IF_AGC      = new(0x0, 0xe, 0, 0);
    public static readonly RegBit DVBT_AD7_SETTING       = new(0x0, 0x11, 15, 0);
    public static readonly RegBit DVBT_REG_GPE           = new(0x0, 0xd, 7, 7);
    public static readonly RegBit DVBT_SERIAL            = new(0x1, 0x7c, 4, 4);
    public static readonly RegBit DVBT_CDIV_PH0          = new(0x1, 0x7d, 3, 0);
    public static readonly RegBit DVBT_CDIV_PH1          = new(0x1, 0x7d, 7, 4);
    public static readonly RegBit DVBT_MPEG_IO_OPT_2_2   = new(0x0, 0x6, 7, 7);
    public static readonly RegBit DVBT_MPEG_IO_OPT_1_0   = new(0x0, 0x7, 7, 6);

    public static readonly RegValue[] INITIAL_REGS =
    [
        new(DVBT_AD_EN_REG, 0x1), new(DVBT_AD_EN_REG1, 0x1), new(DVBT_RSD_BER_FAIL_VAL, 0x2800),
        new(DVBT_MGD_THD0, 0x10), new(DVBT_MGD_THD1, 0x20), new(DVBT_MGD_THD2, 0x20),
        new(DVBT_MGD_THD3, 0x40), new(DVBT_MGD_THD4, 0x22), new(DVBT_MGD_THD5, 0x32),
        new(DVBT_MGD_THD6, 0x37), new(DVBT_MGD_THD7, 0x39), new(DVBT_EN_BK_TRK, 0x0),
        new(DVBT_EN_CACQ_NOTCH, 0x0), new(DVBT_AD_AV_REF, 0x2a), new(DVBT_REG_PI, 0x6),
        new(DVBT_PIP_ON, 0x0), new(DVBT_CDIV_PH0, 0x8), new(DVBT_CDIV_PH1, 0x8),
        new(DVBT_SCALE1_B92, 0x4), new(DVBT_SCALE1_B93, 0xb0), new(DVBT_SCALE1_BA7, 0x78),
        new(DVBT_SCALE1_BA9, 0x28), new(DVBT_SCALE1_BAA, 0x59), new(DVBT_SCALE1_BAB, 0x83),
        new(DVBT_SCALE1_BAC, 0xd4), new(DVBT_SCALE1_BB0, 0x65), new(DVBT_SCALE1_BB1, 0x43),
        new(DVBT_KB_P1, 0x1), new(DVBT_KB_P2, 0x4), new(DVBT_KB_P3, 0x7),
        new(DVBT_K1_CR_STEP12, 0xa), new(DVBT_REG_GPE, 0x1), new(DVBT_SERIAL, 0x0),
        new(DVBT_CDIV_PH0, 0x9), new(DVBT_CDIV_PH1, 0x9), new(DVBT_MPEG_IO_OPT_2_2, 0x0),
        new(DVBT_MPEG_IO_OPT_1_0, 0x0), new(DVBT_TRK_KS_P2, 0x4), new(DVBT_TRK_KS_I2, 0x7),
        new(DVBT_TR_THD_SET2, 0x6), new(DVBT_TRK_KC_I2, 0x5), new(DVBT_CR_THD_SET2, 0x1)
    ];

    public static readonly RegValue[] TUNER_INIT_R820T =
    [
        new(DVBT_DAGC_TRG_VAL, 0x39), new(DVBT_AGC_TARG_VAL_0, 0x0), new(DVBT_AGC_TARG_VAL_8_1, 0x40),
        new(DVBT_AAGC_LOOP_GAIN, 0x16), new(DVBT_LOOP_GAIN2_3_0, 0x8), new(DVBT_LOOP_GAIN2_4, 0x1),
        new(DVBT_LOOP_GAIN3, 0x18), new(DVBT_VTOP1, 0x35), new(DVBT_VTOP2, 0x21), new(DVBT_VTOP3, 0x21),
        new(DVBT_KRF1, 0x0), new(DVBT_KRF2, 0x40), new(DVBT_KRF3, 0x10), new(DVBT_KRF4, 0x10),
        new(DVBT_IF_AGC_MIN, 0x80), new(DVBT_IF_AGC_MAX, 0x7f), new(DVBT_RF_AGC_MIN, 0x80),
        new(DVBT_RF_AGC_MAX, 0x7f), new(DVBT_POLAR_RF_AGC, 0x0), new(DVBT_POLAR_IF_AGC, 0x0),
        new(DVBT_AD7_SETTING, 0xe9f4), new(DVBT_SPEC_INV, 0x1)
    ];

    public static readonly byte[][] BW_PARAMS =
    [
        // 6 MHz
        [0xf5,0xff,0x15,0x38,0x5d,0x6d,0x52,0x07,0xfa,0x2f,0x53,0xf5,0x3f,0xca,0x0b,0x91,
         0xea,0x30,0x63,0xb2,0x13,0xda,0x0b,0xc4,0x18,0x7e,0x16,0x66,0x08,0x67,0x19,0xe0],
        // 7 MHz
        [0xe7,0xcc,0xb5,0xba,0xe8,0x2f,0x67,0x61,0x00,0xaf,0x86,0xf2,0xbf,0x59,0x04,0x11,
         0xb6,0x33,0xa4,0x30,0x15,0x10,0x0a,0x42,0x18,0xf8,0x17,0xd9,0x07,0x22,0x19,0x10],
        // 8 MHz
        [0x09,0xf6,0xd2,0xa7,0x9a,0xc9,0x27,0x77,0x06,0xbf,0xec,0xf4,0x4f,0x0b,0xfc,0x01,
         0x63,0x35,0x54,0xa7,0x16,0x66,0x08,0xb4,0x19,0x6e,0x19,0x65,0x05,0xc8,0x19,0xe0]
    ];

    public static readonly int[][] SNR_CONSTANTS =
    [
        [85387325, 85387325, 85387325, 85387325],
        [86676178, 86676178, 87167949, 87795660],
        [87659938, 87659938, 87885178, 88241743]
    ];
    public static readonly int CONSTELLATION_NUM = SNR_CONSTANTS.Length;
    public static readonly int HIERARCHY_NUM = SNR_CONSTANTS[0].Length;
}
