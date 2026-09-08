# 3.2.0.0

## Electronic aperture control

Contributed by Roman Malizderskyi (@roma-derski), closing the aperture feature request. Tested by
the contributor on an X-T4 (firmware 2.12) with the Fujinon XF27mmF2.8 R WR, the Viltrox AF 27mm
F1.2 XF and the Fujinon XF18-135mmF3.5-5.6 R LM OIS WR: every f-number each lens advertised was
set and read back, and the original aperture restored.

- **The Fuji Lens panel now has an aperture selector.** It lists the f-numbers the attached lens
  reports through `XSDK_CapAperture`, so a native Fujinon and a third-party electronic lens follow
  the same path. The camera stays authoritative: nothing is derived from the lens model name.
- **A new sequencer instruction, "Set Fujifilm aperture".** Set f/1.2 for the sky, refocus, shoot,
  set f/8 for the foreground, refocus, shoot.
- **A camera action, `Camera:SetAperture`, for other plugins and automation.** Pass
  `{"fNumber":2.8}`; on success it returns the requested and the camera-verified f-number as JSON,
  and it throws on anything else rather than reporting a success it cannot back up.
- **Every write is verified.** The plugin reads the aperture back after setting it. If the camera
  reports something other than what was asked for, the previous aperture and exposure mode are
  restored and the command fails with the values it saw.
- **Manual exposure mode is selected before the write.** The SDK refuses aperture writes in Program
  mode with `COMBINATION (0x2003)`. A successful aperture command leaves the camera in Manual, since
  restoring Program or a priority mode would immediately hand aperture selection back to the body.
- **Zoom lenses are re-read at command time.** The SDK's lens-position token, not its
  zoom-capability flag, decides which apertures are available: the Viltrox 27mm reports no zoom
  capability but needs token 1, and the XF18-135 reports no zoom capability while its token changes
  as it is zoomed. The available range is refreshed whenever that token changes and again
  immediately before every write, so a wide-end aperture is never applied at a focal length where
  it does not exist.

The Fuji Lens panel also shows a vendor-qualified lens name, whether the battery is charging, and
has its own icon.

## Failed exposures now report the real error

Also contributed by Roman Malizderskyi. When a capture failed inside the SDK - an ISO and shutter
combination the body rejected, JPEG returned where RAW was required, a timeout waiting for RAF
data - the adapter kept telling N.I.N.A. the image was not ready. N.I.N.A. waited out its own
timeout and then replaced the actionable message with "Camera did not set image as ready after
exposure time + 60 seconds". A failed capture is now terminal, so N.I.N.A. proceeds straight to
the download step and the original SDK error surfaces there. Confirmed against N.I.N.A. 3.2's
`GenericCamera` source, which swallows the download exception and returns null, which this
plugin already turns into the recorded error.

A related defect in this plugin is fixed at the same time: a failed download left the adapter in
its Downloading state until the next exposure started, which suspended battery and lens status
refreshes and, with the new feature, refused aperture changes with "an exposure or image download
is in progress".

## What was not merged

The contributor's branch also carried three session-reliability changes: disabling the camera's
auto power-off for the duration of a session, bypassing camera discovery while a camera is
connected, and removing the device-info query that 3.1.2.0 added for describing an already-open
camera. They are held back, not rejected. None came with a log or a reproduction, the discovery
cache was per-instance in a layer where 3.1.2.0 had just found that several instances coexist, and
the power-off path selected its API from the model name, which this plugin otherwise avoids. Each
is welcome as its own change with the diagnostics export that motivated it.

## Testing

The aperture catalogue, camera-action wire contract, lens-vendor resolution, battery charging
state and exposure-state helpers are covered by unit tests that run without hardware. The
`XSDK_CapAperture`, `XSDK_SetAperture`, `XSDK_GetAperture` and `XSDK_GetLensZoomPos` bindings were
checked against the SDK header. The hardware probe gains an `--aperture-only` mode that sets and
reads back every advertised f-number and restores the original, for anyone who wants to confirm a
lens before relying on it in a sequence. The maintainer's adapter-state fix has not been run
against a camera in this release.

