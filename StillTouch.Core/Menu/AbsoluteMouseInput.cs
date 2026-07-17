using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StillTouch.Core;

internal static class AbsoluteMouseInput
{
    public static bool Click(Point point, bool rightButton)
    {
        var (x, y) = Normalize(point);
        var down = rightButton
            ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN
            : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN;
        var up = rightButton
            ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP
            : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        return Send(
            CreateAbsoluteMove(x, y),
            CreateButton(down),
            CreateButton(up));
    }

    public static bool BeginLeftDrag(Point start, Point current)
    {
        var (startX, startY) = Normalize(start);
        var (currentX, currentY) = Normalize(current);
        return Send(
            CreateAbsoluteMove(startX, startY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateAbsoluteMove(currentX, currentY));
    }

    public static bool CompleteLeftDrag(Point start, Point end)
    {
        var (startX, startY) = Normalize(start);
        var (endX, endY) = Normalize(end);
        return Send(
            CreateAbsoluteMove(startX, startY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateAbsoluteMove(endX, endY),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
    }

    public static bool ReleaseLeft(Point point)
    {
        var (x, y) = Normalize(point);
        return Send(
            CreateAbsoluteMove(x, y),
            CreateButton(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
    }

    internal static int NormalizeCoordinate(int coordinate, int virtualOrigin, int virtualSize)
    {
        if (virtualSize <= 1)
            return 0;

        long offset = Math.Clamp((long)coordinate - virtualOrigin, 0, virtualSize - 1L);
        return (int)Math.Round(offset * 65535.0 / (virtualSize - 1L));
    }

    private static (int X, int Y) Normalize(Point point)
    {
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        return (
            NormalizeCoordinate(point.X, left, width),
            NormalizeCoordinate(point.Y, top, height));
    }

    private static INPUT CreateAbsoluteMove(int normalizedX, int normalizedY) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT
            {
                dx = normalizedX,
                dy = normalizedY,
                dwFlags =
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE_NOCOALESCE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE |
                    MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                dwExtraInfo = MouseInputSourceClassifier.InjectionMarker,
            },
        },
    };

    private static INPUT CreateButton(MOUSE_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT
            {
                dwFlags = flags,
                dwExtraInfo = MouseInputSourceClassifier.InjectionMarker,
            },
        },
    };

    private static unsafe bool Send(params INPUT[] inputs)
    {
        fixed (INPUT* inputPointer = inputs)
        {
            uint sent = PInvoke.SendInput(
                (uint)inputs.Length,
                inputPointer,
                Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
    }
}
