# 3.1.0.0

Feature release. Every SDK value here was verified twice: once against the Fujifilm SDK headers by
an automated checker, and once against a real GFX100S II (firmware 1.20) over USB, including
write-then-read-back round trips that restored the camera to its original settings afterwards.

## Focuser move verification

**Focus moves no longer fail on lenses that report a position offset.** The driver treated a move as
complete only when the reported position matched the commanded one to within the lens' minimum drive
step. On a GFX100S II the lens reports its position 29-38 pulses *below* whatever it was commanded,
repeatably and in both directions, on a lens whose minimum drive step is 3 — so the check could
never succeed and every move ended in a timeout after the lens had already arrived. The move now
completes when the lens reaches the target *or* stops moving, and the residual offset is logged.
This affects every focuser move, not just autofocus.

## Capture quality

- **16-bit RAW.** The plugin can now request the RAW bit depth instead of accepting whatever the
  camera dial is on. Two extra bits of well depth matter on faint nebulosity. Only the GFX bodies
  expose this control; on other models the camera setting is left untouched.
- **Lossless RAW compression.** Roughly halves the file with bit-identical data, which halves the
  USB download time between sub-exposures. The GFX100S II used for testing was shipping
  *uncompressed* frames, so this is a real saving out of the box.
- **Long Exposure NR is turned off, and called out when it is on.** With LENR enabled the camera
  shoots a matching dark after every long sub and subtracts it internally, roughly doubling the time
  per frame and applying calibration you did not choose. The test camera had it switched **on**.
- **Sensor crop mode** can be selected, so a 102MP GFX can shoot the 35mm crop for a small target
  and move far less data per frame. Defaults to leaving your framing alone.

## Control and workflow

- **A long exposure can be aborted.** `XSDK_RELEASE_CANCEL` is issued for an in-progress timed
  exposure, so a sequence can abandon a 20 minute sub for cloud or a meridian flip instead of waiting
  it out. Bodies that refuse the request fall back to the previous behaviour. Measured on a
  GFX100S II: a 15 second exposure was cancelled 2 seconds in, and the camera acknowledged in 9ms.
  A cancelled exposure still leaves one frame in the camera buffer, so that frame is now discarded
  rather than being handed to the next sub-exposure as if it belonged to it.
- **N.I.N.A. sequence instructions**, under the "Fujifilm" category:
  - *Park Fujifilm focuser at infinity*, with an optional offset, so a session starts from a known
    focus position rather than wherever the lens was left.
  - *Set Fujifilm RAW quality*, to change bit depth and compression mid-sequence.
  - *Turn off Fujifilm Long Exposure NR*.
- **Focus limiter awareness.** The plugin reads the lens' focus limiter and reports its ranges in
  metres or feet. If a limiter is set so that autofocus cannot reach infinity, it says so plainly
  rather than leaving you to work out why focus will not reach the stars.

## Live view

Measured on a GFX100S II, which turned up two reasons the preview could look poor.

- **The frame size is no longer guessed.** The plugin told N.I.N.A. the live view frame was
  1280x853 (a 3:2 estimate) while the camera actually streams 1024x768 at the Large setting - 25%
  too wide and the wrong aspect ratio - until the first frame was decoded and the real size
  replaced it. Live view dimensions vary by model, size setting and sensor shape, so nothing is
  assumed now: the size is reported as unknown until a frame has been decoded, and the decoded
  frame defines it.
- **The default quality was one the camera rejects.** `Normal` was the default, and a GFX100S II
  refuses that value outright, leaving live view on whatever quality the body happened to be set
  to. The default is now `Fine`, and if a camera rejects the requested quality the plugin falls
  back to `Fine` rather than silently accepting an unknown one. Verified on the camera: `Normal`
  returns an error, `Fine` is accepted.

For reference, measured at the Large setting: `Fine` delivers 1024x768 at roughly 200-230 KB per
frame, `Basic` the same resolution at about 46-52 KB, both at 15-18 fps.

## Adapting to the camera rather than recognising it

- **Battery reporting works on any body.** The query layout was chosen from a hardcoded list of
  model names, which silently disabled battery reporting on any model missing from it - the
  GFX100RF among them - and needed editing for every camera Fujifilm releases. The plugin now asks
  the camera, probing the candidate layouts largest first and always supplying storage for the
  largest, so the variadic call is never handed too few output pointers.
- **Auto-ISO modes are no longer offered as ISO values.** The sensitivity list returned by the SDK
  mixes real sensitivities with auto-ISO modes, encoded as non-positive numbers. A camera reported
  26 entries of which 3 were auto modes; those would have appeared in N.I.N.A. as selectable ISOs,
  handing exposure control back to the camera mid-sequence. This only became reachable once the
  capability query started working in 3.0.4.0, which is why it has not been seen before.
