# 3.2.2.0

## Images arrived one exposure behind

Every image the plugin downloaded was the frame from the previous exposure. It affects every session
on every body and has been there since at least 3.1.1, but during normal imaging it is invisible,
because consecutive subs of the same target look alike. It shows up as soon as the mount moves: the
first plate solve after a slew or a meridian flip describes the field the mount was on before the
move, and N.I.N.A. corrects against a position it has already left.

A GFX100 II user hit this with Slew & Center, where the stale solve produced a large correction in
the wrong direction, and then again on an automatic meridian flip. The flip is the clearest case in
his logs. The first frame after it solved at position angle 140.7°, matching his pre-flip framing,
while the nine that followed solved at 321.4°. N.I.N.A. acted on the 140.7° solve, synced, slewed,
and went on correcting against solves that were each one frame out of date until it cancelled
centering after ten attempts.

Frame sizes in the log confirm the returned image was a different exposure and not a delayed copy of
the right one. Lossless RAF size varies with image content, and the plugin logs the size and mean
value of every download. The 2 second centering frame requested after the flip came back as
75,790,896 bytes with a mean of 308.7, within 0.2% of the 60 second light that preceded it, where
the 2 second frames that followed it were around 55,000,000 bytes and 260. The same substitution
happens at all nine points in that session where exposure length changed.

The cause is in this plugin: `CaptureRawAsync` releases the shutter, waits out the exposure, then
reads the image at the head of the camera's internal buffer, which is first in, first out, and
nothing checked that the buffer was empty before releasing. His connect log records
`Buffer capacity: 1/33`, where the first number is the count of captured frames the camera is
holding rather than the room left in it, so one frame was already queued before N.I.N.A. took its
first exposure, and every download after that was displaced by one. A shutter press on the body
while card recording is disabled, or a session that ends without collecting the last frame, is
enough to leave one there. Until now the only thing that cleared it was cancelling an exposure.

3.2.2.0 empties the buffer at connect and again immediately before each shutter release, and checks
it after each download, discarding and logging whatever is left. The connect line now reads
`Camera buffer: 1 frame(s) pending of 33`, since the old wording read as free space.

### Checking existing data

Frames captured on earlier versions are valid images, offset by one exposure from their headers. The
visible effect is at changes of exposure length, where the first frame at the new length holds the
old one. Two files saved as 60 second lights in the reported session are 2 second frames: the first
light after framing, and the first light after the flip. Check the first sub after any framing,
centering or flip run.

## Testing

Windows CI passes the existing suite. The fix is verified against the reporter's log, which shows
the queued frame at connect and the one exposure offset at every change of exposure length, and
against the SDK reference for `XSDK_GetBufferCapacity`, `XSDK_ReadImage` and `XSDK_DeleteImage`. The
drain routine is unchanged from the one 3.1.0.0 added for cancelled exposures and now runs at
connect, before each release, and after each download.

No camera was attached for this release. Confirmation on hardware is the test the reporter proposed:
solve a field, slew to another, take two exposures with no sync or corrective slew between them, and
solve both. Both should report the second field.

## Upgrading

No configuration changes. Install over 3.2.1.0, or extract the manual-install zip over the plugin
folder, with N.I.N.A. closed. The first connection after upgrading may log a discarded frame.
