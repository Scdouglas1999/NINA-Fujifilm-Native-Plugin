# Hardware probe

A standalone console app that drives a real Fujifilm camera through the SDK and reports what it
finds. It exists because the plugin targets Windows and WPF, which makes the camera hard to reach
from a test run, while the SDK itself ships a Linux build that can be driven directly.

It was used to verify the 3.1.0.0 release against a GFX100S II on firmware 1.20, and it caught two
defects that reading the code did not: `GetCropMode` writes two output values rather than one, and
the focuser's move verification required an exact position match that the hardware never produces.

## Running it

```
tar xzf <SDK>/REDISTRIBUTABLES/Linux/Linux64PC.tar.gz -C /tmp/fujisdk
dotnet build -c Release
cp /tmp/fujisdk/Linux64PC/*.so /tmp/fujisdk/Linux64PC/XSDK.DAT bin/Release/net8.0/
cd bin/Release/net8.0 && ln -sf XAPI.so libXAPI.so
LD_LIBRARY_PATH=$PWD dotnet probe.dll
```

## What it checks

Device info and the advertised API code list; RAW bit depth, RAW compression, Long Exposure NR and
crop mode (read, write, read back, restore); focus mode, focus capability, focus limiter and a focus
move with settle characterisation; shutter and ISO enumeration; media record; a timed exposure and
download; lens info; battery; live view; a bulb exposure; and cancelling an exposure in progress.

Every property it changes is restored afterwards.

## Caveat

The SDK's C `long` is 8 bytes on Linux and 4 bytes on Windows, so this probe declares those
parameters as 64-bit while the plugin correctly uses 32-bit. It therefore validates SDK behaviour,
values and call sequences - not the plugin's Windows marshalling, which `build/verify-sdk-interop.py`
covers instead.
