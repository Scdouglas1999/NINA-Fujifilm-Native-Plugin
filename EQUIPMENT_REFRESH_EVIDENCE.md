# X-T4 equipment refresh crash evidence

On 2026-08-27 at 14:54:22 local time, N.I.N.A. 3.2.0.9001 with Fujifilm plugin
3.1.2.0 scanned equipment while an X-T4 camera session was open. Its log ends
immediately after this line:

> `[Fuji:Interop] ENUM:0 is already connected; describing it from the open session instead of reopening.`

Source: `%LOCALAPPDATA%\NINA\Logs\20260827-144716-3.2.0.9001.12872-202608.log`,
lines 319-321.

Windows Application events at the same second identify the crash:

- **.NET Runtime, event 1026:** unhandled `System.AccessViolationException`
  at `FujifilmSdkWrapper.XSDK_GetDeviceInfoEx`, called from
  `FujifilmInterop.DetectCamerasAsync`.
- **Application Error, event 1000:** `NINA.exe` exited with exception code
  `0xc0000005`; report ID `694cce23-9b1f-45a4-a537-b643e2189ecd`.

With the changed discovery path, a later equipment scan logged that it reused
the cached descriptor, completed detection, and N.I.N.A. reported one
Fujifilm camera. Source:
`%LOCALAPPDATA%\NINA\Logs\20260827-151910-3.2.0.9001.35924-202608.log`,
lines 879-884.

The full local logs contain device details. These excerpts and event details
are the relevant evidence for review.
