# 3.2.1.0

## Equipment refresh no longer crashes N.I.N.A.

Contributed by Roman Malizderskyi (@roma-derski), closing the X-T4 equipment refresh crash.

Scanning for equipment while a Fujifilm camera was connected could terminate N.I.N.A. outright -
not an error dialog, not a disconnected camera, the process gone. Discovery called
`XSDK_GetDeviceInfoEx` on the live session handle to describe the camera it already had open, and
the native SDK can access-violate when that query races other operations on an active session. The
contributor's crash was recorded on N.I.N.A. 3.2.0.9001 with plugin 3.1.2.0 and an X-T4: the
N.I.N.A. log ends on `ENUM:0 is already connected; describing it from the open session instead of
reopening.`, and Windows logged `NINA.exe terminated due to an unhandled
System.AccessViolationException` inside `XSDK_GetDeviceInfoEx`, exception code `0xc0000005`.

The fix removes that query entirely. Identity is now recorded during the discovery that precedes
connection and reused afterwards, so nothing calls device-info APIs on a handle that is in use.

- **Discovery never inspects a live session.** A connected camera is described from the descriptor
  captured before it was opened. If no cached descriptor exists, the device is skipped and the
  reason logged, rather than inspected unsafely.
- **The descriptor cache is process-wide.** 3.1.2.0 established that several `FujifilmInterop`
  instances coexist in one N.I.N.A. session, so a per-instance cache would miss. The cache is
  static and keyed by device id.
- **Equipment refresh reuses the connected camera's own descriptor.** While a camera is connected,
  the camera factory returns the descriptor discovery produced, including for the Fujifilm focuser
  chooser. The camera does not appear to vanish and is not re-enumerated underneath an open
  session.

Worth noting for anyone who hit this on 3.1.2.0: that release introduced the device-info query to
stop an equipment rescan from making a connected camera disappear. It fixed the disappearance and
traded it for this crash. Both are now handled the same way, by never touching the SDK for
information already known.

## The right camera identity survives a refresh

A follow-up fix from the maintainer. The connection descriptor is now stored before the session is
published, so `IsConnected` never becomes true while the plugin would answer with an empty name or
with the previous camera's. Connection metadata loads after the handle opens, and a refresh landing
in that window used to report whatever metadata still held - which on reconnect could select the
wrong sensor configuration for the body actually attached.

## Testing

193 tests pass on Windows CI: the existing 190, plus three regression cases added for this fix that
run the real camera and factory against a fake SDK session - a refresh before metadata
initialization, stale metadata across a body change, and discovery resuming after disconnect. The
tests are a new Windows-only project, so CI now runs both test projects.

The native crash itself is verified by the contributor's X-T4 evidence above; the maintainer has no
Fujifilm hardware, and no camera was attached for this release. The post-fix scan on the
contributor's X-T4 logs `ENUM:0 is already connected; reused its cached descriptor.` followed by
`Found 1 Fujifilm Cameras`.

## Upgrading

Nothing to reconfigure. Install over 3.2.0.0 with the installer, or replace the plugin folder
contents with the manual-install zip. Close N.I.N.A. first.
