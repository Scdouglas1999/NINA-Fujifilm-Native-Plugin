# N.I.N.A. Fujifilm Native Plugin

![N.I.N.A.](https://img.shields.io/badge/N.I.N.A.-3.2%2B-purple?style=flat-square)
![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue?style=flat-square)
![License](https://img.shields.io/badge/License-Apache_2.0-green?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-8.0-blue?style=flat-square)
[![Support development](https://img.shields.io/badge/Support-Patreon-f96854?style=flat-square)](https://www.patreon.com/cw/SeanDouglas)

A native camera integration plugin for [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://nighttime-imaging.eu/) that enables direct USB communication with Fujifilm cameras. This plugin bypasses generic ASCOM drivers to interface directly with the camera firmware, providing features and performance not available through standard drivers.

---

## Support Development

This plugin is free to use and intended to stay that way. If it helps you run a Fujifilm camera in N.I.N.A., or you want to support continued compatibility work, you can optionally [support development on Patreon](https://www.patreon.com/cw/SeanDouglas).

Patreon support helps with the unglamorous work that keeps camera plugins useful: testing real bodies and lenses, tracking Fujifilm SDK behavior, packaging installers, maintaining documentation, and fixing edge cases that only appear on specific rigs. There are no paid-only builds or locked features; support is appreciated, never required.

## Features

### Camera Control

- **Direct USB Communication**: Connects directly to the camera via USB for reliable, low-latency control
- **Full Exposure Control**: Supports timed exposures and bulb mode up to 60 minutes
- **ISO Management**: Queries available ISO values from the camera and allows full programmatic control
- **16-bit RAW Capture**: Captures full-resolution RAW images with complete sensor data
- **Battery Monitoring**: Battery display on models whose SDK call layout has been verified
- **Lens Detection**: Displays attached lens model, focal length, aperture, and OIS (optical image stabilization) status

### X-Trans Sensor Support

- **Synthetic Bayer Preview**: Converts X-Trans sensor data to a standard Bayer pattern for full-color live preview in N.I.N.A.
- **Non-Destructive Processing**: Preview conversion does not affect saved images; original RAW data is preserved
- **Correct Metadata**: Writes appropriate `BAYERPAT` and `ROWORDER` FITS headers for compatibility with PixInsight, Siril, and other stacking software

### Experimental Electronic Lens Focuser

- **Native Lens Control**: Exposes electronic Fujifilm lenses as focuser devices in N.I.N.A.
- **Absolute Position Control**: Moves focus to specific positions within the lens mechanical range
- **N.I.N.A. Focuser Interface**: Camera and focuser now share one reference-counted SDK session; autofocus behavior still needs physical lens validation
- **Focus Range Detection**: Automatically queries lens focus limits (infinity to close focus)

### Configuration Options

- **Demosaic Quality**: Selectable preview quality (Fast/Balanced/High Quality) to balance speed and image quality
- **RAF Sidecar Export**: Optional saving of native Fujifilm RAF files alongside processed images
- **Extended FITS Metadata**: Adds Fujifilm-specific metadata to FITS headers
- **Live View Tuning**: Selectable SDK image size and quality

---

## Camera Compatibility

The plugin uses Fujifilm's legacy native Shooting SDK runtime. Configuration files and model modules are present for:

| Series | Models |
| :--- | :--- |
| **GFX (Medium Format)** | GFX100RF, GFX100II, GFX100SII, GFX100S, GFX100, GFX50SII, GFX50S, GFX50R |
| **X-H Series** | X-H2, X-H2S |
| **X-T Series** | X-T5, X-T4, X-T3 |
| **X-S Series** | X-S20, X-S10 |
| **Other** | X-Pro3, X-M5 |
| **Legacy / Experimental** | X-T2 |

Fujifilm's current Camera Control SDK compatibility list also includes the GFX ETERNA 55 cinema camera. This plugin does not claim support for it because N.I.N.A. integration depends on the still-camera RAF capture/readout workflow, and that workflow has not been verified for ETERNA. It should remain hidden/unsupported here until a real camera and current SDK headers prove the required still-capture APIs behave like the X/GFX still bodies.

### X-T2 status

The X-T2 is a special case. Fujifilm's current public Camera Control SDK does not list it, but the legacy Shooting SDK distributed with earlier plugin releases contains `FF0002API.dll`, whose embedded module identity is `X-T2API.dll` (version 1.3.0.0).

Static inspection confirms that module exports every core SDK entry point this plugin needs for discovery, connection, shutter/ISO control, timed and bulb release, RAW transfer, focus-position control, and live view. That makes basic operation technically plausible, not guaranteed: the path has not been validated here with physical X-T2 hardware.

| X-T2 function | Plugin status |
| :--- | :--- |
| USB discovery and connection | Legacy SDK path available; hardware validation needed |
| Timed/bulb capture and RAW download | Required SDK exports present; hardware validation needed |
| Electronic-lens focus control | Required SDK exports present; shared-session implementation complete; lens/firmware and hardware validation still required |
| Live view | Required SDK exports present; implementation complete but physical X-T2 validation still required |
| Battery percentage | Disabled because the X-T2 variadic argument layout is not documented in the available headers |

An installed X-T2 runtime must include `XAPI.dll`, `XSDK.DAT`, the transport DLLs, and `FF0002API.dll` beside the plugin assembly. Diagnostics now report this inventory explicitly. X-T2 firmware 1.10 introduced USB tethering; use current firmware where practical and select `USB AUTO` / `USB TETHER SHOOTING AUTO` before connecting.

---

## Requirements

- **N.I.N.A. 3.2 or later**
- **Windows x64**
- **Visual C++ Redistributable (x64)**
- **.NET 8.0 Runtime**

---

## Installation

1. Download the latest installer from the [Releases](../../releases) page.
2. Close N.I.N.A., run the installer, and restart N.I.N.A.

For a manual installation, keep all release files together in the Fujifilm plugin directory. Do not move the `FF####API.dll` files into a subdirectory.

---

## Camera Setup

Configure your camera with the following settings for proper plugin operation:

### Physical Camera Settings

| Setting | Required Value | Purpose |
| :--- | :--- | :--- |
| **Connection Mode** | `USB TETHER SHOOTING AUTO` or `PC SHOOT AUTO` | Enables USB control |
| **Image Quality** | `RAW` or `RAW+JPEG` | The plugin downloads RAF data and discards JPEG frames |
| **Drive Dial** | `S` (Single Shot) | Prevents burst capture conflicts |
| **Shutter Dial** | `T` (Time) or `A` (Auto) | Allows software shutter control |
| **ISO Dial** | `A` (Auto) or `C` (Command) | Allows software ISO control |
| **Focus Mode** | `S` or `C` for lens focuser; `M` for manual/telescope | Determines focus control method |

### N.I.N.A. Settings

For X-Trans cameras to display color preview:

1. Navigate to **Options > Imaging**
2. Enable **Debayer Image** (or "Auto Debayer")
3. In the Imaging tab, verify the **Debayer** toggle is active in the image panel toolbar

---

## Plugin Settings

Access plugin settings through **Options > Equipment > Camera > Fujifilm**.

| Setting | Description | Default |
| :--- | :--- | :--- |
| **Bulb Release Delay** | Delay in milliseconds for bulb mode releases | 500ms |
| **Save Native RAF Sidecar** | Saves original RAF file alongside processed images | Enabled |
| **Extended FITS Metadata** | Adds Fujifilm metadata to FITS headers | Enabled |
| **Demosaic Quality** | Preview processing quality (Fast/Balanced/High Quality) | Fast |
| **Live View Quality / Size** | Controls SDK live-view stream quality and dimensions | Normal / Large |

---

## Troubleshooting

| Issue | Cause | Solution |
| :--- | :--- | :--- |
| **Camera Busy / Exposure Fail** | Camera writing to SD card | Increase image download delay in N.I.N.A. options, or disable SD card recording in camera settings |
| **Exposure Error 0x2003** | Invalid dial combination | Set Shutter and ISO dials to `T`/`A` or `C` to allow software control |
| **Black & White Preview** | Debayering disabled | Enable **Debayer Image** in N.I.N.A. imaging options |
| **Focus Timeout** | Lens in manual focus mode | Ensure lens focus switch is set to `S` or `C` (not pulled back to MF on clutch-type lenses) |
| **Lens Not Detected** | Manual focus lens or adapter | Only electronic AF lenses are supported for focuser control |
| **X-T2 Not Detected** | Missing legacy model module, camera mode, cable, or another tethering process | Verify `FF0002API.dll` is present; select `USB TETHER SHOOTING AUTO`; close X Acquire/other tethering software; reconnect and export diagnostics |
| **Battery Unavailable** | Model-specific battery call is not verified | This is intentional on X-T2 and unknown models; use the camera display |

---

## Limitations

- **Experimental Live View**: Streaming exists, but frame rate and image quality are limited by the current implementation and camera/SDK behavior
- **No Binning**: Only full-frame capture is supported
- **RAW Only**: Plugin captures RAW images; JPEG capture is not supported
- **One Active Camera**: The SDK runtime and plugin session are process-global
- **Legacy X-T2 Path**: The module exists and exposes the needed APIs, but support remains experimental until tested on physical hardware

---

## Building from Source

### Requirements

- Visual Studio 2022
- .NET 8.0 SDK
- Fujifilm Shooting SDK x64 runtime (must be obtained separately; it is not committed to this repository)

### Build Steps

1. Extract the x64 SDK runtime to a local directory. It must contain `XAPI.dll`, `XSDK.DAT`, `FTLPTP.dll`, the transport DLLs, and the `FF####API.dll` model modules. X-T2 specifically requires `FF0002API.dll`.
2. Open the solution in Visual Studio or build from the command line, passing that directory:
   ```powershell
   dotnet build -c Release -p:FujifilmSdkDir="C:\path\to\FujifilmSdk"
   ```
   The `FUJIFILM_SDK_DIR` environment variable can be used instead.
3. The build copies the SDK runtime to the plugin output root. Release packaging fails if the core SDK or X-T2 model module is missing, preventing an unusable installer from being produced silently.

### Tests

The deterministic logic is covered by a platform-neutral xUnit project, so it can run without N.I.N.A., WPF, a Fujifilm SDK installation, or camera hardware:

```powershell
dotnet test tests/NINA.Plugins.Fujifilm.Tests/NINA.Plugins.Fujifilm.Tests.csproj -c Release
```

The suite covers model matching and all shipped configurations, safe X-T2/GFX100RF battery handling, shutter selection, settings normalization, shared-session ownership, metadata typing, active-area crop validation, and X-Trans-to-RGGB conversion. Native SDK behavior still requires the hardware smoke tests described in the X-T2 status section.

---

## License

This project is licensed under the **Apache License 2.0**. See the [LICENSE](LICENSE) file for details.

---

*This software is an independent community project. It is not affiliated with, endorsed by, or associated with FUJIFILM Corporation or the N.I.N.A. development team.*
