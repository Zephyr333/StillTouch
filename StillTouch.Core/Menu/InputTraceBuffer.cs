using System.Text;

namespace StillTouch.Core;

internal enum InputTraceKind : byte
{
    RawHandler,
    RawReport,
    RawFrame,
    RawContact,
    RawStream,
    DeviceMapping,
    PromotedMouse,
    Injection,
    LongPressTimer,
}

internal readonly record struct InputTraceEntry(
    long TimestampMilliseconds,
    InputTraceKind Kind,
    long DurationMicroseconds = 0,
    long AllocatedBytes = 0,
    int QueueDelayMilliseconds = -1,
    nint DeviceHandle = default,
    long StreamEpoch = 0,
    long FrameEpoch = 0,
    long Sequence = 0,
    int ContactId = 0,
    long ContactGeneration = 0,
    uint ScanTime = 0,
    int X = 0,
    int Y = 0,
    int ActiveContactCount = 0,
    int MaxContactCount = 0,
    int Code = 0,
    int Result = 0);

/// <summary>
/// Fixed-size, input-thread-owned trace. Recording overwrites the oldest entry and performs no
/// managed allocation. Formatting/copying happens only on an explicit diagnostic request.
/// </summary>
internal sealed class InputTraceBuffer(int capacity = 4096)
{
    private readonly InputTraceEntry[] _entries =
        new InputTraceEntry[Math.Max(128, capacity)];
    private int _next;
    private int _count;
    private long _overwritten;

    public int Count => _count;

    public long OverwrittenEntryCount => _overwritten;

    public void Record(in InputTraceEntry entry)
    {
        _entries[_next] = entry;
        _next++;
        if (_next == _entries.Length)
            _next = 0;

        if (_count < _entries.Length)
            _count++;
        else
            _overwritten++;
    }

    public string FormatSnapshot()
    {
        InputTraceEntry[] snapshot = Snapshot();
        var builder = new StringBuilder(
            Math.Max(1024, snapshot.Length * 96));
        builder.Append("StillTouch bounded input trace; entries=")
            .Append(snapshot.Length)
            .Append("; overwritten=")
            .Append(_overwritten)
            .AppendLine();
        AppendPerformanceSummary(builder, snapshot, InputTraceKind.RawHandler, "raw");
        AppendPerformanceSummary(builder, snapshot, InputTraceKind.PromotedMouse, "hook");
        AppendPerformanceSummary(builder, snapshot, InputTraceKind.Injection, "sendinput");
        builder.AppendLine(
            "delay_ms: RawHandler/PromotedMouse = message queue; Injection = source event to SendInput start.");
        builder.AppendLine(
            "Injection code: 1/2 = raw left/right; 101/102 = promoted fallback left/right; 103-105 = drag. PromotedMouse result bits: 0x100 = suppressed, 0x200 = paired with a suppressed down.");
        builder.AppendLine(
            "tick_ms\tkind\tduration_us\tallocated_B\tdelay_ms\tdevice\tstream\tframe\tsequence\tcontact\tgeneration\tscan\tx\ty\tactive\tmax\tcode\tresult");

        for (int index = 0; index < snapshot.Length; index++)
        {
            InputTraceEntry entry = snapshot[index];
            builder.Append(entry.TimestampMilliseconds).Append('\t')
                .Append(entry.Kind).Append('\t')
                .Append(entry.DurationMicroseconds).Append('\t')
                .Append(entry.AllocatedBytes).Append('\t')
                .Append(entry.QueueDelayMilliseconds).Append('\t')
                .Append("0x").Append(unchecked((nuint)entry.DeviceHandle).ToString("X")).Append('\t')
                .Append(entry.StreamEpoch).Append('\t')
                .Append(entry.FrameEpoch).Append('\t')
                .Append(entry.Sequence).Append('\t')
                .Append(entry.ContactId).Append('\t')
                .Append(entry.ContactGeneration).Append('\t')
                .Append(entry.ScanTime).Append('\t')
                .Append(entry.X).Append('\t')
                .Append(entry.Y).Append('\t')
                .Append(entry.ActiveContactCount).Append('\t')
                .Append(entry.MaxContactCount).Append('\t')
                .Append(entry.Code).Append('\t')
                .Append(entry.Result).AppendLine();
        }

        return builder.ToString();
    }

    internal InputTraceEntry[] Snapshot()
    {
        var result = new InputTraceEntry[_count];
        int start = _count == _entries.Length ? _next : 0;
        for (int index = 0; index < result.Length; index++)
            result[index] = _entries[(start + index) % _entries.Length];
        return result;
    }

    internal static long ElapsedMicroseconds(long startedTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsed <= 0
            ? 0
            : (long)(elapsed * (1_000_000.0 / Stopwatch.Frequency));
    }

    internal static int GetQueueDelayMilliseconds(uint eventTimeMilliseconds)
    {
        uint now = unchecked((uint)Environment.TickCount);
        uint elapsed = unchecked(now - eventTimeMilliseconds);
        return elapsed <= 60_000 ? (int)elapsed : -1;
    }

    internal static long GetEventTimestampMilliseconds(
        long observedAtMilliseconds,
        int queueDelayMilliseconds) =>
        queueDelayMilliseconds >= 0
            ? observedAtMilliseconds - queueDelayMilliseconds
            : observedAtMilliseconds;

    internal static int GetElapsedMilliseconds(
        long sourceTimestampMilliseconds,
        long observedAtMilliseconds)
    {
        if (sourceTimestampMilliseconds <= 0 ||
            observedAtMilliseconds < sourceTimestampMilliseconds)
        {
            return -1;
        }

        return (int)Math.Min(
            observedAtMilliseconds - sourceTimestampMilliseconds,
            int.MaxValue);
    }

    private static void AppendPerformanceSummary(
        StringBuilder builder,
        InputTraceEntry[] snapshot,
        InputTraceKind kind,
        string label)
    {
        long[] durations = snapshot
            .Where(entry => entry.Kind == kind && entry.DurationMicroseconds >= 0)
            .Select(entry => entry.DurationMicroseconds)
            .Order()
            .ToArray();
        long maximumAllocation = snapshot
            .Where(entry => entry.Kind == kind)
            .Select(entry => entry.AllocatedBytes)
            .DefaultIfEmpty()
            .Max();
        int maximumQueueDelay = snapshot
            .Where(entry => entry.Kind == kind)
            .Select(entry => entry.QueueDelayMilliseconds)
            .DefaultIfEmpty(-1)
            .Max();

        builder.Append(label)
            .Append(" count=").Append(durations.Length)
            .Append(" p50_us=").Append(Percentile(durations, 0.50))
            .Append(" p95_us=").Append(Percentile(durations, 0.95))
            .Append(" p99_us=").Append(Percentile(durations, 0.99))
            .Append(" max_us=").Append(durations.Length == 0 ? 0 : durations[^1])
            .Append(" max_alloc_B=").Append(maximumAllocation)
            .Append(" max_queue_ms=").Append(maximumQueueDelay)
            .AppendLine();
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        int index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
