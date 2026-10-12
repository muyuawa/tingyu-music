# Third-party components — Muyu Music 0.11.1

Notices and metadata are in licenses/. Exact resolved NuGet packages are listed in licenses/package-inventory.json. No GPL player application code is incorporated.

| Component | Version/source | License/distribution |
| --- | --- | --- |
| libFLAC | e94ff9f68b8e7dbd3e9f8b1ac18a8eca1914f181 | BSD library statically linked; libFLAC.txt and pristine source included; GPL command-line tools excluded. |
| FFmpeg | 330caae0c1acccd2222edc52a05940c574561ce5 | LGPL-2.1-or-later, four separate dynamic libraries; GPL/version3/network disabled, no external libraries; complete unmodified source, COPYING files, script and source/binary hashes included. |
| TagLibSharp | 2.3.0 / TaglibSharp-2.3.0.0 | LGPL-2.1-only; separate replaceable assembly, complete library/build source included without test music. |
| NAudio.Asio / NAudio.Core / NAudio.Wasapi | 2.3.0 | MIT; separate managed assemblies, no Steinberg SDK or device driver shipped. |
| Microsoft.WindowsAppSDK | 2.2.0 | NuGet binary terms collected; repository licensing does not replace binary terms. |
| LiquidGlassWinUI | Managed source 647fd60; native NuGet 1.0.3 | MIT, luckyelysia; LICENSE and Muyu integration notes retained; managed/HLSL/native sources included in source package. |
| Win2D | 1.4.0 | Win2D-binary-license.html and package metadata retained. |
| Microsoft.Data.Sqlite | 8.0.31 | Microsoft MIT; SQLite public domain; SQLitePCLRaw Apache-2.0. |
| .NET 8 runtime | Self-contained publish | MIT and bundled third-party notices. |
| Visual C++ runtime | VS 2022 redistributable | App-local redistributable binaries and Microsoft terms. |

FFmpeg and TagLibSharp may be modified and replaced, including reverse engineering to debug modifications; see licenses/LGPL-REBUILD.md. The source package permits rebuilding/relinking compatible libraries. Verification scripts protect the original delivery record and do not prevent runtime replacement.

The development-only BtbN FFmpeg encoder generates synthetic fixtures and is excluded from delivery. Configuration/build logs, local environment and toolchains are excluded. Source identifiers are recorded in archives, ffmpeg-source.json, NuGet locks and notices. Third-party licensing does not convey ownership of Muyu or imported music.