- A model-specific branch whose diagnostic claimed battery reporting was unavailable on one body has
  been removed, since adaptive probing makes that untrue.

## Correctness

- **Optional features are gated on what the camera advertises.** The plugin now reads the list of
  supported API codes from the body and skips anything it does not claim, instead of relying on a
  per-model table. The test camera advertised 576 API codes.
- **Removed `XSDK_GetImageSize`.** It is not an exported symbol of `XAPI.dll` at all, so any call
  would have thrown `EntryPointNotFoundException`. This was found by a new checker that reads the
  DLL's export table and compares it against every `DllImport` the plugin declares.
- **`GetCropMode` writes two values, not one.** The API parameter is the number of output values a
  call produces, and crop mode returns a mode and a status. Reading it with a single output pointer
  corrupted memory; this was caught by testing against the camera rather than by reading the code.
- `build/verify-sdk-interop.py` now checks every entry point against `XAPI.dll` and every constant,
  API code and API parameter against the SDK headers. 201 checks, run before each release.

## Verified on hardware

A GFX100S II on firmware 1.20 was driven over USB during development. Confirmed on the camera
itself, not just against the headers:

- RAW bit depth, RAW compression, Long Exposure NR and crop mode were each read, changed, read back
  to prove the change took effect, and restored to their original values.
- The camera advertised 576 API codes, and the capability gate correctly identified all four
  capture-quality controls as available.
- The camera arrived with **Long Exposure NR switched on and RAW compression off** — exactly the two
  settings this release exists to fix.
- Focus capability only returns usable values once the body is in manual focus mode, which is the
  order the plugin already used. The lens reported `INF=-1004, MOD=6995` with 333 and 365 pulses of
  over-search travel: under the pre-3.0.3.0 mapping infinity sat at position 0 with nothing beneath
  it, and under the corrected mapping it sits at position 333 within a range of 0-8697. That is the
  reported autofocus failure, reproduced and fixed on hardware.
- `SetMediaRecord(OFF)` was accepted and read back as 0x4, confirming the 3.0.4.0 constant fix.
- Two real exposures were captured and downloaded as valid RAF data. `XSDK_ReadImage` was confirmed
  to empty the in-camera buffer by itself, so the redundant delete removed in 3.0.4.0 was indeed
  redundant.
- In manual exposure mode the body advertised 68 shutter codes including every T-mode value from 2
  to 60 minutes (`64000030` through `64000180`), all of which the pre-3.0.4.0 catalog rejected as
  undocumented. It also reported bulb as unavailable while simultaneously listing the bulb shutter
  code, which is why the plugin overrides that flag.

- Lens information, battery reporting, live view, a bulb exposure and cancelling an exposure in
  progress were all exercised on the camera. Battery confirmed the eight-value protocol the plugin
  selects for this model; live view delivered JPEG frames; the bulb sequence produced a valid RAF
  even though the same body reports bulb as unsupported, which is exactly why that flag is
  overridden.

- The plugin's own decision-making classes were compiled into a probe and driven with live camera
  data, so the shipping logic was exercised rather than a parallel reimplementation: 27 checks
  covering capability gating, model-config resolution, shutter selection across 0.5s to 3600s, the
  focus travel mapping, focus limiter interpretation, battery layout discovery and the
  capture-quality plan.
- Every setting was applied across every value the camera advertises, read back, and restored: RAW
  bit depth, RAW compression, Long Exposure NR, all four crop modes, focus distance unit, manual
  focus forcing, card recording, all 23 fixed ISO values, all 67 timed shutter codes, and live view
  across every quality and size. A value a camera advertises and then refuses is reported and
  skipped rather than failing the connection, which is what the plugin does at runtime.

Not confirmed on hardware: the rotated-frame mask (the test camera reported no rotation), and the
sequence instructions running inside N.I.N.A. itself.

Nothing in this release is conditioned on a camera model. Behaviour is derived from what a body
reports - its advertised API codes and its capability lists - so a model the plugin has never seen
gets the same treatment as one it has.

# 3.0.4.0

Every change below was found by auditing the plugin against the Fujifilm SDK headers and the
official Programming Reference, and each one is backed either by a header the code contradicts or by
a failure visible in real device logs.

## Exposure

- **A sub-exposure is never silently shortened.** If a requested exposure was longer than the
  camera's longest timed shutter speed and bulb was unavailable, the driver quietly substituted the
  nearest timed speed — so a 300 second sub was exposed for 60 seconds and then written to FITS
  labelled `EXPTIME = 300`. That is invisible in the file and corrupts a stack. The request now
  fails with an explanation instead.
- **Bulb mode works again on every model.** The SDK's bulb-capability flag came back "not capable"
  on all 82 probes in the diagnostics logs, including sessions that went on to run a successful bulb
  exposure seconds later. The flag is not trustworthy, so the model configuration — which records
  that every supported body has a mechanical bulb mode — is now authoritative when the SDK denies it.
  Together with the item above, this is what kept long sub-exposures working correctly.
