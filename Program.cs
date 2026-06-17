using DvbTv.Services;
using DvbTv.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DvbTv;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Serilog ready from line one: Console + rolling daily file under logs/.
        // When something breaks we read the log and see WHICH layer failed
        // (tuner lock vs demux vs decode) without guessing.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            // shared:true — without it, if the file is held open at startup (a second instance,
            // or even a log reader), Serilog silently rolls to dvbtv-<date>_001.log and the
            // "obvious" file stops telling the whole story (bit us on 2026-06-10).
            .WriteTo.File("logs/dvbtv-.log", rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();

        try
        {
            Log.Information("=== DvbTv starting ===");
            ApplicationConfiguration.Initialize();

            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();

            // One interface -> one implementation. Swap any layer without touching the rest.
            // Tuner backend is config-selectable: "WinUsb" (RTL2832 hardware demod over WinUSB,
            // the current stick driver) or "Bda" (legacy DirectShow). The stick is WinUSB XOR BDA.
            var tunerBackend = builder.Configuration["Tv:Tuner"] ?? "WinUsb";
            if (tunerBackend.Equals("Bda", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddSingleton<IDvbTuner, DvbTuner>();
                Log.Information("Tuner backend: BDA (DirectShow)");
            }
            else
            {
                builder.Services.AddSingleton<IDvbTuner, RtlSdrDvbTuner>();
                Log.Information("Tuner backend: WinUSB (RTL2832 hardware demod)");
            }
            builder.Services.AddSingleton<IVideoPlayer, VlcVideoPlayer>();
            builder.Services.AddSingleton<IChannelStore, JsonChannelStore>();
            builder.Services.AddSingleton<IChannelScanner, ChannelScanner>();
            builder.Services.AddSingleton<ITvController, TvController>();
            builder.Services.AddSingleton<MainForm>();

            using var host = builder.Build();
            var form = host.Services.GetRequiredService<MainForm>();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup");
        }
        finally
        {
            Log.Information("=== DvbTv stopped ===");
            Log.CloseAndFlush();
        }
    }
}
