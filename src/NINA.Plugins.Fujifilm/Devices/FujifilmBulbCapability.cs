using System;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Decides whether bulb is available, and what the longest exposure the camera can be asked for is.
/// </summary>
/// <remarks>
/// <para>
/// This exists because getting it wrong is not obvious from the outside: it shows up only as the
/// maximum exposure N.I.N.A. offers, silently capped. Version 3.0.2.0 changed the resolution to
/// trust the SDK's bulb flag alone, and that flag reports "not capable" on every body observed so
/// far - 82 of 82 probes across two cameras, including sessions that went on to run a successful
/// bulb exposure moments later, and one that reported the bulb shutter code in its own supported
/// list while denying bulb support. The maximum exposure therefore collapsed to the longest timed
/// shutter speed, which was 60 seconds.
/// </para>
/// <para>
/// Every camera this plugin supports has a mechanical bulb mode, which is what the model
/// configuration records, so the configuration is authoritative when the SDK denies it.
/// </para>
/// </remarks>
public static class FujifilmBulbCapability
{
    /// <summary>Fallback ceiling when a model configuration does not give one.</summary>
    public const double DefaultBulbMaximumSeconds = 3600.0;

    /// <summary>
    /// Whether bulb should be treated as available.
    /// </summary>
    /// <param name="sdkReportedBulbCapable">The flag from XSDK_CapShutterSpeed.</param>
    /// <param name="modelDefaultBulbCapable">DefaultBulbCapable from the model configuration.</param>
    public static bool Resolve(bool sdkReportedBulbCapable, bool? modelDefaultBulbCapable) =>
        sdkReportedBulbCapable || modelDefaultBulbCapable == true;

    /// <summary>
    /// The longest exposure that can be requested: the configured bulb ceiling when bulb is
    /// available, otherwise the longest timed shutter speed the camera advertises.
    /// </summary>
    public static double ResolveMaximumExposureSeconds(
        bool bulbCapable, double? configuredBulbMaximumSeconds, double timedMaximumSeconds)
    {
        if (!bulbCapable)
        {
            return timedMaximumSeconds;
        }

        var ceiling = configuredBulbMaximumSeconds is > 0
            ? configuredBulbMaximumSeconds.Value
            : DefaultBulbMaximumSeconds;

        // A body whose timed shutter reaches further than the configured bulb ceiling should not be
        // artificially limited by it.
        return Math.Max(ceiling, timedMaximumSeconds);
    }
}