- **All 18 camera models now advertise a 60 minute bulb ceiling.** Five bodies (X-T4, X-H2S, X-S10,
  X-S20, X-M5) were capped at 900 seconds while their same-generation siblings allowed 3600.
  Fujifilm's published specifications give "Bulb mode: up to 60min." for all of them.
- **Corrected the fastest-shutter figure on nine models.** Seven GFX bodies claimed 1/32000s when
  their electronic shutter reaches 1/16000s, and the X-S20 carried the X-H2's 1/180000s figure along
  with the X-T5's ISO 125 floor (the X-S20's standard range starts at ISO 160). Every model's
  declared limits now correspond to shutter speeds the SDK can actually express, and a test enforces
  that.
- **Sub-exposures longer than 60 seconds can now use the camera's own timed shutter.** The shutter
  catalog stopped at 60 seconds, so every longer code the body advertised — including the whole
  T-mode series from 2 to 60 minutes — was rejected as undocumented and the exposure was pushed onto
  the bulb path. 37 missing codes were added from `XAPI.h`, covering 35s-2000s and 2-60 minutes.
- **Frames are no longer destroyed when the camera reports a rotation.** Only the low byte of
  `lFormat` is the image format; bits `0x0F00` carry the orientation, so a RAW shot with the body
  rotated arrives as `0x0601`/`0x0301`/`0x0801`. The code compared the whole value against `1`,
  concluded the frame was not RAW, deleted it, and polled until the exposure timed out.
- **The ISO capability query works.** `XSDK_CapSensitivity` was declared with four parameters where
  the SDK takes three, shifting every argument by one slot. The call reported success and returned
  zero values on every session, so the plugin always fell back to a hardcoded ISO list.
- **Removed a shutter-speed table for the GFX100S that used the wrong encoding.** SDK shutter codes
  are microseconds; that table treated them as `1/x` denominators, so SDK code `30` (1/32000s) was
  described as 1/30s. It was inert on the GFX100S but would have produced a badly wrong exposure on
  any body that reports those codes.

## Stability

- **Fixed an 8-byte heap overrun on every image download and live-view frame.**
  `XSDK_ImageInformation` is missing the trailing `hImage` handle that `XAPI.h` declares, so the
  marshaller allocated 56 bytes for a structure the SDK writes 64 bytes into. This corrupted memory
  on a hot path with no crash at the call site.
- **Corrected nine SDK error-code values.** Among other things `FORCEMODE_BUSY` was defined as the
  value that actually means `AF_TIMEOUT`. Both of the SDK's documented recoverable busy states are
  now recognised and retried instead of surfacing as hard failures.

## Camera

- **Card recording can now actually be turned off, and is a setting.** The plugin always intended to
  stop the camera writing to its own card during a session, but sent value `0`, which the SDK
  rejected as an invalid combination — so card recording stayed on. The correct value is now sent,
  and because this changes long-standing behaviour it is exposed as **Stop the camera writing to its
  memory card** (on by default). Turn it off to keep an in-camera backup of every frame.
- **A fully charged battery no longer reports 14%.** Three documented status codes were unmapped and
  fell through to a numeric fallback that treated the raw status code as a percentage.
- Removed an invented shooting-mode constant and corrected `ModeManual` in the camera configuration
  files; neither is read at runtime, but both were wrong.

## Settings and diagnostics

- **Three buttons on the settings page did nothing.** `Refresh`, `Load Capabilities` and
  `Export Diagnostics` were bound to command names that do not exist, so clicking them was silently
  ignored. Refresh and Load Capabilities were masked by side effects elsewhere; **Export Diagnostics
  was completely unreachable**, which is the one thing needed to diagnose a problem report.
- **The diagnostics export now tells you where it wrote the file.** The path was previously returned
  and discarded.
- **Settings persist when you navigate away from the options page**, not only when Save is pressed.
- README corrections: the settings page lives under **Options > Plugins**, and the focus-mode
  guidance now matches what the plugin does.

# 3.0.3.0

## Focuser positioning

This release fixes the focuser reporting inconsistent and sometimes negative positions, and
autofocus runs failing when the best focus sat close to position 0.

- **The full lens travel is now available, including the range past infinity.** `XSDK_CapFocusPos`
  reports four values that describe the focus axis: the nominal infinity and minimum-object-distance
  marks, plus the "over search" travel that extends beyond each of them. Earlier releases built the
  usable range from the two nominal marks alone and discarded the over-search values, so the travel
  past infinity was unreachable. Because that region is exactly where a lens sits when focused for
  astronomy — and further still on a full-spectrum body, whose focus falls beyond the visible-light
  infinity mark — the driver was clamping away the part of the range that matters most.
