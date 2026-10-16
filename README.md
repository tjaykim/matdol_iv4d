<p align="center"><img src="Assets/Icons/icon-128.png" width="96" alt="matdol"></p>

<h1 align="center">matdol image viewer for developers</h1>
<p align="center"><i>grind every pixel</i></p>

matdol is a fast image viewer for people who work on images and video at the pixel level: codec, ISP and display developers.
It runs on Windows and Linux (macOS: build from source).

- **Raw data**: headerless YUV / RGB / gray files in many pixel formats and bit depths (8–16 bit), with auto size detection, frame playback, separate fields and stereo 3D layouts
- **Image formats**: PNG, JPEG, BMP, WebP, GIF, PNM, DPX, TIFF (16-bit, float, multi-page), Y4M, camera raw (DNG, CR2, NEF; embedded preview for others), HEIC / HEIF / AVIF via the OS decoder
- **Compare**: up to 4 slots in a 2×2 split view with synced zoom and pan, difference |A − B|, average and binary-diff views, even across different sizes, bit depths and color spaces
- **Inspect**: pixel values in the original bit depth, single-channel views, YUV → RGB matrices (BT.601 / 709 / 2020 …), block grid
- **Optional**: VESA DSC (.dsc) decoding through a separate plugin ([matdol-dsc](https://github.com/tjaykim/matdol-dsc))

Press **F1** in the app for keys and usage.

## Download

Get the latest version from [**Releases**](../../releases/latest).

| OS | File | Install |
|---|---|---|
| Windows 10/11 (x64) | `matdol-<ver>-win-x64-setup.exe` | Run the installer. Or unzip `…-win-x64-portable.zip` and run `matdol.exe` |
| Ubuntu / Debian (x64) | `matdol_<ver>_amd64.deb` | `sudo apt install ./matdol_<ver>_amd64.deb` |
| Any Linux (x64), no admin rights | `matdol-<ver>-linux-x64.tar.gz` | Extract, then `./install.sh` (installs to `~/.local`) |

The Windows build is not code-signed yet — if SmartScreen warns, click *More info → Run anyway*.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run -c Release
```

Installers (output in `artifacts/installers/`):

| Target | Command | Needs |
|---|---|---|
| Windows setup | `package-win.cmd` | [Inno Setup 6](https://jrsoftware.org/isinfo.php) |
| Linux .deb + .tar.gz | `package-linux.cmd` (on Windows) | nothing else — works on Windows, no WSL |
| macOS .dmg | `bash installer/macos/build-dmg.sh [osx-arm64\|osx-x64]` | a Mac (macOS 12+) |

The **build & release** workflow (Actions tab → *Run workflow*) builds the Windows and Linux packages on GitHub and drafts a release.

## Feedback

Bug reports and sample files that break things are welcome — please open an [issue](../../issues).

## License

[MIT](LICENSE). Third-party components and their licenses: [Assets/Legal/ThirdPartyNotices.txt](Assets/Legal/ThirdPartyNotices.txt).

<sub>Parts of the code were written with the help of AI.</sub>
