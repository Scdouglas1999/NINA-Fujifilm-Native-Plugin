using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Imaging;
using NINA.Plugins.Fujifilm.Settings;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Interop;

public interface ILibRawAdapter
{
    Task<LibRawResult> ProcessRawAsync(byte[] buffer, CancellationToken cancellationToken);
}

[Export(typeof(ILibRawAdapter))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class LibRawAdapter : ILibRawAdapter
{
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly IProfileService _profileService;
    private readonly ICameraModelCatalog _catalog;
    private readonly IFujiSettingsProvider _settingsProvider;

    [ImportingConstructor]
    public LibRawAdapter(
        IFujifilmDiagnosticsService diagnostics,
        IProfileService profileService,
        ICameraModelCatalog catalog,
        IFujiSettingsProvider settingsProvider)
    {
        _diagnostics = diagnostics;
        _profileService = profileService;
        _catalog = catalog;
        _settingsProvider = settingsProvider;
    }

    public async Task<LibRawResult> ProcessRawAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (buffer == null || buffer.Length == 0)
        {
            _diagnostics.RecordEvent("LibRaw", "Empty buffer provided; skipping processing.");
            return LibRawResult.FromFailure(LibRawProcessingStatus.InvalidBuffer, null);
        }

        return await Task.Run(() =>
        {
            RawProcessingResult processed;

            // Get the demosaic algorithm from settings (enum value maps directly to LibRaw algorithm)
            var demosaicAlgorithm = (int)_settingsProvider.Settings.PreviewDemosaicQuality;

            try
            {
                processed = RawProcessor.ProcessRawBufferWithMetadata(buffer, demosaicAlgorithm);
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("LibRaw", $"RawProcessor threw exception: {ex.GetType().Name} - {ex.Message}");
                _diagnostics.RecordEvent("LibRaw", $"Exception stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _diagnostics.RecordEvent("LibRaw", $"Inner exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                }
                var rafPath = TryPersistRaf(buffer);
                return LibRawResult.FromFailure(LibRawProcessingStatus.UnhandledException, rafPath);
            }

            if (!processed.Success)
            {
                var rafPath = processed.RafSidecarPath ?? TryPersistRaf(buffer);
                processed.RafSidecarPath = rafPath;
            }

            _diagnostics.RecordEvent("LibRaw", $"LibRaw status={processed.Status} size={processed.Width}x{processed.Height} pattern={processed.ColorFilterPattern} ({processed.PatternWidth}x{processed.PatternHeight}) sidecar={(processed.RafSidecarPath ?? "none")}");
            
            if (!string.IsNullOrEmpty(processed.ErrorMessage))
            {
                _diagnostics.RecordEvent("LibRaw", $"Error details: {processed.ErrorMessage}");
            }
            
            if (processed.BayerData != null && processed.BayerData.Length > 0)
            {
                var sampleCount = Math.Min(processed.BayerData.Length, 1000);
                var allZeros = processed.BayerData.Take(sampleCount).All(value => value == 0);
                if (allZeros)
                {
                    _diagnostics.RecordEvent("LibRaw", $"WARNING: First {sampleCount} Bayer pixels are all zeros.");
                }
            }
            else if (processed.DebayeredRgb == null || processed.DebayeredRgb.Length == 0)
            {
                _diagnostics.RecordEvent("LibRaw", "WARNING: LibRaw returned neither Bayer nor RGB image data.");
            }

            return MapToLibRawResult(processed);
        }, cancellationToken).ConfigureAwait(false);
    }

    private string? TryPersistRaf(byte[] buffer)
    {
        try
        {
            // Use NINA's configured image directory
            var imageDirectory = _profileService.ActiveProfile.ImageFileSettings.FilePath;
            Directory.CreateDirectory(imageDirectory);
            
            // Generate filename with timestamp (basic pattern - no exposure/ISO info available here)
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"Fuji_{timestamp}_{Guid.NewGuid():N}_recovery.raf";
            
            var path = Path.Combine(imageDirectory, fileName);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(buffer, 0, buffer.Length);
            }
            
            _diagnostics.RecordEvent("LibRaw", $"Saved RAF recovery file to: {path}");
            return path;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("LibRaw", $"Failed to save RAF recovery file: {ex.Message}");
            return null;
        }
    }

    private LibRawResult MapToLibRawResult(RawProcessingResult processed)
    {
        var (croppedData, croppedWidth, croppedHeight, cropped) = BayerPaddingCropper.Crop(
            processed.BayerData,
            processed.Width,
            processed.Height,
            processed.ActiveLeft,
            processed.ActiveTop,
            processed.ActiveWidth,
            processed.ActiveHeight);
        
        _diagnostics.RecordEvent("LibRaw", cropped
            ? $"Cropped optical black padding: {processed.Width}x{processed.Height} → {croppedWidth}x{croppedHeight}, margins left={processed.ActiveLeft}, top={processed.ActiveTop}"
            : $"No Bayer cropping applied: LibRaw={processed.Width}x{processed.Height}, BayerLen={croppedData.Length}");
        
        return new LibRawResult(
            BayerData: croppedData,
            Width: croppedWidth,
            Height: croppedHeight,
            ColorFilterPattern: processed.ColorFilterPattern,
            PatternWidth: processed.PatternWidth,
            PatternHeight: processed.PatternHeight,
            BlackLevel: processed.BlackLevel,
            WhiteLevel: processed.WhiteLevel,
            Status: processed.Status,
            RafSidecarPath: processed.RafSidecarPath,
            DebayeredRgb: processed.DebayeredRgb,
            CameraMultipliers: processed.PreviewCameraMultipliers,
            PreviewBitDepth: processed.PreviewBitDepth);
    }
}

public enum LibRawProcessingStatus
{
    Success = 0,
    InvalidBuffer,
    WrapperUnavailable,
    ProcessingFailed,
    UnhandledException,
    InitializationFailed,
    UnsupportedFormat,
    InsufficientMemory,
    CorruptData,
    IoError,
    LibRawError,
    MetadataExtractionFailed,
    DataExtractionFailed,
    UnknownError
}

public readonly record struct LibRawResult(
    ushort[] BayerData,
    int Width,
    int Height,
    string ColorFilterPattern,
    int PatternWidth,
    int PatternHeight,
    int BlackLevel,
    int WhiteLevel,
    LibRawProcessingStatus Status,
    string? RafSidecarPath,
    ushort[]? DebayeredRgb = null,
    double[]? CameraMultipliers = null,
    int PreviewBitDepth = 16)
{
    // Success if we have either BayerData (for Bayer sensors) OR DebayeredRgb (for X-Trans)
    public bool Success => Status == LibRawProcessingStatus.Success && Width > 0 && Height > 0 &&
        (BayerData.Length > 0 || (DebayeredRgb != null && DebayeredRgb.Length > 0));
    
    /// <summary>
    /// Gets debayered RGB data if available from LibRaw (for X-Trans display in NINA).
    /// Returns null if not available or for Bayer sensors.
    /// </summary>
    public ushort[]? GetDebayeredRgb() => DebayeredRgb;

    public double[]? GetCameraMultipliers()
        => CameraMultipliers != null && CameraMultipliers.Length > 0 ? CameraMultipliers : null;

    public static LibRawResult FromFailure(LibRawProcessingStatus status, string? rafPath)
        => new(Array.Empty<ushort>(), 0, 0, string.Empty, 0, 0, 0, 0, status, rafPath, null, Array.Empty<double>(), 16);
}