- **Positions are never negative.** A lens parked past the infinity mark produced a position below
  the advertised minimum, which surfaced in N.I.N.A. as a negative number and aborted autofocus
  runs. Positions are now reported against the true start of travel and are always within
  `0 .. MaxStep`.
- **Infinity is no longer pinned to position 0.** The infinity mark now sits at a consistent
  positive position with the over-search travel available below it, so an autofocus run has room to
  sample both sides of focus instead of hitting the bottom of the range while building its curve.
  The focuser description in N.I.N.A. shows the infinity position and how much past-infinity travel
  the attached lens reports.
- **The camera is held in manual focus while the focuser is connected.** Starting an exposure
  half-presses the shutter, and a body left in AF-S or AF-C responds by refocusing on its own,
  moving the lens away from the position N.I.N.A. set. `XSDK_SetFocusPos` is documented as setting
  the focus position for manual focus mode. The previous mode is restored on disconnect, and the
  behaviour can be turned off under Focuser in the plugin options.
- Requests that fall outside the physical travel are still clamped, but are now logged as warnings
  naming the limit that was hit, so a truncated autofocus curve is diagnosable from the log.
- Focus diagnostics now record the over-search values, the computed travel, the infinity position,
  and the camera's focus mode on connect.

Note that the Fujifilm SDK states the focus pulse count "is not absolute, but fluctuates with
temperature and a variety of other conditions". Positions are therefore relative to the capability
block the lens reports each session and are not guaranteed to be identical across power cycles; the
driver re-reads that block on every connection so the mapping stays consistent within a session.

## Verification

- Adds regression tests covering the focus travel mapping, built on focus capability values captured
  from real hardware, including the past-infinity case that previously produced negative positions
  and an autofocus sweep around the infinity mark.

# 3.0.2.0

## X-T2 compatibility

- Documents the X-T2 accurately as a legacy/experimental Shooting SDK path. The legacy `FF0002API.dll` module exposes the discovery, session, shutter/ISO, bulb, RAW-transfer, electronic-focus, and live-view entry points used by this plugin; physical-camera validation is still required.
- Keeps capture, RAW transfer, live view, and compatible electronic-lens focus available for X-T2 instead of excluding the model solely because Fujifilm's current public SDK omits it.
- Deliberately disables X-T2 battery queries. Its legacy variadic argument layout is not documented, and guessing it could corrupt the native call frame.

## Camera coverage

- Adds a GFX100RF configuration for Fujifilm's current SDK-supported 102MP fixed-lens GFX body.
- Documents that GFX ETERNA 55 is not claimed as supported by this N.I.N.A. still-camera plugin until its still RAF capture/readout behavior is verified with hardware and current SDK headers.

## Fixed

- Retries transient zero-camera discovery results and logs native runtime/model-module inventory and detection failures.
- Corrects Windows C `long` interop widths for generic properties, battery calls, and release options.
- Rejects undocumented shutter codes instead of guessing their exposure duration, separates timed and bulb capability limits, and prevents capture when the requested shutter setting was rejected.
- Guarantees bulb release on cancellation or failure and removes unintended extra delay from bulb exposure duration.
- Stops live view before still capture, prevents live view during an exposure, removes debug JPEG writes, and reliably exits camera-side live view after stream faults or disposal.
- Shares one reference-counted SDK session between camera and lens focuser; disconnecting either device no longer invalidates the other.
- Validates lens focus capability, honors N.I.N.A.'s move timeout, waits for the reported position, and propagates move failures to N.I.N.A.
- Surfaces RAW-decoding and malformed-image failures instead of returning silent black frames.
- Safely validates LibRaw active-area crop bounds before removing optical-black padding.
- Writes generated Fujifilm metadata into N.I.N.A.'s FITS/XISF metadata collection with typed values.
- Refreshes battery/lens state while idle, correctly reports a 0% battery level, and stops advertising battery support when a model's battery protocol is unavailable.
- Uses collision-resistant RAF sidecar/recovery filenames.
- Validates camera configuration files and fixes prefixed/overlapping model-name matching.
- Normalizes persisted settings and exposes the implemented live-view quality and size controls.
- Corrects SDK/runtime publish layout and fails release packaging when required runtime files are absent.
- Synchronizes manifest, assembly, camera, and focuser versions at 3.0.2.

## Cleanup and validation

- Removes unsafe long-exposure-noise-reduction probing and dead profile, bracketing, camera-spec, and XISF-encoder code.
- Adds Windows CI build/test coverage and a platform-neutral xUnit regression suite.
- The automated suite covers model/config validation, shutter mapping, safe model-specific battery signatures, settings normalization, shared-session ownership, FITS metadata typing, active-area cropping, and synthetic RGGB conversion.
