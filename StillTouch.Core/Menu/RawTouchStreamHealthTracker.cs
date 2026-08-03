namespace StillTouch.Core;

internal enum RawTouchStreamDisposition
{
    Accepted,
    Quarantined,
    Resynchronized,
}

internal readonly record struct RawTouchStreamDecision(
    RawTouchStreamDisposition Disposition,
    long StreamEpoch);

/// <summary>
/// Keeps parser gaps fail-open on a per-device basis. Once a report is missing or malformed, tip
/// contacts are ignored until a complete all-lifted frame proves that the ContactId lifecycle is
/// synchronized again. Stream epochs are process-lifetime monotonic and never depend on report
/// ordinals, coordinates, or device-local ContactId reuse.
/// </summary>
internal sealed class RawTouchStreamHealthTracker
{
    private readonly Dictionary<nint, DeviceHealth> _devices = [];
    private long _nextStreamEpoch;

    public RawTouchStreamDecision ObserveCompletedFrame(
        nint deviceHandle,
        List<RawDecodedContact> contacts)
    {
        DeviceHealth state = GetOrCreate(deviceHandle);
        if (!state.IsQuarantined)
        {
            return new(
                RawTouchStreamDisposition.Accepted,
                state.StreamEpoch);
        }

        bool allLifted = contacts.Count > 0;
        for (int index = 0; index < contacts.Count; index++)
            allLifted &= !contacts[index].IsTip;

        if (!allLifted)
        {
            return new(
                RawTouchStreamDisposition.Quarantined,
                state.StreamEpoch);
        }

        state.IsQuarantined = false;
        state.StreamEpoch = NextEpoch();
        return new(
            RawTouchStreamDisposition.Resynchronized,
            state.StreamEpoch);
    }

    public long MarkDiscontinuity(nint deviceHandle)
    {
        DeviceHealth state = GetOrCreate(deviceHandle);
        if (!state.IsQuarantined)
        {
            state.IsQuarantined = true;
            state.StreamEpoch = NextEpoch();
        }

        return state.StreamEpoch;
    }

    /// <summary>
    /// A zero-contact report with no pending hybrid frame is a safe stream boundary, not evidence
    /// of a missing contact by itself. It must never poison an otherwise healthy stream. When a
    /// previous real gap left the device quarantined, the empty boundary proves that no old tip
    /// contact can leak into the next physical Down and starts a fresh healthy epoch.
    /// </summary>
    public RawTouchStreamDecision ObserveZeroContactBoundary(nint deviceHandle)
    {
        DeviceHealth state = GetOrCreate(deviceHandle);
        if (!state.IsQuarantined)
        {
            return new(
                RawTouchStreamDisposition.Accepted,
                state.StreamEpoch);
        }

        state.IsQuarantined = false;
        state.StreamEpoch = NextEpoch();
        return new(
            RawTouchStreamDisposition.Resynchronized,
            state.StreamEpoch);
    }

    public bool IsHealthy(nint deviceHandle) =>
        _devices.TryGetValue(deviceHandle, out DeviceHealth? state) &&
        !state.IsQuarantined;

    public void RemoveDevice(nint deviceHandle) => _devices.Remove(deviceHandle);

    public void Reset() => _devices.Clear();

    private DeviceHealth GetOrCreate(nint deviceHandle)
    {
        if (_devices.TryGetValue(deviceHandle, out DeviceHealth? state))
            return state;

        state = new DeviceHealth(NextEpoch());
        _devices.Add(deviceHandle, state);
        return state;
    }

    private long NextEpoch()
    {
        if (_nextStreamEpoch == long.MaxValue)
            throw new InvalidOperationException("Raw touch stream epoch exhausted.");

        return ++_nextStreamEpoch;
    }

    private sealed class DeviceHealth(long streamEpoch)
    {
        public long StreamEpoch { get; set; } = streamEpoch;

        public bool IsQuarantined { get; set; }
    }
}
