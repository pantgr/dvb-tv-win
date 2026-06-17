namespace DvbTv.Models;

/// <summary>An EPG event (programme) from the DVB EIT.</summary>
public sealed class DvbEvent
{
    public int ServiceId { get; init; }
    public int EventId { get; init; }
    public DateTime StartUtc { get; init; }
    public TimeSpan Duration { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    public DateTime StartLocal => StartUtc.ToLocalTime();
    public DateTime EndLocal => (StartUtc + Duration).ToLocalTime();
    public string TimeRange => $"{StartLocal:HH:mm}–{EndLocal:HH:mm}";
}
