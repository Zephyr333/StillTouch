namespace StillTouch.Core;

/// <summary>
/// Converts completed per-device HID frames into Down/Move/Up lifecycles. Contact IDs are scoped
/// to a device and receive a new generation every time the same numeric ID is reused.
/// </summary>
internal sealed class RawContactLifecycleTracker
{
    private readonly Dictionary<ContactKey, ActiveContact> _active = [];
    private readonly Dictionary<ContactKey, long> _generations = [];
    private readonly HashSet<ContactKey> _frameContacts = [];
    private int _maximumContactCount;

    public int ActiveContactCount => _active.Count;

    public bool HasActiveContacts(nint deviceHandle)
    {
        foreach (ContactKey key in _active.Keys)
        {
            if (key.DeviceHandle == deviceHandle)
                return true;
        }

        return false;
    }

    public TouchContactSnapshot ProcessFrame(
        nint deviceHandle,
        long frameEpoch,
        uint scanTime,
        List<RawDecodedContact> contacts,
        long timestampMilliseconds,
        List<TouchContactChange> changes,
        long rawStreamEpoch = 0)
    {
        changes.Clear();
        _frameContacts.Clear();

        // Validate the complete frame before mutating the active-contact table. A duplicated
        // Contact ID makes the whole frame ambiguous; applying the leading contacts and only
        // then rejecting the duplicate would leave a hidden partial Down/Move behind.
        for (int index = 0; index < contacts.Count; index++)
        {
            RawDecodedContact contact = contacts[index];
            var key = new ContactKey(deviceHandle, contact.ContactId);
            if (!_frameContacts.Add(key))
            {
                // Fail open: preserve the last known lifecycle and publish no synthetic change.
                return CreateSnapshot(timestampMilliseconds);
            }
        }

        // Contact Count describes a single physical frame, so a replacement frame containing
        // the old contact's UP and a new contact's DOWN is multi-touch regardless of report
        // ordering. Publish the same peak to every change to prevent Up-first order from being
        // mistaken for a completed single-finger tap.
        int prospectivePeak = _active.Count;
        for (int index = 0; index < contacts.Count; index++)
        {
            RawDecodedContact contact = contacts[index];
            var key = new ContactKey(deviceHandle, contact.ContactId);
            if (contact.IsTip && !_active.ContainsKey(key))
                prospectivePeak++;
        }
        _maximumContactCount = Math.Max(_maximumContactCount, prospectivePeak);

        for (int index = 0; index < contacts.Count; index++)
        {
            RawDecodedContact contact = contacts[index];
            var key = new ContactKey(deviceHandle, contact.ContactId);
            if (contact.IsTip)
            {
                bool wasActive = _active.TryGetValue(key, out ActiveContact active);
                if (!wasActive)
                {
                    long generation = _generations.TryGetValue(key, out long previous)
                        ? previous + 1
                        : 1;
                    _generations[key] = generation;
                    active = new ActiveContact(generation, contact.Position);
                    _active.Add(key, active);
                    _maximumContactCount = Math.Max(_maximumContactCount, _active.Count);
                }
                else
                {
                    active = active with { LastPosition = contact.Position };
                    _active[key] = active;
                }

                changes.Add(new(
                    contact.ContactId,
                    wasActive ? TouchContactChangeKind.Move : TouchContactChangeKind.Down,
                    contact.Position,
                    _active.Count,
                    _maximumContactCount,
                    timestampMilliseconds,
                    deviceHandle,
                    frameEpoch,
                    scanTime,
                    active.Generation,
                    rawStreamEpoch));
            }
            else if (_active.Remove(key, out ActiveContact active))
            {
                changes.Add(new(
                    contact.ContactId,
                    TouchContactChangeKind.Up,
                    contact.Position,
                    _active.Count,
                    _maximumContactCount,
                    timestampMilliseconds,
                    deviceHandle,
                    frameEpoch,
                    scanTime,
                    active.Generation,
                    rawStreamEpoch));
            }
        }

        TouchContactSnapshot snapshot = CreateSnapshot(timestampMilliseconds);
        if (_active.Count == 0)
            _maximumContactCount = 0;
        return snapshot;
    }

    public void ResetDevice(nint deviceHandle)
    {
        var activeKeys = _active.Keys
            .Where(key => key.DeviceHandle == deviceHandle)
            .ToArray();
        foreach (ContactKey key in activeKeys)
            _active.Remove(key);

        if (_active.Count == 0)
            _maximumContactCount = 0;
    }

    public void Reset()
    {
        _active.Clear();
        _frameContacts.Clear();
        _maximumContactCount = 0;
    }

    public void Clear()
    {
        Reset();
        _generations.Clear();
    }

    private TouchContactSnapshot CreateSnapshot(long timestampMilliseconds) => new(
        _active.Count,
        _maximumContactCount,
        timestampMilliseconds);

    private readonly record struct ContactKey(nint DeviceHandle, int ContactId);

    private readonly record struct ActiveContact(
        long Generation,
        System.Drawing.Point LastPosition);
}
