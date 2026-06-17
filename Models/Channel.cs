namespace DvbTv.Models;

/// <summary>A single DVB-T television service discovered during a scan.</summary>
public sealed class Channel
{
    public string Name { get; set; } = "";
    public int LogicalChannelNumber { get; set; }
    public long FrequencyHz { get; set; }
    public int BandwidthMhz { get; set; } = 8;
    public int ServiceId { get; set; }
    public int PmtPid { get; set; }
    public int VideoPid { get; set; }
    public int AudioPid { get; set; }

    public override string ToString() =>
        LogicalChannelNumber > 0 ? $"{LogicalChannelNumber}. {Name}" : Name;
}
