using System.Drawing;

namespace StillTouch.Core;

internal readonly record struct RawDecodedContact(
    int ContactId,
    bool IsTip,
    Point Position);

internal enum RawTouchFrameStatus
{
    Pending,
    Completed,
    OrphanContinuation,
    DroppedIncomplete,
    Invalid,
}

internal readonly record struct RawTouchFrameResult(
    RawTouchFrameStatus Status,
    nint DeviceHandle = default,
    long FrameEpoch = 0,
    uint ScanTime = 0,
    List<RawDecodedContact>? Contacts = null,
    bool HadDiscontinuity = false);

/// <summary>
/// Reassembles parallel and hybrid HID reports independently for every raw-input device.
/// In hybrid mode only the first report carries a non-zero Contact Count; later reports with the
/// same Scan Time are continuations and must never be interpreted as an all-contacts-up frame.
/// </summary>
internal sealed class RawTouchFrameAssembler
{
    private readonly Dictionary<nint, DeviceFrameState> _devices = [];
    private long _nextFrameEpoch;

    public long DroppedIncompleteFrames { get; private set; }

    public long OrphanContinuationReports { get; private set; }

    public bool HasPendingFrames
    {
        get
        {
            foreach (DeviceFrameState state in _devices.Values)
            {
                if (state.ExpectedContactCount > state.Contacts.Count)
                    return true;
            }

            return false;
        }
    }

    public int GetRemainingContactCount(nint deviceHandle)
    {
        if (!_devices.TryGetValue(deviceHandle, out DeviceFrameState? state))
            return 0;

        return Math.Max(0, state.ExpectedContactCount - state.Contacts.Count);
    }

    public RawTouchFrameResult AppendReport(
        nint deviceHandle,
        bool hasContactCount,
        int contactCount,
        bool hasScanTime,
        uint scanTime,
        ReadOnlySpan<RawDecodedContact> contacts)
    {
        if (deviceHandle == 0 || !hasContactCount || contactCount < 0)
            return new(RawTouchFrameStatus.Invalid);

        DeviceFrameState state = GetOrCreateDevice(deviceHandle);
        bool droppedIncomplete = false;

        if (contactCount > 0)
        {
            if (state.ExpectedContactCount > state.Contacts.Count)
            {
                DroppedIncompleteFrames++;
                droppedIncomplete = true;
            }

            state.BeginFrame(
                contactCount,
                hasScanTime,
                scanTime,
                ++_nextFrameEpoch);
        }
        else
        {
            if (state.ExpectedContactCount == 0)
            {
                // A zero count without a pending frame is not proof that every prior contact is
                // up. Windows hybrid devices legitimately use zero only for continuation reports.
                OrphanContinuationReports++;
                return new(RawTouchFrameStatus.OrphanContinuation);
            }

            if (state.HasScanTime != hasScanTime ||
                (state.HasScanTime && state.ScanTime != scanTime))
            {
                state.ResetFrame();
                DroppedIncompleteFrames++;
                return new(RawTouchFrameStatus.DroppedIncomplete);
            }
        }

        if (contacts.Length == 0 ||
            state.Contacts.Count + contacts.Length > state.ExpectedContactCount)
        {
            state.ResetFrame();
            return new(RawTouchFrameStatus.Invalid);
        }

        for (int index = 0; index < contacts.Length; index++)
            state.Contacts.Add(contacts[index]);

        if (state.Contacts.Count < state.ExpectedContactCount)
        {
            return new(
                droppedIncomplete
                    ? RawTouchFrameStatus.DroppedIncomplete
                    : RawTouchFrameStatus.Pending);
        }

        long epoch = state.FrameEpoch;
        uint completedScanTime = state.HasScanTime ? state.ScanTime : 0;
        state.ExpectedContactCount = 0;
        return new(
            RawTouchFrameStatus.Completed,
            deviceHandle,
            epoch,
            completedScanTime,
            state.Contacts,
            droppedIncomplete);
    }

    public void ResetDevice(nint deviceHandle)
    {
        _devices.Remove(deviceHandle);
    }

    public void Reset() => _devices.Clear();

    private DeviceFrameState GetOrCreateDevice(nint deviceHandle)
    {
        if (_devices.TryGetValue(deviceHandle, out DeviceFrameState? state))
            return state;

        state = new DeviceFrameState();
        _devices.Add(deviceHandle, state);
        return state;
    }

    private sealed class DeviceFrameState
    {
        public List<RawDecodedContact> Contacts { get; } = new(capacity: 10);

        public int ExpectedContactCount { get; set; }

        public bool HasScanTime { get; private set; }

        public uint ScanTime { get; private set; }

        public long FrameEpoch { get; private set; }

        public void BeginFrame(
            int contactCount,
            bool hasScanTime,
            uint scanTime,
            long frameEpoch)
        {
            Contacts.Clear();
            ExpectedContactCount = contactCount;
            HasScanTime = hasScanTime;
            ScanTime = scanTime;
            FrameEpoch = frameEpoch;
        }

        public void ResetFrame()
        {
            Contacts.Clear();
            ExpectedContactCount = 0;
            HasScanTime = false;
            ScanTime = 0;
        }
    }
}
