using System;

namespace NINA.Plugins.Fujifilm.Devices;

/// <summary>Outcome of one position sample taken while waiting for a focus move to finish.</summary>
public enum FocusSettleResult
{
    /// <summary>The lens is still moving; keep polling.</summary>
    Moving,

    /// <summary>The lens reached the commanded position.</summary>
    Arrived,

    /// <summary>The lens stopped moving short of the commanded position, and that is as good as it gets.</summary>
    Settled
}

/// <summary>
/// Decides when a focus move has finished.
/// </summary>
/// <remarks>
/// <para>
/// Requiring the reported position to equal the commanded one does not work on real hardware. A
/// GFX100S II reports a position 29-38 pulses below whatever it was told to go to, in both
/// directions and repeatably, on a lens whose minimum drive step is 3 — so an exact-arrival check
/// never succeeds and every move ends in a timeout even though the lens moved correctly. The SDK
/// says as much: the focus position "is not absolute, but fluctuates with temperature and a variety
/// of other conditions".
/// </para>
/// <para>
/// So the move is complete when the lens either reaches the target or stops moving. Waiting for the
/// position to go quiet is robust to that offset, to lens backlash, and to bodies that quantise the
/// request differently from the readout.
/// </para>
/// </remarks>
public sealed class FocusSettleTracker
{
    /// <summary>
    /// Consecutive unchanged samples required before the lens counts as stopped. At the 50ms poll
    /// interval used by the focuser this is a fifth of a second of no movement; the measured lens
    /// completed its travel in about 550ms.
    /// </summary>
    public const int DefaultStableSamples = 4;

    private readonly int _target;
    private readonly int _tolerance;
    private readonly int _stableSamplesRequired;

    private int _lastPosition;
    private int _stableSamples;
    private bool _hasSample;

    /// <param name="target">Commanded position.</param>
    /// <param name="tolerance">How close counts as arrived; normally the lens' minimum drive step.</param>
    /// <param name="stableSamplesRequired">Unchanged samples needed to call the lens stopped.</param>
    public FocusSettleTracker(int target, int tolerance, int stableSamplesRequired = DefaultStableSamples)
    {
        _target = target;
        _tolerance = Math.Max(1, tolerance);
        _stableSamplesRequired = Math.Max(1, stableSamplesRequired);
    }

    /// <summary>Position reported by the most recent sample.</summary>
    public int LastPosition => _lastPosition;

    /// <summary>How far the last sample sat from the commanded position.</summary>
    public int Residual => _lastPosition - _target;

    public FocusSettleResult Observe(int position)
    {
        var previous = _lastPosition;
        var hadSample = _hasSample;

        _lastPosition = position;
        _hasSample = true;

        if (Math.Abs(position - _target) <= _tolerance)
        {
            return FocusSettleResult.Arrived;
        }

        if (hadSample && Math.Abs(position - previous) <= _tolerance)
        {
            _stableSamples++;
            if (_stableSamples >= _stableSamplesRequired)
            {
                return FocusSettleResult.Settled;
            }
        }
        else
        {
            _stableSamples = 0;
        }

        return FocusSettleResult.Moving;
    }
}
