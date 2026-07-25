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
