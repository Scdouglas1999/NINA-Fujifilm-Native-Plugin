# N.I.N.A. Fujifilm Native Plugin

![N.I.N.A.](https://img.shields.io/badge/N.I.N.A.-3.2%2B-purple?style=flat-square)
![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue?style=flat-square)
![License](https://img.shields.io/badge/License-Apache_2.0-green?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-8.0-blue?style=flat-square)
[![Support on Patreon](https://img.shields.io/badge/Support%20on-Patreon-f96854?style=for-the-badge&logo=patreon&logoColor=white)](https://www.patreon.com/cw/SeanDouglas)

A native camera integration plugin for [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://nighttime-imaging.eu/) that enables direct USB communication with Fujifilm cameras. This plugin bypasses generic ASCOM drivers to interface directly with the camera firmware, providing features and performance not available through standard drivers.

Nothing in the plugin is conditioned on a camera model. What it does is derived from what a body
reports over USB - the API codes it advertises and the capability lists it returns - so a camera it
has never seen is treated the same as one it has.

See [RELEASE_NOTES.md](RELEASE_NOTES.md) for what changed in each version. **If you are on 3.0.2.0
or 3.0.3.0, update: those releases capped the maximum exposure at 60 seconds.**

---

## Support Development

<p align="center">
  <a href="https://www.patreon.com/cw/SeanDouglas"><img src="https://img.shields.io/badge/Support%20the%20Plugin%20on-Patreon-f96854?style=for-the-badge&logo=patreon&logoColor=white" alt="Support the plugin on Patreon"></a>
</p>

This plugin is free to use and intended to stay that way. If it helps you run a Fujifilm camera in N.I.N.A., or you want to support continued compatibility work, you can optionally [support development on Patreon](https://www.patreon.com/cw/SeanDouglas).

Patreon support helps with the unglamorous work that keeps camera plugins useful: testing real bodies and lenses, tracking Fujifilm SDK behavior, packaging installers, maintaining documentation, and fixing edge cases that only appear on specific rigs. There are no paid-only builds or locked features; support is appreciated, never required.

## Features

### Camera Control

- **Direct USB communication** with the camera, bypassing generic ASCOM drivers
- **Timed and bulb exposures up to 60 minutes.** Sub-exposures longer than the camera's longest
  timed shutter speed use bulb; bodies advertising the SDK's T-mode codes can time up to an hour
  natively. An exposure is never silently shortened - a request the camera cannot satisfy fails with
  an explanation instead
- **Abort an exposure in progress**, so a sequence can abandon a long sub for cloud or a meridian
  flip rather than waiting it out. Bodies that refuse the request fall back to waiting
- **ISO from the camera's own list.** Auto-ISO modes are filtered out, so a sequence cannot hand
  exposure control back to the camera
- **RAW bit depth and compression control.** 16-bit for more headroom on faint signal; lossless
  compression roughly halves the file and the download time between subs
- **Long Exposure NR is switched off**, and reported if left on. With it enabled the camera shoots a
  matching dark after every long sub and subtracts it internally, roughly doubling the time per frame
  and applying calibration you did not choose
- **Sensor crop mode**, so a high-resolution body can shoot a smaller frame for a small target
- **Card recording is disabled** while connected, so card writes do not compete with the USB download
- **Battery reporting** on any body that implements a known query layout
- **Lens detection**: the attached lens model and whether it has optical stabilisation
- **Electronic aperture control**: discovers the f-numbers reported by the attached lens and lets
  the user select and verify aperture from the Fuji Lens panel. Setting an aperture automatically
  switches the camera to Manual exposure mode so the body does not retain automatic aperture control

Optional features are gated on the list of API codes the connected camera advertises, so a body that
does not implement one is simply left alone rather than being sent a call it will reject.
Electronic aperture is the exception: supported bodies do not consistently include the standard
Cap/Set/Get aperture calls in that list, so the plugin uses `XSDK_CapAperture` as the authoritative
capability check.

### X-Trans Sensor Support

- **Synthetic Bayer Preview**: Converts X-Trans sensor data to a standard Bayer pattern for full-color live preview in N.I.N.A.
- **Non-Destructive Processing**: Preview conversion does not affect saved images; original RAW data is preserved
- **Correct Metadata**: Writes appropriate `BAYERPAT` and `ROWORDER` FITS headers for compatibility with PixInsight, Siril, and other stacking software

### Electronic Lens Focuser

- **Native lens control**: exposes electronic Fujifilm lenses as focuser devices in N.I.N.A.
- **Full mechanical travel**, including the range past infinity. The SDK reports the nominal infinity
  and close-focus marks plus the "over search" travel beyond each; using only the nominal marks put
  infinity at position 0 with nothing beneath it, which is where autofocus runs failed
- **Positions are never negative** and always fall within `0 .. MaxStep`, and the focuser description
  reports where infinity sits and how much past-infinity travel the lens has
- **The camera is held in manual focus** while the focuser is connected, so it cannot refocus on its
  own when an exposure half-presses the shutter. Your original mode is restored on disconnect
- **Focus limiter awareness**: if a limiter is set so autofocus cannot reach infinity, the plugin
  says so rather than leaving you to work it out
- Camera and focuser share one reference-counted SDK session, so disconnecting either does not
  invalidate the other

### Configuration Options

See [Plugin Settings](#plugin-settings) for the full list, including RAW quality, Long Exposure NR,
crop mode, demosaic quality, live view tuning and focuser behaviour.

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

Two cameras are deliberately absent.

**X-T2 is not supported.** Fujifilm's current Camera Control SDK does not list it, and no one has
reported getting it working with this plugin. A legacy model module for it exists and exports the
entry points the plugin needs, so the code will still attempt a connection if you try, but treat
that as unsupported and unlikely to work rather than as a feature.

**GFX ETERNA 55 is not supported.** It is a cinema camera, and N.I.N.A. integration here depends on
the still-camera RAF capture and readout path, which has not been shown to behave the same way on
it.

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
| **Exposure Mode** | `M` (Manual) | **Important.** In Aperture or Shutter Priority the camera keeps control of exposure and offers the plugin only a single shutter speed with no bulb, which caps your maximum exposure |
| **Drive Dial** | `S` (Single Shot) | Prevents burst capture conflicts |
| **Shutter Dial** | `T` (Time) or `A` (Auto) | Allows software shutter control |
| **ISO Dial** | `A` (Auto) or `C` (Command) | Allows software ISO control |
| **Focus Mode** | Either; the plugin handles it | It switches the body to manual focus while the focuser is connected, so it cannot refocus on its own, and restores your setting on disconnect. The lens' own focus capability is only readable in manual focus mode, which is why this happens before anything else |

### N.I.N.A. Settings

For X-Trans cameras to display color preview:

1. Navigate to **Options > Imaging**
2. Enable **Debayer Image** (or "Auto Debayer")
3. In the Imaging tab, verify the **Debayer** toggle is active in the image panel toolbar

---

## Plugin Settings

Access plugin settings through **Options > Plugins > Fujifilm Native Camera**.

| Setting | Description | Default |
| :--- | :--- | :--- |
| **Bulb Release Delay** | Delay in milliseconds for bulb mode releases (0-5000) | 500ms |
| **Save Native RAF Sidecar** | Saves original RAF file alongside processed images | Enabled |
| **Extended FITS Metadata** | Adds Fujifilm metadata to FITS headers | Enabled |
| **Stop camera writing to its memory card** | Prevents card writes competing with the USB download | Enabled |
| **RAW bit depth** | Requests 14-bit or 16-bit RAW (GFX bodies only) | 16-bit |
| **RAW compression** | Lossless roughly halves the file and the download time | Lossless |
| **Turn off Long Exposure NR** | Stops the camera shooting and subtracting its own dark after every long sub | Enabled |
| **Sensor crop** | Crop mode to request; smaller frames download faster | Leave alone |
| **Focus distance unit** | Unit used when reporting lens focus limiter ranges | Metres |
| **Force manual focus mode while connected** | Stops the body refocusing on its own between exposures | Enabled |
| **Demosaic Quality** | Preview processing quality (Fast/Balanced/High Quality) | Fast |
| **Live View Quality** | Fine or Basic. Not every body accepts every value; a rejected one falls back to Fine | Fine |
| **Live View Image quality / size** | Relative stream size (Large/Medium/Small) | Large |

RAW bit depth is only offered by some bodies; where it is not, the camera's own setting is left
alone. The same applies to compression, Long Exposure NR and crop mode - the plugin asks the camera
what it supports rather than assuming from the model.

Settings are written to disk when you press **Save** and also when you navigate away from the
options page. **Export Diagnostics** writes a JSON report and shows you the file path; plugin
events are also mirrored into N.I.N.A.'s own log.

---

## Sequence instructions

The plugin adds these to N.I.N.A.'s advanced sequencer under the **Fujifilm** category:

| Instruction | Purpose |
| :--- | :--- |
| **Park Fujifilm focuser at infinity** | Moves the lens to its infinity mark, with an optional offset, so a session starts from a known focus position |
| **Set Fujifilm RAW quality** | Changes RAW bit depth and compression mid-sequence |
| **Turn off Fujifilm Long Exposure NR** | Ensures the camera is not shooting its own darks |
| **Set Fujifilm aperture** | Switches to Manual exposure mode, then sets and verifies the f-number of an attached electronic lens |

---

## Troubleshooting

| Issue | Cause | Solution |
| :--- | :--- | :--- |
| **Maximum exposure is only 60 seconds** | A regression in 3.0.2.0 through 3.0.3.0: the SDK reports "no bulb support" on essentially every body, and the plugin believed it | Fixed in 3.1.0.0. The maximum returns to 60 minutes |
| **Every long sub takes twice as long** | The camera's Long Exposure NR is on, so it shoots and subtracts its own dark after each frame | Leave **Turn off the camera's Long Exposure NR** enabled. The plugin also warns when it finds it on |
| **Camera Busy / Exposure Fail** | Camera writing to its memory card | Leave **Stop the camera writing to its memory card** enabled, or increase the image download delay in N.I.N.A. options |
| **Focuser position differs every reconnect, or is negative** | The body refocusing on its own, or a lens parked past infinity being clamped | Fixed in 3.1.0.0. Leave **Force manual focus mode while connected** enabled |
| **Autofocus fails near the bottom of the range** | Infinity used to sit at position 0 with no travel beneath it | Fixed in 3.1.0.0. The focuser description now shows where infinity sits and how much past-infinity travel exists; size your autofocus steps to fit |
| **Focus moves time out even though the lens moved** | Some lenses report a position offset from the one they were commanded | Fixed in 3.1.0.0; a move now completes when the lens stops moving, and the offset is logged |
| **A move never finishes and reports a neighbouring position** | Fixed in 3.1.1.0: requests were snapped to a multiple of the lens' minimum drive step, so most positions could never be reported and N.I.N.A. waited for one that did not exist | Update to 3.1.1.0 |
| **Autofocus cannot reach the stars** | The lens focus limiter is set to a range that excludes infinity | Set the limiter switch to its full range. The plugin reports the limiter ranges and says explicitly when one excludes infinity |
| **Exposure Error 0x2003** | Invalid dial combination | Set the mode to Manual, and the Shutter and ISO dials to `T`/`A` or `C`, so the plugin can control them. In Aperture Priority the camera only offers one shutter speed and no bulb |
| **Black & White Preview** | Debayering disabled | Enable **Debayer Image** in N.I.N.A. imaging options |
| **Live view looks stretched or poor** | Fixed in 3.1.0.0: the frame size was estimated wrongly, and the default quality was one some bodies reject | Update to 3.1.0.0; nothing needs configuring |
| **Lens Not Detected** | Manual focus lens or adapter | Only electronic lenses report a programmable focus range |
| **Camera vanishes from N.I.N.A. during an equipment rescan** | Detection used to open a second handle on an already-connected camera, which the SDK refuses | Fixed in 3.1.2.0 |
| **Battery Unavailable** | The camera did not accept any known battery query layout | Use the camera display. The plugin probes rather than relying on a model list, so this is rare |

---

## Limitations

- **Live view is a preview, not an imaging path.** Measured on a GFX100S II at the Large setting:
  1024x768 at 15-18 fps, roughly 200-230 KB per frame on Fine and 46-52 KB on Basic. Dimensions
  differ by model and sensor aspect ratio
- **No sensor temperature.** Astro software usually writes `CCD-TEMP` from the camera; the Fujifilm
  SDK exposes a temperature reading only in its movie-mode API, so there is nothing to report for
  stills. This will not be added unless Fujifilm exposes it
- **No binning**: only full-frame capture is supported
- **RAW only**: JPEG capture is not supported
- **One active camera**: the SDK runtime and plugin session are process-global
- **No pixel-shift multi-shot.** The SDK supports it, but it needs a different capture sequence and
  multi-frame handling, and has not been implemented
- **X-T2 is not supported** and is not known to work

---

## Building from Source

### Requirements

- Visual Studio 2022
- .NET 8.0 SDK
- Fujifilm Shooting SDK x64 runtime (must be obtained separately; it is not committed to this repository)

### Build Steps

1. Extract the x64 SDK runtime to a local directory. It must contain `XAPI.dll`, `XSDK.DAT`, `FTLPTP.dll`, the transport DLLs, and the `FF####API.dll` model modules.
2. Open the solution in Visual Studio or build from the command line, passing that directory:
   ```powershell
   dotnet build -c Release -p:FujifilmSdkDir="C:\path\to\FujifilmSdk"
   ```
   The `FUJIFILM_SDK_DIR` environment variable can be used instead.
3. The build copies the SDK runtime to the plugin output root. Release packaging fails if any required runtime file is missing, so an unusable installer cannot be produced silently.

### Tests

The deterministic logic is covered by a platform-neutral xUnit project, so it runs without N.I.N.A.,
WPF, a Fujifilm SDK installation, or camera hardware:

```powershell
dotnet test tests/NINA.Plugins.Fujifilm.Tests/NINA.Plugins.Fujifilm.Tests.csproj -c Release
```

It covers model matching and every shipped configuration, focus travel mapping and move settling,
focus limiter interpretation, shutter selection and the bulb ceiling, sensitivity filtering, battery
layout discovery, live view zoom mapping, capability gating, settings normalisation, shared-session
ownership, metadata typing, active-area crop validation and X-Trans-to-RGGB conversion.

### Verifying the SDK interop layer

`build/verify-sdk-interop.py` checks the interop surface against the real SDK: every `DllImport`
entry point against `XAPI.dll`'s export table, and every constant, API code and API parameter against
the SDK headers. It has caught shipped defects that reading the code did not - an entry point that
does not exist in the DLL at all, among them. It needs the licensed SDK, so CI cannot run it:

```powershell
python3 build/verify-sdk-interop.py --sdk-dll path\to\XAPI.dll --headers path\to\SDK\HEADERS
```

### Testing against a real camera

`tools/hardware-probe` drives a connected camera through the whole feature set. It compiles the
plugin's own decision-making classes and feeds them live camera data, so the shipping logic is what
gets exercised rather than a reimplementation, and it applies every setting across every value the
camera advertises before restoring each one. See its README for how to run it.

Because the SDK ships a Linux build, the probe can drive a camera from a Linux workstation even
though the plugin itself is Windows-only.

---

## License

This project is licensed under the **Apache License 2.0**. See the [LICENSE](LICENSE) file for details.

---

*This software is an independent community project. It is not affiliated with, endorsed by, or associated with FUJIFILM Corporation or the N.I.N.A. development team.*
