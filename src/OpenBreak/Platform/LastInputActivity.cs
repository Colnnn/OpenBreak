using System.Runtime.InteropServices;

namespace OpenBreak;

internal static class LastInputActivity
{
    internal static LastInputSample? Read()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref input) ? new LastInputSample(GetTickCount(), input.Tick) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint Tick;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetTickCount();
}
