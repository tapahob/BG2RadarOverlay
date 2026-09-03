using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinApiBindings;

namespace BGOverlay.Input
{
    public enum MouseMessageTypes
    {
        Click
    }

    public enum MouseMessageCode
    {
        LeftButtonDown = 0x0201,
        LeftButtonUp = 0x0202,
        RightButtonDown = 0x0204,
        RightButtonUp = 0x0205,
        MiddleButtonDown = 0x0207,
        MiddleButtonUp = 0x0208
    }

    public class MouseMessageEventArgs : EventArgs
    {
        public int MessageCode { get; }
        public int X { get; }
        public int Y { get; }
        public bool Shift { get; }
        public bool Control { get; }
        public bool Alt { get; }

        internal MouseMessageEventArgs(int messageCode, int x, int y, bool shift, bool control, bool alt)
        {
            MessageCode = messageCode;
            X = x;
            Y = y;
            Shift = shift;
            Control = control;
            Alt = alt;
        }
    }

    public delegate void MouseMessageEventHandler(object sender, MouseMessageEventArgs e);

    /// <summary>
    /// Global mouse hook built on WH_MOUSE_LL. Unlike hook libraries that inject a DLL into the
    /// target process (e.g. Winook, previously used here), this runs entirely in-process, which is
    /// what makes it work when the overlay and the game both run under Wine/Proton - cross-process
    /// DLL injection between separate Wine processes is unreliable, but a plain in-process
    /// SetWindowsHookEx(WH_MOUSE_LL) is implemented by Wine's user32 and works the same as on
    /// native Windows. Events are filtered to the target pid's foreground window, mirroring the
    /// old per-process hook behavior.
    /// </summary>
    public sealed class MouseHook
    {
        private const int WH_MOUSE_LL = 14;
        private const uint WM_QUIT = 0x0012;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private readonly int _pid;
        private readonly ConcurrentDictionary<int, MouseMessageEventHandler> _handlers = new ConcurrentDictionary<int, MouseMessageEventHandler>();
        private readonly ManualResetEventSlim _installed = new ManualResetEventSlim(false);

        private LowLevelMouseProc _proc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private Thread _hookThread;
        private volatile uint _hookThreadId;

        public MouseHook(int pid, MouseMessageTypes types)
        {
            _pid = pid;
        }

        public void AddHandler(MouseMessageCode code, MouseMessageEventHandler handler)
        {
            _handlers[(int)code] = handler;
        }

        public Task InstallAsync()
        {
            return Task.Run(() =>
            {
                _hookThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "GlobalMouseHook" };
                _hookThread.SetApartmentState(ApartmentState.STA);
                _hookThread.Start();
                _installed.Wait();
            });
        }

        public void Uninstall()
        {
            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private void RunMessageLoop()
        {
            _hookThreadId = GetCurrentThreadId();
            // Keep a reference so the delegate isn't collected while the hook is installed.
            _proc = HookCallback;
            _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);

            _installed.Set();

            // WH_MOUSE_LL requires a message pump on the installing thread to keep receiving callbacks.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _handlers.TryGetValue(wParam.ToInt32(), out var handler) && IsTargetForeground())
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    handler?.Invoke(this, new MouseMessageEventArgs(
                        wParam.ToInt32(),
                        hookStruct.pt.X,
                        hookStruct.pt.Y,
                        (GetKeyState(VK_SHIFT) & 0x8000) != 0,
                        (GetKeyState(VK_CONTROL) & 0x8000) != 0,
                        (GetKeyState(VK_MENU) & 0x8000) != 0));
                }
                catch (Exception ex)
                {
                    Logger.Error("Mouse hook callback error!", ex);
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private bool IsTargetForeground()
        {
            var hwnd = WinAPIBindings.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            WinAPIBindings.GetWindowThreadProcessId(hwnd, out var windowPid);
            return windowPid == (uint)_pid;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
    }
}
