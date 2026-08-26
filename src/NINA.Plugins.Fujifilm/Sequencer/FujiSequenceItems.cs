using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Plugins.Fujifilm.Devices;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Settings;
using NINA.Sequencer.SequenceItem;

namespace NINA.Plugins.Fujifilm.Sequencer;

/// <summary>
/// Shared plumbing for the Fujifilm sequence instructions: they all need the connected Fujifilm
/// device, and they should fail with a sentence a user can act on when it is missing.
/// </summary>
public abstract class FujiSequenceItemBase : SequenceItem
{
    protected FujiSequenceItemBase(IFujiEquipmentRegistry registry)
    {
        Registry = registry;
    }

    protected FujiSequenceItemBase(FujiSequenceItemBase copyMe)
        : this(copyMe.Registry)
    {
        CopyMetaData(copyMe);
    }

    protected IFujiEquipmentRegistry Registry { get; }

    protected FujiCamera RequireCamera() =>
        Registry.ConnectedCamera
        ?? throw new SequenceEntityFailedException(
            "No Fujifilm camera is connected. Connect the Fujifilm camera before this instruction runs.");

    protected FujiFocuser RequireFocuser() =>
        Registry.ConnectedFocuser
        ?? throw new SequenceEntityFailedException(
            "No Fujifilm focuser is connected. Connect the Fujifilm focuser before this instruction runs.");
}

