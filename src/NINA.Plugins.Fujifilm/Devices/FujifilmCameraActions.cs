using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>
/// Implements the public, ASCOM-style custom actions exposed by the Fujifilm camera.
/// Keep this transport layer independent of the SDK so every caller uses the same
/// aperture implementation and the wire contract can be tested without hardware.
/// </summary>
public static class FujifilmCameraActions
{
    public const string SetAperture = "Camera:SetAperture";

    private static readonly IReadOnlyList<string> Actions =
        Array.AsReadOnly(new[] { SetAperture });

    public static IReadOnlyList<string> SupportedActions => Actions;

    public static string Execute(
        string actionName,
        string actionParameters,
        bool connected,
        Func<double, ApertureSetResult> setAperture)
    {
        if (!string.Equals(actionName, SetAperture, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Camera action '{actionName}' is not supported.");
        }

        if (!connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }

        ArgumentNullException.ThrowIfNull(setAperture);
        var requested = ParseFNumber(actionParameters);
        var result = setAperture(requested);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error)
                    ? $"The camera could not set aperture f/{requested:0.0#}."
                    : result.Error);
        }

        if (!double.IsFinite(result.AppliedFNumber) || result.AppliedFNumber <= 0)
        {
            throw new InvalidOperationException("The camera did not return a valid aperture for verification.");
        }

        return JsonSerializer.Serialize(new SetApertureResponse(requested, result.AppliedFNumber));
    }

    private static double ParseFNumber(string actionParameters)
    {
        if (string.IsNullOrWhiteSpace(actionParameters))
        {
            throw new ArgumentException(
                "Camera:SetAperture requires JSON parameters such as {\"fNumber\":2.8}.",
                nameof(actionParameters));
        }

        try
        {
            using var document = JsonDocument.Parse(actionParameters);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidParameters();
            }

            double? fNumber = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "fNumber", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (fNumber.HasValue || property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDouble(out var value))
                {
                    throw InvalidParameters();
                }

                fNumber = value;
            }

            if (!fNumber.HasValue || !double.IsFinite(fNumber.Value) || fNumber.Value <= 0)
            {
                throw InvalidParameters();
            }

            return fNumber.Value;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Camera:SetAperture parameters must be valid JSON containing a positive numeric fNumber.",
                nameof(actionParameters),
                ex);
        }
    }

    private static ArgumentException InvalidParameters() => new(
        "Camera:SetAperture parameters must contain exactly one positive numeric fNumber.",
        "actionParameters");

    private sealed record SetApertureResponse(
        [property: JsonPropertyName("requestedFNumber")] double RequestedFNumber,
        [property: JsonPropertyName("appliedFNumber")] double AppliedFNumber);
}

public readonly record struct ApertureSetResult(
    bool Success,
    string Error,
    double AppliedFNumber);
