using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using R3;
using StillTouch.Core.Interop;
using Windows.Win32;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StillTouch.Core;

public enum RecognizedGesture
{
    ThreeFingerTap,
    TwoFingerTap,
    TwoFingerSwipeUp,
    TwoFingerSwipeDown,
}

public readonly record struct TouchContactSnapshot(
    int ActiveContactCount,
    int MaxContactCount,
    long UpdatedAtMilliseconds)
{
    public bool IsRecent(long nowMilliseconds, int maximumAgeMilliseconds) =>
        MaxContactCount > 0 &&
        nowMilliseconds >= UpdatedAtMilliseconds &&
        nowMilliseconds - UpdatedAtMilliseconds <= maximumAgeMilliseconds;
}

[SupportedOSPlatform("windows8.0")]
public sealed class GestureRecognitionService : IDisposable
{
    internal const uint LongPressTimerMessage = 0x8000 + 0x53;

    private const uint WM_INPUT = 0x00FF;
    private const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint GIDC_ARRIVAL = 1;
    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort DigitizerUsagePage = 0x0D;
    private const ushort ContactIdentifierId = 0x51;
    private const ushort ContactCountId = 0x54;
    private const ushort ScanTimeId = 0x56;
    private const ushort TipId = 0x42;
    private const ushort FingerUsage = 0x22;
    private const ushort XCoordinateId = 0x30;
    private const ushort YCoordinateId = 0x31;
    private const ushort TouchScreenUsage = 0x04;
    private const double TapMovementThreshold = 32.0;
    private const double SwipeDistanceThreshold = 90.0;
    private static readonly TimeSpan TapDurationThreshold = TimeSpan.FromMilliseconds(450);
    private static readonly int RawHidDataOffset =
        Marshal.OffsetOf<RAWINPUT>("data").ToInt32() +
        Marshal.OffsetOf<RAWHID>("bRawData").ToInt32();

    private readonly WndProcDelegate _wndProc;
    private readonly InputTraceBuffer _trace;
    private readonly Dictionary<int, PointerStroke> _activeStrokes = [];
    private readonly Dictionary<nint, RawDeviceContext> _rawDevices = [];
    private readonly HashSet<nint> _unavailableRawDevices = [];
    private readonly RawTouchFrameAssembler _rawFrameAssembler = new();
    private readonly RawContactLifecycleTracker _rawLifecycles = new();
    private readonly RawTouchStreamHealthTracker _rawStreamHealth = new();
    private readonly List<TouchContactChange> _rawChanges = new(capacity: 16);
    private readonly List<PointerStroke> _completedStrokes = [];
    private readonly Subject<RecognizedGesture> _gestureRecognized = new();
    private DateTimeOffset _captureStartedAt;
    private nint _previousWndProc;
    private nint _hwnd;
    private uint _maxContactCount;
    private nint _rawInputBuffer;
    private int _rawInputBufferCapacity;
    private bool _disposed;
    private bool _isEnabled;

    public Observable<RecognizedGesture> ObservableGestureRecognized => _gestureRecognized;

    public TouchContactSnapshot CurrentTouchSnapshot { get; private set; }

    public event Action<TouchContactSnapshot>? TouchSnapshotChanged;

    internal event Action<TouchContactChange>? TouchContactChanged;

    internal event Action<long>? LongPressTimerElapsed;

    internal event Action<string>? DiagnosticMessage;

    internal bool HasRawTouchDevice => _rawDevices.Count > 0;

    internal System.Drawing.Point LastGesturePosition { get; private set; }

    public GestureRecognitionService(nint hwnd)
        : this(hwnd, new InputTraceBuffer())
    {
    }

    internal GestureRecognitionService(nint hwnd, InputTraceBuffer trace)
    {
        if (hwnd == nint.Zero)
            throw new ArgumentException("Window handle is required.", nameof(hwnd));

        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _hwnd = hwnd;
        _wndProc = WndProc;
        _previousWndProc = PInvoke.SetWindowLongPtr(
            new HWND(_hwnd),
            WINDOW_LONG_PTR_INDEX.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));

        if (_previousWndProc == 0)
            throw new InvalidOperationException("Failed to subclass window for gesture recognition.");