/// <summary>
/// Moves the lens to its infinity mark. Handy as the first instruction of a session so autofocus
/// starts from a sane place rather than wherever the lens happened to be left.
/// </summary>
[ExportMetadata("Name", "Park Fujifilm focuser at infinity")]
[ExportMetadata("Description", "Moves the Fujifilm lens to the infinity mark reported by the lens, optionally offset by a number of steps.")]
[ExportMetadata("Icon", "PluginSVG")]
[ExportMetadata("Category", "Fujifilm")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public class ParkFujiFocuserAtInfinity : FujiSequenceItemBase
{
    private int _offsetSteps;

    [ImportingConstructor]
    public ParkFujiFocuserAtInfinity(IFujiEquipmentRegistry registry) : base(registry) { }

    private ParkFujiFocuserAtInfinity(ParkFujiFocuserAtInfinity copyMe) : base(copyMe)
    {
        OffsetSteps = copyMe.OffsetSteps;
    }

    /// <summary>
    /// Steps to add to the infinity position. Positive moves toward close focus; negative moves
    /// into the past-infinity travel, which is where a full-spectrum body usually focuses.
    /// </summary>
    [JsonProperty]
    public int OffsetSteps
    {
        get => _offsetSteps;
        set { _offsetSteps = value; RaisePropertyChanged(); }
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var focuser = RequireFocuser();
        var target = focuser.InfinityPosition + OffsetSteps;

        if (target < 0 || target > focuser.FocusRange)
        {
            throw new SequenceEntityFailedException(
                $"Infinity is at position {focuser.InfinityPosition} and the offset of {OffsetSteps} would move to {target}, " +
                $"which is outside the lens travel of 0 to {focuser.FocusRange}.");
        }

        await focuser.MoveAsync(target, token).ConfigureAwait(false);
    }

    public override object Clone() => new ParkFujiFocuserAtInfinity(this);

    public override string ToString() => $"Category: {Category}, Item: {nameof(ParkFujiFocuserAtInfinity)}, Offset: {OffsetSteps}";
}

/// <summary>
/// Applies RAW bit depth and compression to the connected body mid-sequence.
/// </summary>
[ExportMetadata("Name", "Set Fujifilm RAW quality")]
[ExportMetadata("Description", "Sets the RAW bit depth and compression on the connected Fujifilm camera.")]
[ExportMetadata("Icon", "PluginSVG")]
[ExportMetadata("Category", "Fujifilm")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public class SetFujiRawQuality : FujiSequenceItemBase
{
    private RawBitDepthPreference _bitDepth = RawBitDepthPreference.SixteenBit;
    private RawCompressionPreference _compression = RawCompressionPreference.Lossless;

    [ImportingConstructor]
    public SetFujiRawQuality(IFujiEquipmentRegistry registry) : base(registry) { }

    private SetFujiRawQuality(SetFujiRawQuality copyMe) : base(copyMe)
    {
        BitDepth = copyMe.BitDepth;
        Compression = copyMe.Compression;
    }

    [JsonProperty]
    public RawBitDepthPreference BitDepth
    {
        get => _bitDepth;
        set { _bitDepth = value; RaisePropertyChanged(); }
    }

    [JsonProperty]
    public RawCompressionPreference Compression
    {
        get => _compression;
        set { _compression = value; RaisePropertyChanged(); }
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var camera = RequireCamera();

        var depth = FujiCaptureQualityPlan.ToSdkValue(BitDepth);
        if (depth != 0 && !camera.TrySetCaptureProperty(
                FujifilmSdkWrapper.API_CODE_SetRAWOutputDepth, depth, "RAW bit depth", out var depthError))
        {
            throw new SequenceEntityFailedException(depthError);
        }

        var compression = FujiCaptureQualityPlan.ToSdkValue(Compression);
        if (compression != 0 && !camera.TrySetCaptureProperty(
                FujifilmSdkWrapper.API_CODE_SetRAWCompression, compression, "RAW compression", out var compressionError))
        {
            throw new SequenceEntityFailedException(compressionError);
        }

        return Task.CompletedTask;
    }

    public override object Clone() => new SetFujiRawQuality(this);

    public override string ToString() => $"Category: {Category}, Item: {nameof(SetFujiRawQuality)}, Depth: {BitDepth}, Compression: {Compression}";
}

/// <summary>
/// Fails the sequence if the camera's Long Exposure NR is on, so a night is not silently spent
/// shooting at half rate with in-camera dark subtraction applied.
/// </summary>
[ExportMetadata("Name", "Turn off Fujifilm Long Exposure NR")]
[ExportMetadata("Description", "Turns off the camera's Long Exposure Noise Reduction, which otherwise doubles the time per sub-exposure and applies in-camera dark subtraction.")]
[ExportMetadata("Icon", "PluginSVG")]
[ExportMetadata("Category", "Fujifilm")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public class DisableFujiLongExposureNR : FujiSequenceItemBase
{
    [ImportingConstructor]
    public DisableFujiLongExposureNR(IFujiEquipmentRegistry registry) : base(registry) { }

    private DisableFujiLongExposureNR(DisableFujiLongExposureNR copyMe) : base(copyMe) { }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var camera = RequireCamera();

        if (!camera.TrySetCaptureProperty(
                FujifilmSdkWrapper.API_CODE_SetLongExposureNR,
                FujifilmSdkWrapper.SDK_OFF,
                "Long exposure NR",
                out var error))
        {
            throw new SequenceEntityFailedException(error);
        }

        return Task.CompletedTask;
    }

    public override object Clone() => new DisableFujiLongExposureNR(this);

    public override string ToString() => $"Category: {Category}, Item: {nameof(DisableFujiLongExposureNR)}";
}

/// <summary>Sets the f-number of an attached electronic lens.</summary>
[ExportMetadata("Name", "Set Fujifilm aperture")]
[ExportMetadata("Description", "Switches the camera to Manual exposure mode, then sets and verifies the attached electronic lens aperture.")]
[ExportMetadata("Icon", "PluginSVG")]
[ExportMetadata("Category", "Fujifilm")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public class SetFujiAperture : FujiSequenceItemBase
{
    private double _fNumber = 2.8;

    [ImportingConstructor]
    public SetFujiAperture(IFujiEquipmentRegistry registry) : base(registry) { }

    private SetFujiAperture(SetFujiAperture copyMe) : base(copyMe)
    {
        FNumber = copyMe.FNumber;
    }

    [JsonProperty]
    public double FNumber
    {
        get => _fNumber;
        set { _fNumber = value; RaisePropertyChanged(); }
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var camera = RequireCamera();
        if (!camera.TrySetAperture(FNumber, out var error))
        {
            throw new SequenceEntityFailedException(error);
        }

        return Task.CompletedTask;
    }

    public override object Clone() => new SetFujiAperture(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(SetFujiAperture)}, Aperture: f/{FNumber:0.0#}";
}
