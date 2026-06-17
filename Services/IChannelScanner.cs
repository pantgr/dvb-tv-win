using DvbTv.Models;

namespace DvbTv.Services;

/// <summary>Scans the UHF band and reads SI/PSI to discover DVB-T services.</summary>
public interface IChannelScanner
{
    Task<IReadOnlyList<Channel>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