        try
        {
            RegisterRawTouchInput();
        }
        catch
        {
            _ = PInvoke.SetWindowLongPtr(
                new HWND(_hwnd),
                WINDOW_LONG_PTR_INDEX.GWL_WNDPROC,
                _previousWndProc);
            _previousWndProc = 0;
            throw;
        }
    }

    public nint GameWindowHandle { get; set; }
    public nint TouchWindowHandle { get; set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            ResetCapture();
            PublishTouchReset();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        IsEnabled = false;
        UnregisterRawTouchInput();
        ClearRawDevices();

        if (_rawInputBuffer != 0)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = 0;
            _rawInputBufferCapacity = 0;
        }

        if (_previousWndProc != 0)
            PInvoke.SetWindowLongPtr(new HWND(_hwnd), WINDOW_LONG_PTR_INDEX.GWL_WNDPROC, _previousWndProc);

        _disposed = true;
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        // Managed exceptions must not cross the subclassed native window procedure boundary.
        try
        {
            // Device and display caches must remain current while conversion is disabled; otherwise
            // re-enabling after a dock/rotation/device change would reuse stale descriptors.
            if (msg == WM_INPUT_DEVICE_CHANGE)
            {
                HandleRawDeviceChange(wParam, lParam);
            }
            else if (msg == WM_DISPLAYCHANGE)
            {
                foreach (RawDeviceContext device in _rawDevices.Values)
                    RefreshDisplayMapping(device, reportMapping: false);
                ResetCapture();
                if (_isEnabled)
                    PublishTouchReset();
            }

            if (_isEnabled)
            {
                switch (msg)
                {
                    case WM_INPUT:
                        ProcessRawInput(lParam);
                        break;
                    case WM_POINTERDOWN:
                        HandlePointerDown(GetPointerId(wParam), TryGetPointerPoint(wParam, out var downPoint) ? downPoint : null);
                        break;
                    case WM_POINTERUPDATE:
                        HandlePointerUpdate(GetPointerId(wParam), TryGetPointerPoint(wParam, out var updatePoint) ? updatePoint : null);
                        break;
                    case WM_POINTERUP:
                        HandlePointerUp(GetPointerId(wParam), TryGetPointerPoint(wParam, out var upPoint) ? upPoint : null);
                        break;
                    case WM_POINTERCAPTURECHANGED:
                        ResetCapture();
                        PublishTouchReset();
                        break;
                    case LongPressTimerMessage:
                        LongPressTimerElapsed?.Invoke(Environment.TickCount64);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Gesture recognition failed: {ex}");
            ResetCapture();
            PublishTouchReset();
        }

        return PInvoke.CallWindowProcRaw(
            _previousWndProc,
            hwnd,
            msg,
            wParam,
            lParam);
    }

    private void RegisterRawTouchInput()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = DigitizerUsagePage,
            usUsage = TouchScreenUsage,
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK | RAWINPUTDEVICE_FLAGS.RIDEV_DEVNOTIFY,
            hwndTarget = new HWND(_hwnd),
        };

        if (!PInvoke.RegisterRawInputDevices([device], (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Failed to register raw touch input.");

        DiscoverRawTouchDevices();
    }

    private void HandleRawDeviceChange(nuint change, nint deviceHandle)
    {
        RemoveRawDevice(deviceHandle);
        if ((uint)change == GIDC_ARRIVAL)
            _ = TryGetRawDeviceContext(deviceHandle, out _);

        ResetCapture();
        if (_isEnabled)
            PublishTouchReset();
    }

    private void UnregisterRawTouchInput()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = DigitizerUsagePage,
            usUsage = TouchScreenUsage,
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
        };

        _ = PInvoke.RegisterRawInputDevices([device], (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private void ProcessRawInput(nint rawInputHandle)
    {
        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int queueDelay = InputTraceBuffer.GetQueueDelayMilliseconds(
            unchecked((uint)PInvoke.GetMessageTime()));
        int result = 0;
        try
        {
            if (!TryReadRawInput(rawInputHandle))
                InvalidateAllRawStreams();
            else
                result = 1;
        }
        finally
        {
            _trace.Record(new(
                Environment.TickCount64,
                InputTraceKind.RawHandler,
                InputTraceBuffer.ElapsedMicroseconds(started),
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                queueDelay,
                Result: result));
        }
    }

    private unsafe bool TryReadRawInput(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        _ = PInvoke.GetRawInputData(
            new HRAWINPUT((void*)rawInputHandle),
            RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT,
            null,
            &size,
            headerSize);
        if (size == 0 || size > int.MaxValue)
            return false;

        EnsureRawInputBuffer((int)size);
        uint readSize = PInvoke.GetRawInputData(
            new HRAWINPUT((void*)rawInputHandle),
            RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT,
            (void*)_rawInputBuffer,
            &size,
            headerSize);
        if (readSize != size)
            return false;

        if (readSize < (uint)RawHidDataOffset)
            return false;

        ref readonly RAWINPUT raw = ref *(RAWINPUT*)_rawInputBuffer;
        if (raw.header.dwSize > readSize ||
            raw.header.dwSize < (uint)RawHidDataOffset)
        {
            return false;
        }

        if (raw.header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEHID)
            return false;

        nint deviceHandle = (nint)raw.header.hDevice;
        if (!TryGetRawDeviceContext(deviceHandle, out RawDeviceContext? foundDevice) ||
            foundDevice is null)
        {
            return true;
        }
        RawDeviceContext device = foundDevice;

        var hid = raw.data.hid;
        if (hid.dwSizeHid == 0 || hid.dwCount == 0 ||
            hid.dwSizeHid > int.MaxValue || hid.dwCount > int.MaxValue)
        {
            InvalidateRawStream(deviceHandle);
            return true;
        }

        ulong rawBytes = (ulong)hid.dwSizeHid * hid.dwCount;
        ulong availableRawBytes = raw.header.dwSize - (uint)RawHidDataOffset;
        if (rawBytes > availableRawBytes || rawBytes > int.MaxValue)
        {
            InvalidateRawStream(deviceHandle);
            return true;
        }

        nint rawData = _rawInputBuffer + RawHidDataOffset;
        for (int packetIndex = 0; packetIndex < (int)hid.dwCount; packetIndex++)
        {
            nint packet = rawData + packetIndex * (int)hid.dwSizeHid;
            if (!TryGetReportValue(
                    device,
                    packet,
                    (int)hid.dwSizeHid,
                    DigitizerUsagePage,
                    ContactCountId,
                    out uint rawContactCount))
            {
                InvalidateRawStream(deviceHandle);
                return true;
            }

            bool hasScanTime = TryGetReportValue(
                device,
                packet,
                (int)hid.dwSizeHid,
                DigitizerUsagePage,
                ScanTimeId,
                out uint scanTime);
            if (rawContactCount > 256)
            {
                InvalidateRawStream(deviceHandle);
                return true;
            }

            int contactCount = (int)rawContactCount;
            int remaining = contactCount > 0
                ? contactCount
                : _rawFrameAssembler.GetRemainingContactCount(deviceHandle);
            int contactsInPacket = Math.Min(remaining, device.ContactCollections.Length);
            _trace.Record(new(
                Environment.TickCount64,
                InputTraceKind.RawReport,
                DeviceHandle: deviceHandle,
                ScanTime: hasScanTime ? scanTime : 0,
                ActiveContactCount: contactCount,
                MaxContactCount: remaining,
                Code: hasScanTime ? 1 : 0,
                Result: contactsInPacket));

            if (contactsInPacket == 0)
            {
                RawTouchFrameResult emptyResult = _rawFrameAssembler.AppendReport(
                    deviceHandle,
                    hasContactCount: true,
                    contactCount,
                    hasScanTime,
                    scanTime,
                    ReadOnlySpan<RawDecodedContact>.Empty);
                HandleRawFrameResult(deviceHandle, emptyResult);
                continue;
            }

            for (int contactIndex = 0; contactIndex < contactsInPacket; contactIndex++)
            {
                if (!TryReadRawContact(
                        device,
                        packet,
                        (int)hid.dwSizeHid,
                        device.ContactCollections[contactIndex],
                        out device.ContactScratch[contactIndex]))
                {
                    InvalidateRawStream(deviceHandle);
                    return true;
                }
            }

            RawTouchFrameResult result = _rawFrameAssembler.AppendReport(
                deviceHandle,
                hasContactCount: true,
                contactCount,
                hasScanTime,
                scanTime,
                device.ContactScratch.AsSpan(0, contactsInPacket));
            HandleRawFrameResult(deviceHandle, result);
        }

        return true;
    }

    private void HandleRawFrameResult(
        nint deviceHandle,
        RawTouchFrameResult result)
    {
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.RawFrame,
            DeviceHandle: deviceHandle,
            FrameEpoch: result.FrameEpoch,
            ScanTime: result.ScanTime,
            ActiveContactCount: result.Contacts?.Count ?? 0,
            Code: (int)result.Status,
            Result: result.HadDiscontinuity ? 1 : 0));

        if (result.Status == RawTouchFrameStatus.Pending)
            return;

        if (result.Status == RawTouchFrameStatus.OrphanContinuation)
        {
            // MateBook-class devices may append a zero-contact idle/tail report after a fully
            // completed lift frame. With no pending hybrid frame it must not be promoted to a
            // permanent parser gap. If a driver uses this as the only observable lift boundary,
            // cancel the unfinished candidate fail-open (no click) and let the next Down start a
            // clean lifecycle instead of requiring a restart/toggle.
            bool canceledActiveContact =
                _rawLifecycles.HasActiveContacts(deviceHandle);
            if (canceledActiveContact)
            {
                _rawLifecycles.ResetDevice(deviceHandle);
                _rawChanges.Clear();
                PublishTouchReset();
            }

            RawTouchStreamDecision boundary =
                _rawStreamHealth.ObserveZeroContactBoundary(deviceHandle);
            _trace.Record(new(
                Environment.TickCount64,
                InputTraceKind.RawStream,
                DeviceHandle: deviceHandle,
                StreamEpoch: boundary.StreamEpoch,
                Code: (int)boundary.Disposition,
                Result: canceledActiveContact ? 4 : 3));
            return;
        }

        if (result.Status != RawTouchFrameStatus.Completed ||
            result.Contacts is not { } completedContacts)
        {
            InvalidateRawStream(deviceHandle);
            return;
        }

        if (result.HadDiscontinuity)
            InvalidateRawStream(deviceHandle);

        RawTouchStreamDecision stream =
            _rawStreamHealth.ObserveCompletedFrame(deviceHandle, completedContacts);
        if (stream.Disposition != RawTouchStreamDisposition.Accepted)
        {
            _trace.Record(new(
                Environment.TickCount64,
                InputTraceKind.RawStream,
                DeviceHandle: deviceHandle,
                StreamEpoch: stream.StreamEpoch,
                Code: (int)stream.Disposition));
            // A complete all-lifted frame changes Quarantined -> Resynchronized but is not itself
            // published. The next physical Down is the first frame eligible for conversion.
            return;
        }

        ProcessRawFrame(
            result.DeviceHandle,
            result.FrameEpoch,
            result.ScanTime,
            completedContacts,
            stream.StreamEpoch);
    }

    private void InvalidateRawStream(nint deviceHandle)
    {
        long streamEpoch = _rawStreamHealth.MarkDiscontinuity(deviceHandle);
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.RawStream,
            DeviceHandle: deviceHandle,
            StreamEpoch: streamEpoch,
            Code: (int)RawTouchStreamDisposition.Quarantined,
            Result: 1));
        _rawFrameAssembler.ResetDevice(deviceHandle);
        _rawLifecycles.ResetDevice(deviceHandle);
        _rawChanges.Clear();
        PublishTouchReset();
    }

    private void InvalidateAllRawStreams()
    {
        foreach (nint deviceHandle in _rawDevices.Keys)
        {
            long streamEpoch = _rawStreamHealth.MarkDiscontinuity(deviceHandle);
            _trace.Record(new(
                Environment.TickCount64,
                InputTraceKind.RawStream,
                DeviceHandle: deviceHandle,
                StreamEpoch: streamEpoch,
                Code: (int)RawTouchStreamDisposition.Quarantined,
                Result: 2));
        }

        _rawFrameAssembler.Reset();
        _rawLifecycles.Reset();
        _rawChanges.Clear();
        PublishTouchReset();
    }

    private unsafe void EnsureRawInputBuffer(int requiredSize)
    {
        if (requiredSize <= _rawInputBufferCapacity)
            return;

        int newCapacity = Math.Max(requiredSize, Math.Max(1024, _rawInputBufferCapacity * 2));
        _rawInputBuffer = _rawInputBuffer == 0
            ? Marshal.AllocHGlobal(newCapacity)
            : Marshal.ReAllocHGlobal(_rawInputBuffer, newCapacity);
        _rawInputBufferCapacity = newCapacity;
    }

    private unsafe bool TryGetRawDeviceContext(
        nint deviceHandle,
        out RawDeviceContext? context)
    {
        if (_rawDevices.TryGetValue(deviceHandle, out context))
            return true;
        if (deviceHandle == 0 || _unavailableRawDevices.Contains(deviceHandle))
            return false;

        RID_DEVICE_INFO info = default;
        info.cbSize = (uint)sizeof(RID_DEVICE_INFO);
        uint size = info.cbSize;
        uint result = PInvoke.GetRawInputDeviceInfo(
            new HANDLE((void*)deviceHandle),
            RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_DEVICEINFO,
            &info,
            &size);
        if (result == uint.MaxValue ||
            info.dwType != RID_DEVICE_INFO_TYPE.RIM_TYPEHID ||
            info.Anonymous.hid.usUsagePage != DigitizerUsagePage ||
            info.Anonymous.hid.usUsage != TouchScreenUsage)
        {
            _unavailableRawDevices.Add(deviceHandle);
            return false;
        }

        PreparsedDataHandle? preparsedData = null;
        try
        {
            preparsedData = GetPreparsedData(deviceHandle);
            if (!IsHidSuccess(PInvoke.HidP_GetCaps(preparsedData.Handle, out HIDP_CAPS caps)))
            {
                _unavailableRawDevices.Add(deviceHandle);
                return false;
            }

            HIDP_LINK_COLLECTION_NODE[] linkNodes = GetLinkCollectionNodes(
                preparsedData.Handle,
                caps.NumberLinkCollectionNodes);
            ushort[] contactCollections = GetContactCollections(linkNodes);
            CoordinateBounds logicalBounds = GetLogicalBounds(
                preparsedData.Handle,
                caps.NumberInputValueCaps);
            if (contactCollections.Length == 0 || !logicalBounds.IsValid)
            {
                _unavailableRawDevices.Add(deviceHandle);
                return false;
            }

            uint maximumUsageLength = PInvoke.HidP_MaxUsageListLength(
                HIDP_REPORT_TYPE.HidP_Input,
                DigitizerUsagePage,
                preparsedData.Handle);
            int usageCapacity = (int)Math.Clamp(maximumUsageLength, 8, 1024);
            context = new RawDeviceContext(
                deviceHandle,
                preparsedData,
                contactCollections,
                logicalBounds,
                usageCapacity);
            preparsedData = null;
            RefreshDisplayMapping(context, reportMapping: true);
            _rawDevices.Add(deviceHandle, context);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Unable to cache raw touch descriptor: {ex}");
            _unavailableRawDevices.Add(deviceHandle);
            return false;
        }
        finally
        {
            preparsedData?.Dispose();
        }
    }

    private unsafe void DiscoverRawTouchDevices()
    {
        uint count = 0;
        uint elementSize = (uint)sizeof(RAWINPUTDEVICELIST);
        uint initialResult = PInvoke.GetRawInputDeviceList(
            Span<RAWINPUTDEVICELIST>.Empty,
            ref count,
            elementSize);
        if (initialResult == uint.MaxValue || count == 0 || count > 4096)
            return;

        var devices = new RAWINPUTDEVICELIST[count];
        uint listed = PInvoke.GetRawInputDeviceList(devices, ref count, elementSize);
        if (listed == uint.MaxValue)
            return;

        int length = (int)Math.Min(listed, count);
        for (int index = 0; index < length; index++)
        {
            if (devices[index].dwType == RID_DEVICE_INFO_TYPE.RIM_TYPEHID)
                _ = TryGetRawDeviceContext((nint)devices[index].hDevice, out _);
        }
    }

    private void RemoveRawDevice(nint deviceHandle)
    {
        if (_rawDevices.Remove(deviceHandle, out RawDeviceContext? context))
            context.Dispose();
        _unavailableRawDevices.Remove(deviceHandle);
        _rawFrameAssembler.ResetDevice(deviceHandle);
        _rawLifecycles.ResetDevice(deviceHandle);
        _rawStreamHealth.RemoveDevice(deviceHandle);
    }

    private void ClearRawDevices()
    {
        foreach (RawDeviceContext context in _rawDevices.Values)
            context.Dispose();
        _rawDevices.Clear();
        _unavailableRawDevices.Clear();
        _rawFrameAssembler.Reset();
        _rawLifecycles.Clear();
        _rawStreamHealth.Reset();
        _rawChanges.Clear();
    }

    private static unsafe bool TryGetReportValue(
        RawDeviceContext device,
        nint packet,
        int packetSize,
        ushort usagePage,
        ushort usage,
        out uint value)
    {
        NTSTATUS status = PInvoke.HidP_GetUsageValue(
            HIDP_REPORT_TYPE.HidP_Input,
            usagePage,
            0,
            usage,
            out value,
            device.PreparsedData.Handle,
            new PSTR((byte*)packet),
            (uint)packetSize);
        return IsHidSuccess(status);
    }

    private static unsafe PreparsedDataHandle GetPreparsedData(nint deviceHandle)
    {
        uint size = 0;
        _ = PInvoke.GetRawInputDeviceInfo(new HANDLE((void*)deviceHandle), RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, null, &size);
        if (size == 0)
            throw new InvalidOperationException("Raw input preparsed data is empty.");

        nint handle = Marshal.AllocHGlobal((int)size);
        uint result = PInvoke.GetRawInputDeviceInfo(new HANDLE((void*)deviceHandle), RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, (void*)handle, &size);
        if (result == uint.MaxValue)
        {
            Marshal.FreeHGlobal(handle);
            throw new InvalidOperationException("GetRawInputDeviceInfo(RIDI_PREPARSEDDATA) failed.");
        }

        return new(new PHIDP_PREPARSED_DATA(handle));
    }

    private static HIDP_LINK_COLLECTION_NODE[] GetLinkCollectionNodes(
        PHIDP_PREPARSED_DATA preparsedData,
        ushort expectedCount)
    {
        if (expectedCount == 0)
            return [];

        uint count = expectedCount;
        var nodes = new HIDP_LINK_COLLECTION_NODE[count];
        var status = PInvoke.HidP_GetLinkCollectionNodes(nodes, ref count, preparsedData);
        if (!IsHidSuccess(status) || count == 0 || count > nodes.Length)
            return [];

        if (count < nodes.Length)
            Array.Resize(ref nodes, (int)count);

        return nodes;
    }

    private static ushort[] GetContactCollections(HIDP_LINK_COLLECTION_NODE[] nodes)
    {
        var collections = new List<ushort>(Math.Max(1, nodes.Length - 1));
        for (ushort index = 1; index < nodes.Length; index++)
        {
            if (nodes[index].LinkUsagePage == DigitizerUsagePage &&
                nodes[index].LinkUsage == FingerUsage)
            {
                collections.Add(index);
            }
        }

        return collections.ToArray();
    }

    private static CoordinateBounds GetLogicalBounds(
        PHIDP_PREPARSED_DATA preparsedData,
        ushort inputValueCapsCount)
    {
        int count = Math.Max(inputValueCapsCount, (ushort)1);
        var caps = new HIDP_VALUE_CAPS[count];
        ushort capsLength = (ushort)caps.Length;
        var xStatus = PInvoke.HidP_GetSpecificValueCaps(
            HIDP_REPORT_TYPE.HidP_Input,
            GenericDesktopPage,
            0,
            XCoordinateId,
            caps,
            ref capsLength,
            preparsedData);
        var x = IsHidSuccess(xStatus)
            ? GetLogicalRange(caps, capsLength)
            : default;

        capsLength = (ushort)caps.Length;
        var yStatus = PInvoke.HidP_GetSpecificValueCaps(
            HIDP_REPORT_TYPE.HidP_Input,
            GenericDesktopPage,
            0,
            YCoordinateId,
            caps,
            ref capsLength,
            preparsedData);
        var y = IsHidSuccess(yStatus)
            ? GetLogicalRange(caps, capsLength)
            : default;

        return new(x, y);
    }

    private static CoordinateRange GetLogicalRange(HIDP_VALUE_CAPS[] caps, ushort capsLength)
    {
        int length = Math.Clamp(capsLength, 0, caps.Length);
        for (int i = 0; i < length; i++)
        {
            int minimum = caps[i].LogicalMin;
            int maximum = caps[i].LogicalMax;
            if (maximum > minimum)
                return new(minimum, maximum);
        }

        return default;
    }

    private unsafe bool TryReadRawContact(
        RawDeviceContext device,
        nint packet,
        int packetSize,
        ushort nodeIndex,
        out RawDecodedContact contact)
    {
        contact = default;
        NTSTATUS contactIdStatus = PInvoke.HidP_GetUsageValue(
            HIDP_REPORT_TYPE.HidP_Input,
            DigitizerUsagePage,
            nodeIndex,
            ContactIdentifierId,
            out uint contactId,
            device.PreparsedData.Handle,
            new PSTR((byte*)packet),
            (uint)packetSize);

        uint logicalX = 0;
        uint logicalY = 0;
        var xStatus = PInvoke.HidP_GetUsageValue(
            HIDP_REPORT_TYPE.HidP_Input,
            GenericDesktopPage,
            nodeIndex,
            XCoordinateId,
            out logicalX,
            device.PreparsedData.Handle,
            new PSTR((byte*)packet),
            (uint)packetSize);
        var yStatus = PInvoke.HidP_GetUsageValue(
            HIDP_REPORT_TYPE.HidP_Input,
            GenericDesktopPage,
            nodeIndex,
            YCoordinateId,
            out logicalY,
            device.PreparsedData.Handle,
            new PSTR((byte*)packet),
            (uint)packetSize);

        if (!IsHidSuccess(contactIdStatus) ||
            !IsHidSuccess(xStatus) ||
            !IsHidSuccess(yStatus) ||
            !TryReadTipContact(device, packet, packetSize, nodeIndex, out bool isTip))
        {
            return false;
        }

        var point = ScaleToScreen(
            device,
            unchecked((int)logicalX),
            unchecked((int)logicalY));
        contact = new(
            (int)contactId,
            isTip,
            new System.Drawing.Point(point.X, point.Y));
        return true;
    }

    private static unsafe bool TryReadTipContact(
        RawDeviceContext device,
        nint packet,
        int packetSize,
        ushort nodeIndex,
        out bool isTip)
    {
        uint usageLength = (uint)device.UsageScratch.Length;
        NTSTATUS status = PInvoke.HidP_GetUsages(
            HIDP_REPORT_TYPE.HidP_Input,
            DigitizerUsagePage,
            nodeIndex,
            device.UsageScratch,
            ref usageLength,
            device.PreparsedData.Handle,
            new PSTR((byte*)packet),
            (uint)packetSize);

        if (!IsHidSuccess(status))
        {
            isTip = false;
            return false;
        }

        isTip = false;
        int length = (int)Math.Min(usageLength, (uint)device.UsageScratch.Length);
        for (int index = 0; index < length; index++)
        {
            if (device.UsageScratch[index] == TipId)
            {
                isTip = true;
                break;
            }
        }

        return true;
    }

    private static PointerPoint ScaleToScreen(
        RawDeviceContext device,
        int logicalX,
        int logicalY)
    {
        CoordinateBounds logicalBounds = device.LogicalBounds;
        RECT displayRect = device.DisplayRect;
        return new(
            ScaleCoordinate(
                logicalX,
                logicalBounds.X.Minimum,
                logicalBounds.X.Maximum,
                displayRect.left,
                displayRect.right),
            ScaleCoordinate(
                logicalY,
                logicalBounds.Y.Minimum,
                logicalBounds.Y.Maximum,
                displayRect.top,
                displayRect.bottom));
    }

    private unsafe void RefreshDisplayMapping(
        RawDeviceContext device,
        bool reportMapping)
    {
        RECT deviceRect = default;
        RECT displayRect;
        bool usedPointerMapping = PInvoke.GetPointerDeviceRects(
            new HANDLE((void*)device.DeviceHandle),
            &deviceRect,
            &displayRect);
        if (!usedPointerMapping)
        {
            int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            int width = Math.Max(
                1,
                PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN));
            int height = Math.Max(
                1,
                PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
            displayRect = new RECT(left, top, left + width, top + height);
        }

        device.DeviceRect = deviceRect;
        device.DisplayRect = displayRect;
        device.UsedPointerMapping = usedPointerMapping;
        if (reportMapping)
        {
            ReportCoordinateMappingOnce(
                device.DeviceHandle,
                device.LogicalBounds,
                deviceRect,
                displayRect,
                usedPointerMapping);
        }
    }

    private void ReportCoordinateMappingOnce(
        nint deviceHandle,
        CoordinateBounds logicalBounds,
        RECT pointerDeviceRect,
        RECT displayRect,
        bool usedPointerMapping)
    {
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.DeviceMapping,
            DeviceHandle: deviceHandle,
            X: logicalBounds.X.Minimum,
            Y: logicalBounds.Y.Minimum,
            ActiveContactCount: logicalBounds.X.Maximum,
            MaxContactCount: logicalBounds.Y.Maximum,
            Code: 0));
        _trace.Record(new(
            Environment.TickCount64,
            InputTraceKind.DeviceMapping,
            DeviceHandle: deviceHandle,
            X: displayRect.left,
            Y: displayRect.top,
            ActiveContactCount: displayRect.right,
            MaxContactCount: displayRect.bottom,
            Code: 1,
            Result: usedPointerMapping ? 1 : 0));

        string pointerRange = usedPointerMapping
            ? $"HIMETRIC=({pointerDeviceRect.left},{pointerDeviceRect.top}).." +
              $"({pointerDeviceRect.right},{pointerDeviceRect.bottom})"
            : "HIMETRIC=不可用，回退到主显示器";
        string message =
            $"触摸坐标映射：Logical=({logicalBounds.X.Minimum},{logicalBounds.Y.Minimum}).." +
            $"({logicalBounds.X.Maximum},{logicalBounds.Y.Maximum})；{pointerRange}；" +
            $"Display=({displayRect.left},{displayRect.top}).." +
            $"({displayRect.right},{displayRect.bottom})。";

        try
        {
            DiagnosticMessage?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never interfere with the global input path.
        }
    }

    internal static int ScaleCoordinate(
        int value,
        int sourceMinimum,
        int sourceMaximum,
        int targetMinimum,
        int targetMaximumExclusive)
    {
        long sourceSpan = (long)sourceMaximum - sourceMinimum;
        long targetSpan = (long)targetMaximumExclusive - targetMinimum;
        if (sourceSpan <= 0 || targetSpan <= 1)
            return targetMinimum;

        long sourceOffset = Math.Clamp((long)value - sourceMinimum, 0, sourceSpan);
        return targetMinimum +
            (int)Math.Round(sourceOffset * (targetSpan - 1.0) / sourceSpan);
    }

    private void ProcessRawFrame(
        nint deviceHandle,
        long frameEpoch,
        uint scanTime,
        List<RawDecodedContact> contacts,
        long rawStreamEpoch)
    {
        long timestamp = Environment.TickCount64;
        CurrentTouchSnapshot = _rawLifecycles.ProcessFrame(
            deviceHandle,
            frameEpoch,
            scanTime,
            contacts,
            timestamp,
            _rawChanges,
            rawStreamEpoch);
        for (int index = 0; index < _rawChanges.Count; index++)
        {
            TouchContactChange change = _rawChanges[index];
            _trace.Record(new(
                change.TimestampMilliseconds,
                InputTraceKind.RawContact,
                DeviceHandle: change.DeviceHandle,
                StreamEpoch: change.RawStreamEpoch,
                FrameEpoch: change.FrameEpoch,
                ContactId: change.ContactId,
                ContactGeneration: change.ContactGeneration,
                ScanTime: change.ScanTime,
                X: change.Position.X,
                Y: change.Position.Y,
                ActiveContactCount: change.ActiveContactCount,
                MaxContactCount: change.MaxContactCount,
                Code: (int)change.Kind));
            TouchContactChanged?.Invoke(change);
        }
        TouchSnapshotChanged?.Invoke(CurrentTouchSnapshot);
    }

    private void PublishTouchReset()
    {
        CurrentTouchSnapshot = new(
            _rawLifecycles.ActiveContactCount,
            0,
            Environment.TickCount64);
        try
        {
            TouchSnapshotChanged?.Invoke(CurrentTouchSnapshot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Touch reset snapshot subscriber failed: {ex}");
        }

        try
        {
            TouchContactChanged?.Invoke(new(
                0,
                TouchContactChangeKind.Reset,
                System.Drawing.Point.Empty,
                0,
                0,
                Environment.TickCount64));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Touch reset contact subscriber failed: {ex}");
        }
    }

    // Trigger early when one finger lifts with a swipe and the remaining fingers are stationary (held).
    private bool ShouldTriggerEarlySwipe()
    {
        if (_completedStrokes.Count == 0 || _activeStrokes.Count == 0)
            return false;

        bool anyCompletedSwipe = _completedStrokes.Any(
            s => Math.Abs(s.Delta.Y) >= SwipeDistanceThreshold &&
                 Math.Abs(s.Delta.Y) > Math.Abs(s.Delta.X) * 1.3);

        bool allActiveStationary = _activeStrokes.Values.All(
            s => s.Movement <= TapMovementThreshold);

        return anyCompletedSwipe && allActiveStationary;
    }

    private void HandlePointerDown(int pointerId, PointerPoint? point)
    {
        if (point is not { } value)
            return;

        if (_activeStrokes.Count == 0)
        {
            _completedStrokes.Clear();
            _captureStartedAt = DateTimeOffset.Now;

            // Filter: only start tracking if point is in valid area
            if (!IsValidGestureStartPoint(value))
                return;
        }

        _activeStrokes[pointerId] = new PointerStroke(value);
        _maxContactCount = Math.Max(_maxContactCount, (uint)_activeStrokes.Count);
    }

    private bool IsValidGestureStartPoint(PointerPoint point)
    {
        var screenPoint = new System.Drawing.Point(point.X, point.Y);

        if (GameWindowHandle != nint.Zero && !OsPlatformApi.IsPointInsideClientArea(screenPoint, GameWindowHandle))
            return false;

        if (TouchWindowHandle != nint.Zero && OsPlatformApi.IsPointInsideWindowOrChild(screenPoint, TouchWindowHandle))
            return false;

        return true;
    }

    private void HandlePointerUpdate(int pointerId, PointerPoint? point)
    {
        if (point is not { } value)
            return;

        if (_activeStrokes.TryGetValue(pointerId, out var stroke))
            stroke.Add(value);
    }

    private void HandlePointerUp(int pointerId, PointerPoint? point)
    {
        if (point is not { } value)
            return;

        if (_activeStrokes.Remove(pointerId, out var stroke))
        {
            stroke.Add(value);
            _completedStrokes.Add(stroke);
        }
    }

    private readonly List<GestureDefinition> _gestures =
    [
        new(RecognizedGesture.ThreeFingerTap, ctx => ctx.MaxContactCount >= 3
            && ctx.Duration <= TapDurationThreshold
            && ctx.MaxMovement <= TapMovementThreshold),
        new(RecognizedGesture.TwoFingerTap,  ctx => ctx.MaxContactCount == 2
            && ctx.Duration <= TapDurationThreshold
            && ctx.MaxMovement <= TapMovementThreshold),
        new(RecognizedGesture.TwoFingerSwipeUp,   ctx => IsVerticalSwipe(ctx) && ctx.DominantStroke!.Delta.Y < 0),
        new(RecognizedGesture.TwoFingerSwipeDown, ctx => IsVerticalSwipe(ctx) && ctx.DominantStroke!.Delta.Y > 0),
    ];

    private static bool IsVerticalSwipe(GestureContext ctx) =>
        ctx.MaxContactCount >= 2 &&
        ctx.DominantStroke is not null &&
        Math.Abs(ctx.DominantStroke.Delta.Y) >= SwipeDistanceThreshold &&
        Math.Abs(ctx.DominantStroke.Delta.Y) > Math.Abs(ctx.DominantStroke.Delta.X) * 1.3;


    private void CompleteCapture()
    {
        RecognizedGesture? gesture = RecognizeGesture();
        if (gesture is not null)
        {
            _gestureRecognized.OnNext(gesture.Value);
        }

        ResetCapture();
    }

    private RecognizedGesture? RecognizeGesture()
    {
        if (_completedStrokes.Count == 0)
            return null;

        var ctx = BuildGestureContext();
        var definition = _gestures.FirstOrDefault(g => g.Matches(ctx));
        if (definition is null)
            return null;

        LastGesturePosition = ctx.Position;
        return definition.Gesture;
    }

    private GestureContext BuildGestureContext() => new(
        MaxContactCount: (int)_maxContactCount,
        Duration: DateTimeOffset.Now - _captureStartedAt,
        MaxMovement: _completedStrokes.Max(static s => s.Movement),
        Position: _completedStrokes[0].StartPoint,
        DominantStroke: _completedStrokes.MaxBy(static s => Math.Abs(s.Delta.Y))
    );

    private void ResetCapture()
    {
        bool interruptedRawLifecycle =
            _rawLifecycles.ActiveContactCount > 0 ||
            _rawFrameAssembler.HasPendingFrames;
        if (interruptedRawLifecycle)
        {
            foreach (nint deviceHandle in _rawDevices.Keys)
                _ = _rawStreamHealth.MarkDiscontinuity(deviceHandle);
        }

        _activeStrokes.Clear();
        _completedStrokes.Clear();
        _rawLifecycles.Reset();
        _rawChanges.Clear();
        _rawFrameAssembler.Reset();
        _maxContactCount = 0;
    }

    private static bool TryGetPointerPoint(nuint wParam, out PointerPoint point)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            point = default;
            return false;
        }

        uint pointerId = (uint)(wParam & 0xFFFF);
        if (!PInvoke.GetPointerInfo(pointerId, out var pointerInfo))
        {
            point = default;
            return false;
        }

        point = new(pointerInfo.ptPixelLocation.X, pointerInfo.ptPixelLocation.Y);
        return true;
    }

    private static int GetPointerId(nuint wParam) =>
        (int)(wParam & 0xFFFF);

    private static bool IsHidSuccess(NTSTATUS status) =>
        (int)status.Value == HIDP_STATUS_SUCCESS;

    private readonly record struct PointerPoint(int X, int Y);

    private readonly record struct CoordinateRange(int Minimum, int Maximum)
    {
        public bool IsValid => Maximum > Minimum;
    }

    private readonly record struct CoordinateBounds(CoordinateRange X, CoordinateRange Y)
    {
        public bool IsValid => X.IsValid && Y.IsValid;
    }

    private sealed class RawDeviceContext : IDisposable
    {
        public RawDeviceContext(
            nint deviceHandle,
            PreparsedDataHandle preparsedData,
            ushort[] contactCollections,
            CoordinateBounds logicalBounds,
            int usageCapacity)
        {
            DeviceHandle = deviceHandle;
            PreparsedData = preparsedData;
            ContactCollections = contactCollections;
            LogicalBounds = logicalBounds;
            UsageScratch = new ushort[usageCapacity];
            ContactScratch = new RawDecodedContact[contactCollections.Length];
        }

        public nint DeviceHandle { get; }

        public PreparsedDataHandle PreparsedData { get; }

        public ushort[] ContactCollections { get; }

        public CoordinateBounds LogicalBounds { get; }

        public ushort[] UsageScratch { get; }

        public RawDecodedContact[] ContactScratch { get; }

        public RECT DeviceRect { get; set; }

        public RECT DisplayRect { get; set; }

        public bool UsedPointerMapping { get; set; }

        public void Dispose() => PreparsedData.Dispose();
    }

    private sealed class PointerStroke
    {
        private readonly PointerPoint _start;
        private PointerPoint _last;

        public PointerStroke(PointerPoint start)
        {
            _start = start;
            _last = start;
        }

        public Vector2 Delta => new((float)(_last.X - _start.X), (float)(_last.Y - _start.Y));

        public System.Drawing.Point StartPoint => new(_start.X, _start.Y);

        public PointerPoint LastPoint => _last;

        public double Movement { get; private set; }

        public void Add(PointerPoint point)
        {
            double distance = Distance(_last, point);
            if (distance <= 0)
                return;

            Movement += distance;
            _last = point;
        }

        private static double Distance(PointerPoint left, PointerPoint right)
        {
            double dx = right.X - left.X;
            double dy = right.Y - left.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private sealed class PreparsedDataHandle(PHIDP_PREPARSED_DATA handle) : IDisposable
    {
        public PHIDP_PREPARSED_DATA Handle { get; } = handle;

        public void Dispose() =>
            Marshal.FreeHGlobal(Handle.Value);
    }

    private sealed record GestureContext(
        int MaxContactCount,
        TimeSpan Duration,
        double MaxMovement,
        System.Drawing.Point Position,
        PointerStroke? DominantStroke);

    private sealed record GestureDefinition(RecognizedGesture Gesture, Func<GestureContext, bool> Matches);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nuint wParam, nint lParam);
}
