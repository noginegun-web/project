using System;
using System.Runtime.InteropServices;

namespace ScumOxygen.Core;

internal sealed class ConsoleCommandSender
{
    private readonly Logger _log;
    private readonly IntPtr _hInput;
    private readonly bool _available;
    private bool _warned;
    private readonly object _lock = new();

    public ConsoleCommandSender(Logger log)
    {
        _log = log;
        if (!OperatingSystem.IsWindows())
        {
            _available = false;
            return;
        }

        _hInput = GetStdHandle(STD_INPUT_HANDLE);
        if (_hInput == IntPtr.Zero || _hInput == INVALID_HANDLE_VALUE)
        {
            _available = false;
            return;
        }

        _available = GetConsoleMode(_hInput, out _);
    }

    public bool TrySend(string command)
    {
        if (!_available)
        {
            if (!_warned)
            {
                _warned = true;
                _log.Info("[ConsoleCommandSender] Console input handle not available; cannot send commands via console.");
            }
            return false;
        }

        var text = command + "\r\n";
        var records = new INPUT_RECORD[text.Length * 2];
        var idx = 0;

        foreach (var ch in text)
        {
            var vk = ch == '\r' ? (ushort)VK_RETURN : (ushort)0;
            records[idx++] = NewKeyEvent(ch, true, vk);
            records[idx++] = NewKeyEvent(ch, false, vk);
        }

        lock (_lock)
        {
            if (!WriteConsoleInputW(_hInput, records, (uint)records.Length, out var written) || written == 0)
            {
                _log.Info("[ConsoleCommandSender] WriteConsoleInputW failed.");
                return false;
            }
        }

        return true;
    }

    private static INPUT_RECORD NewKeyEvent(char ch, bool keyDown, ushort vk)
    {
        return new INPUT_RECORD
        {
            EventType = KEY_EVENT,
            KeyEvent = new KEY_EVENT_RECORD
            {
                bKeyDown = keyDown,
                wRepeatCount = 1,
                wVirtualKeyCode = vk,
                wVirtualScanCode = 0,
                UnicodeChar = ch,
                dwControlKeyState = 0
            }
        };
    }

    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const short KEY_EVENT = 0x0001;
    private const int VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public short EventType;
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(
        IntPtr hConsoleInput,
        [In] INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);
}
