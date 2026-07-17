namespace StillTouch.Core;

internal static class MouseInputSourceClassifier
{
    private const ulong SourceMask = 0xFFFFFF80;
    private const ulong PenSignature = 0xFF515700;
    private const ulong TouchSignature = 0xFF515780;

    public const nuint InjectionMarker = 0x5443584D;

    public static bool IsOwnInjection(nuint extraInfo) =>
        extraInfo == InjectionMarker;

    public static bool IsTouchDerived(nuint extraInfo) =>
        ((ulong)extraInfo & SourceMask) == TouchSignature;

    public static bool IsPenDerived(nuint extraInfo) =>
        ((ulong)extraInfo & SourceMask) == PenSignature;
}
