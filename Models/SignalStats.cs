namespace DvbTv.Models;

/// <summary>Tuner lock state and signal quality at a moment in time. Ber = bit errors per 1 MB.</summary>
public readonly record struct SignalStats(bool Locked, int StrengthPercent, int QualityPercent, double SnrDb, int Ber = 0)
{
    public static SignalStats NoLock => new(false, 0, 0, 0, 0);
}
