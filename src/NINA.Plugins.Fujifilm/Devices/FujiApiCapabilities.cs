using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// The set of model-dependent API codes a connected body advertises.
/// </summary>
/// <remarks>
/// <para>
/// <c>XSDK_GetDeviceInfoEx</c> returns the list of API codes the attached camera supports. That
/// list is the only reliable way to know whether an optional feature is available: the per-model
/// headers disagree with each other (for example <c>API_PARAM_SetRAWOutputDepth</c> is 1 on the
/// GFX bodies and -1, meaning unsupported, on every X body), and firmware revisions move the line.
/// </para>
/// <para>
/// Asking the camera and skipping unsupported calls is also what keeps the diagnostics log honest:
/// a call the body never claimed to support fails with <c>API_NOTFOUND</c>, which is noise that
/// hides real errors.
/// </para>
/// </remarks>
public sealed class FujiApiCapabilities
{
    private readonly HashSet<int> _apiCodes;

    public FujiApiCapabilities(IEnumerable<int> apiCodes)
    {
        _apiCodes = new HashSet<int>(apiCodes ?? Array.Empty<int>());
    }

    /// <summary>Capabilities for a camera that did not report a usable API list.</summary>
    public static FujiApiCapabilities Unknown { get; } = new(Array.Empty<int>());

    /// <summary>
    /// True when the camera did not report any API codes. Callers should fall back to attempting
    /// the call and handling failure, rather than assuming the feature is missing.
    /// </summary>
    public bool IsEmpty => _apiCodes.Count == 0;

    public int Count => _apiCodes.Count;

    /// <summary>
    /// Whether the camera advertises the given API code. An empty list means the camera did not
    /// tell us, so report support and let the call itself decide.
    /// </summary>
    public bool Supports(int apiCode) => IsEmpty || _apiCodes.Contains(apiCode);

    /// <summary>Whether every one of the given API codes is advertised.</summary>
    public bool SupportsAll(params int[] apiCodes) => apiCodes.All(Supports);

    /// <summary>
    /// Whether the camera positively confirmed the API code. Unlike <see cref="Supports"/> this
    /// returns false for an unknown list, so it suits reporting rather than gating.
    /// </summary>
    public bool Confirms(int apiCode) => _apiCodes.Contains(apiCode);

    public override string ToString() => IsEmpty ? "unknown" : $"{_apiCodes.Count} API codes";
}
