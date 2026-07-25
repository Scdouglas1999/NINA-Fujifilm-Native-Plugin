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

It compiles the plugin's own model-independent classes - `FocusTravelMap`,
`FujifilmShutterSpeedCatalog`, `FujifilmSensitivityCatalog`, `FujiApiCapabilities`,
`FujiCaptureQualityPlan`, `FocusLimiterState`, `FocusSettleTracker`, `FujifilmBatteryProtocol`,
`CameraModelRules` - and drives them with data read from the attached camera, so the shipping logic
is what gets exercised rather than a parallel reimplementation.

On top of that it exercises the SDK directly: device info and the advertised API code list; RAW bit
depth, RAW compression, Long Exposure NR and crop mode; focus mode, focus capability, focus limiter
and a focus move with settle characterisation; shutter and ISO enumeration; media record; timed and
bulb exposures with download; lens info; battery; live view across every quality and size; and
cancelling an exposure in progress.

Every setting it changes is applied, read back, and restored to the value it found.

## No camera model is named

The probe derives everything from what the camera reports - its advertised API codes and its `Cap*`
lists - so on a body offering fewer options it simply tries fewer combinations. A camera that
advertises a value and then refuses it is reported rather than treated as a failure, because that is
exactly what the plugin has to cope with.

## Caveat

The SDK's C `long` is 8 bytes on Linux and 4 bytes on Windows, so this probe declares those
parameters as 64-bit while the plugin correctly uses 32-bit. It therefore validates SDK behaviour,
values and call sequences - not the plugin's Windows marshalling, which `build/verify-sdk-interop.py`
covers instead.
