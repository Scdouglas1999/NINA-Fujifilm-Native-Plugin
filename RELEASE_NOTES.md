# 3.2.2.0

## The camera was handing back the previous image

If you use Slew & Center or automatic meridian flips, update. This one matters.

A GFX100 II user emailed me about something odd: the first image after a big mount move plate-solved
to where the mount used to be, not where it had just gone. With Slew & Center that produced a large
correction in the wrong direction. After an automatic meridian flip it was impossible to miss. The
first frame after the flip solved at the old position angle of 140.7°, and every frame after that
solved at 321.4°, the 180° flip you would expect. N.I.N.A. trusted the bad solve, chased it through
ten slew attempts, and gave up on centering.

He sent a full night of logs, and they showed something worse than an occasional stale frame. Every
download that night handed back the image from the previous exposure.

You can see it because a lossless RAF changes size with what is in it, and the plugin logs the size
and average brightness of every frame it downloads. Just before the flip, N.I.N.A. asked for a
2 second frame to centre with. What came back was 75.8 MB averaging 309 ADU, which is a dead match
for the 60 second light taken immediately before it, and nothing like the 55 MB and 260 ADU of the
2 second frames that followed. The same swap turns up at all nine points that night where the
exposure length changed, from the first frame of the session to the last.

This one is mine, not N.I.N.A.'s and not Fujifilm's. The plugin fires the shutter, waits out the
exposure, then reads whatever image is sitting at the front of the camera's internal buffer. That
buffer is first in, first out, and nothing ever checked it was empty before firing. His log shows
one image already in there the moment the camera connected. The line reads `Buffer capacity: 1/33`,
and that first number is how many pictures the camera is holding, not how much room is left. One
stray image in the buffer and every download after it is one behind, all night.

It does not take much to leave one there. Press the shutter on the body while the plugin has card
recording switched off, or close N.I.N.A. without collecting the last frame, and it sits in the
buffer waiting for someone to read it. Until now the only thing that ever cleared it was cancelling
an exposure.

What has changed:

- The buffer is emptied when the camera connects. Anything the body was still holding gets thrown
  away, and the log says how many frames that was.
- It is emptied again right before every shutter release, so whatever is in there cannot be mistaken
  for the frame you are about to take.
- It is checked once more after every download. If anything is left, it gets dropped with a warning
  in the log instead of being handed to the next exposure.
- The connect line now reads `Camera buffer: 1 frame(s) pending of 33`. The old wording looked like
  free space, which it never was.

### If you imaged on an earlier version

Your frames are all real images. Each one is just an exposure older than its filename says. Where it
actually bites is the first frame after you change exposure length, because that one has the old
length. In the session I was sent, two files saved as 60 second lights are really 2 second frames:
the first light after framing, and the first light after the flip. Worth checking the first sub
after any framing, centering or flip run.

## Testing

Windows CI is green on the existing test suite.

The fix itself is verified against that night's log, where you can see the stale frame present at
connect and the one frame lag at every change of exposure length, and against the Fujifilm SDK
reference for the buffer, read and delete calls. The draining code is not new. It is what the plugin
already used after a cancelled exposure, now run in the three places that actually needed it.

No camera was attached for this release, so the real confirmation has to happen on hardware, and the
test the reporter suggested is the right one: solve a field, slew somewhere else, take two frames
back to back without letting anything sync or correct, and solve both. On this release both should
come back as the new field.

## Upgrading

Nothing to reconfigure. Install over 3.2.1.0, or drop the manual-install zip over your plugin
folder. Close N.I.N.A. first. The first time you connect after updating you may see a line about
discarding a stale frame, which is the fix doing its job.
