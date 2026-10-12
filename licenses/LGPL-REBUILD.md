# LGPL 组件、源码与替换

Muyu Music 动态加载 LGPL 组件。允许为修改这些组件及调试此类修改而进行逆向工程，也允许用兼容接口的修改版替换对应 DLL；没有签名校验或阻止替换的应用机制。应用其他部分的许可不限制 LGPL 授予的权利。

## FFmpeg

精确提交、源码 URL、源码与四个 DLL 的 SHA256 记录在 `ffmpeg-source.json`。使用未修改源码，配置 GPL=0、version3=0、network=0，不链接第三方外部库。`ffmpeg-minimal-source.tar.gz` 是完整上游源码，包含其全部 COPYING 文件。

解压源码，在 Windows x64 安装 MSVC 2022、Windows SDK、MSYS2 bash 与 make。将 `build-ffmpeg.ps1` 放在项目 scripts 目录，指定 `-Source` 为解压目录；本工作区工具放在相邻 `.tools`，脚本不会永久修改 PATH。脚本包含完整 configure 参数和中文编译器横幅兼容包装，不修改 FFmpeg 源码。原构建使用 MSVC 19.42.34433；其他版本可能生成不同字节，兼容 ABI 即可替换。

替换应用根目录和 `audio-host` 中的 `avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、`swresample-6.dll`。必须同时保持这四个库的相互兼容。新的主 ABI 版本需要重新链接 MuyuAudio：工程 `native/CMakeLists.txt` 标明头文件与导入库位置。

## TagLibSharp 2.3.0

NuGet 包使用 LGPL-2.1-only。随附 `TagLibSharp-2.3.0-library-source.zip`，来自未修改的上游标签 `TaglibSharp-2.3.0.0`，包含全部库源文件、项目、构建属性、原始签名密钥、COPYING 和作者表；未收录测试音乐样本。

解压后执行：

```powershell
dotnet build src/TaglibSharp/TaglibSharp.csproj -c Release -p:TargetFrameworks=netstandard2.0 -p:GeneratePackageOnBuild=false
```

用生成的 `TagLibSharp.dll` 替换应用与 Audio Host 的同名文件。应用使用公开 .NET API，不把 TagLibSharp 合并入自身程序集。

源码来源：https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0 。库源文件中的版权声明和许可条款继续有效。

## 其他组件

libFLAC 静态链接库使用其 BSD 许可，声明在 `libFLAC.txt`；未分发 GPL flac 命令行工具。NAudio.Asio 使用 MIT 许可，见 `NAudio-MIT.txt`；本包未分发 Steinberg ASIO SDK 或设备驱动。Microsoft 与 SQLite 相关依赖的许可和包版本见上级 `package-inventory.json` 及对应目录。

开发时用于生成测试音频的 BtbN FFmpeg 可执行程序不在发行包中。发行包仅包含上述自行构建的四个离线 FFmpeg 动态库。
