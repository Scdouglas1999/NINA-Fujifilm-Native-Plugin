# 3.2.2.0

## Every download returned the previous exposure

Reported by a GFX100 II user with a full night of logs, closing the "first image after a slew
shows the old field" problem.

The symptom was subtle. Normal imaging looked fine, but the first exposure after any large mount
movement plate-solved to the field the mount had just left. With Slew & Center that produced a
large, wrong correction. After an automatic meridian flip it was unmistakable: the first
post-flip 2 s centering frame solved with the pre-flip position angle (140.7°) while every later
frame solved at 321.4°, and the recenter routine chased the lagged solves until it gave up after
ten attempts.

The N.I.N.A. log proved the frame was not merely old but a different exposure. Lossless RAF size
tracks image content, and the plugin logs the size and sampled statistics of every frame:

| Requested | Bytes downloaded | Sampled mean | Sampled max |
| --- | --- | --- | --- |
| Last 60 s light before the flip | 75,789,744 | 308.9 | 65,280 |
| First "2 s" frame after the flip | 75,790,896 | 308.7 | 65,255 |
| Every later 2 s centering frame | ~55,000,000 | ~260 | < 21,000 |
| First "60 s" light after centering | 55,994,112 | 259.3 | 1,241 |

The same swap appears at all nine exposure-length changes in that log, from the first frame of
the session to the last. Every download, all night, returned the frame captured by the previous
trigger.

The cause is in the plugin, not N.I.N.A., the SDK or the camera. `CaptureRawAsync` fires the
trigger, waits the exposure length, then reads whatever frame is at the head of the camera's
buffer. The buffer is first-in first-out and nothing ever checked it was empty before the trigger.
The connect log for that session reported one captured frame already sitting in the buffer
(`Buffer capacity: 1/33`; the SDK defines that first value as the number of frames the camera is
holding), so from the first exposure onward every download was one frame behind. With media
recording set to OFF, a shutter press on the body or an exposure a previous session never
collected is enough to leave that frame there, and until now only a cancelled exposure ever
cleared it.

- **The buffer is drained on connect.** Any frame the camera is holding when the session opens is
  deleted and logged as `Camera buffer holds N frame(s) of M on connect; discarding them.`
- **The buffer is drained before every trigger.** Whatever is in the buffer when an exposure
  starts cannot be that exposure's frame.
- **The buffer is checked after every download.** If frames remain once the exposure's own frame
  has been read, they are discarded with a warning, so a body that produces more than one frame
  per trigger cannot re-introduce the lag.
- **The connect log line now says what it means.** `Camera buffer: N frame(s) pending of M`
  replaces the ambiguous `Buffer capacity: N/M`.

For anyone who imaged with an affected session: every saved frame is genuine, but each was
captured one exposure earlier than its header claims, and the first frame after each change of
exposure length has the previous length. In the reported session two "60 s" lights are really 2 s
frames. Check the first light after any framing, centering or flip.

## Testing

No Fujifilm hardware was attached for this release. The fix is verified against the reporter's
log, which shows the stale frame present at connect and the one-frame lag at every exposure-length
change, and against the SDK reference for `XSDK_GetBufferCapacity` (4.1.8.5), `XSDK_ReadImage` and
`XSDK_DeleteImage`. The drain path itself is the one 3.1.0.0 introduced for cancelled exposures,
now also run on connect, before each trigger and after each download. The existing test suite
passes on Windows CI. The reporter's proposed check, a plain slew between two fields followed by
two consecutive exposures, is the confirmation to run on hardware: with this release both solve to
the new field.

## Upgrading

Nothing to reconfigure. Install over 3.2.1.0 with the installer, or replace the plugin folder
contents with the manual-install zip. Close N.I.N.A. first. On the first connection after
upgrading, expect a log line reporting how many stale frames were discarded.
