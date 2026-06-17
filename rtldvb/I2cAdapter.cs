namespace RtlDvb;

internal sealed class I2cMessage(int addr, int flags, byte[] buf, int len)
{
    public const int I2C_M_TEN = 0x0010;  // ten-bit chip address marker (used as "plain" here)
    public const int I2C_M_RD  = 0x0001;  // read (slave→master)

    public int Addr { get; } = addr;
    public int Flags { get; } = flags;
    public byte[] Buf { get; } = buf;
    public int Len { get; } = len;
}

/// <summary>Port of I2cAdapter.java — retrying I2C master with the messy 3-method RTL2832 xfer.</summary>
internal abstract class I2cAdapter
{
    private readonly object _lock = new();
    private const int RETRIES = 10;

    /// <summary>Cached RTL2832 demod page (shared with the frontend's page management). -1 = unknown.</summary>
    public int Page { get; set; } = -1;

    public void Transfer(int addr, int flags, byte[] buf) => Transfer(addr, flags, buf, buf.Length);
    public void Transfer(int addr, int flags, byte[] buf, int len) => Transfer(new I2cMessage(addr, flags, buf, len));

    public void Transfer(int addr1, int flags1, byte[] buf1, int addr2, int flags2, byte[] buf2)
        => Transfer(new I2cMessage(addr1, flags1, buf1, buf1.Length), new I2cMessage(addr2, flags2, buf2, buf2.Length));

    public void Send(int addr, byte[] buf, int count) => Transfer(addr, I2cMessage.I2C_M_TEN, buf, count);
    public void Recv(int addr, byte[] buf, int count) => Transfer(addr, I2cMessage.I2C_M_TEN | I2cMessage.I2C_M_RD, buf, count);

    private void Transfer(params I2cMessage[] messages)
    {
        lock (_lock)
        {
            for (int i = 0; i < RETRIES; i++)
            {
                try
                {
                    if (MasterXfer(messages) == messages.Length) return;
                }
                catch (RtlUsbException) when (i != RETRIES - 1) { /* retry */ }
            }
        }
    }

    protected abstract int MasterXfer(I2cMessage[] messages);
}

/// <summary>Opens the demod's I2C gate around a block of tuner I2C access, always closes it.</summary>
internal abstract class I2GateControl
{
    protected abstract void I2cGateCtrl(bool enable);

    public void RunInOpenGate(Action r)
    {
        try { I2cGateCtrl(true); r(); }
        finally { I2cGateCtrl(false); }
    }
}
