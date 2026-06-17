# DvbTv

DVB-T live TV στο PC από RTL2832U USB stick. C# / .NET 9 WinForms, DirectShow BDA tuning + LibVLCSharp GPU decode (NVDEC/d3d11va), DI + Serilog.

Πλήρης τεκμηρίωση: **`SKILL.md`** (overview + status + build), **`HARDWARE.md`** (stick + ελληνικό DVB-T), **`ARCHITECTURE.md`** (design).

## Build & run (winpc)
```powershell
# packages (μία φορά)
dotnet add DvbTv.csproj package Microsoft.Extensions.Hosting
dotnet add DvbTv.csproj package Serilog.Extensions.Hosting
dotnet add DvbTv.csproj package Serilog.Sinks.Console
dotnet add DvbTv.csproj package Serilog.Sinks.File
dotnet add DvbTv.csproj package LibVLCSharp
dotnet add DvbTv.csproj package LibVLCSharp.WinForms
dotnet add DvbTv.csproj package VideoLAN.LibVLC.Windows

dotnet build -c Debug
bin\Debug\net9.0-windows\DvbTv.exe
```

## Status
Σκελετός που τρέχει. VLC player + GPU + DI + logging λειτουργικά (TV → Open file δοκιμάζει το pipeline). BDA tuner + scanner = skeletons (επόμενο milestone). Logs: `bin\Debug\net9.0-windows\logs\`.
