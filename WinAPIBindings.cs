using BGOverlay;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinApiBindings
{
    public static class WinAPIBindings
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All                     = 0x001F0FFF,
            Terminate               = 0x00000001,
            CreateThread            = 0x00000002,
            VirtualMemoryOperation  = 0x00000008,
            VirtualMemoryRead       = 0x00000010,
            VirtualMemoryWrite      = 0x00000020,
            DuplicateHandle         = 0x00000040,
            CreateProcess           = 0x000000080,
            SetQuota                = 0x00000100,
            SetInformation          = 0x00000200,
            QueryInformation        = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize             = 0x00100000
        }

        [DllImport("user32.dll")]
        public static extern int ShowWindow(int hwnd, int nCmdShow);

        public static IntPtr GetModuleBaseAddress(Process proc, string modName)
        {
            foreach (ProcessModule module in proc.Modules)
            {
                if (module.ModuleName.Equals(modName))
                {
                    return module.BaseAddress;
                }
            }

            return IntPtr.Zero;
        }

        public static IntPtr GetModuleBaseAddress(int procId, string modName)
        {
            IntPtr addr = IntPtr.Zero;

            IntPtr hSnap = CreateToolhelp32Snapshot(SnapshotFlags.Module | SnapshotFlags.Module32, (uint)procId);
            if (hSnap.ToInt64() != -1)
            {
                MODULEENTRY32 modEntry = new MODULEENTRY32();
                modEntry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));

                if (Module32First(hSnap, ref modEntry))
                {
                    do
                    {
                        if (modEntry.szModule.Equals(modName))
                        {
                            CloseHandle(hSnap);
                            return modEntry.modBaseAddr;
                        }
                    } while (Module32Next(hSnap, ref modEntry));
                }
            }

            return IntPtr.Zero;
        }

        public static IntPtr FindDMAAddy(IntPtr ptr, int[] offsets = null)
        {
            offsets = offsets ?? new int[] { };

            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[IntPtr.Size];
            foreach (int i in offsets)
            {
                ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
                ptr = (IntPtr.Size == 4)
                    ? IntPtr.Add(new IntPtr(BitConverter.ToInt32(buffer, 0)), i)
                    : ptr = IntPtr.Add(new IntPtr(BitConverter.ToInt64(buffer, 0)), i);
            }
            return ptr;
        }

        public static UInt32 ReadUInt32(IntPtr ptr)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[4];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = BitConverter.ToUInt32(buffer, 0);
            return result;
        }
        public static int ReadInt32(IntPtr ptr)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[4];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = BitConverter.ToInt32(buffer, 0);
            return result;
        }

        public static short ReadInt16(IntPtr ptr)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[2];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = BitConverter.ToInt16(buffer, 0);
            return result;
        }

        public static ushort ReadUInt16(IntPtr ptr)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[2];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = BitConverter.ToUInt16(buffer, 0);
            return result;
        }

        public static byte ReadByte(IntPtr ptr)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[1];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = buffer[0];
            return result;
        }
        public static byte[] ReadBytes(IntPtr ptr, int size)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[size];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            return buffer;
        }

        public static string ReadString(IntPtr ptr, int strLength = 16)
        {
            IntPtr hProc = Configuration.hProc;
            var buffer = new byte[strLength];
            ReadProcessMemory(hProc, ptr, buffer, buffer.Length, out var read);
            var result = UTF8Encoding.UTF8.GetString(buffer);
            if (result.StartsWith("\0"))
            {
                return "<ERROR>";
            }
            var length = result.IndexOf('\0');
            result = length > 0
                ? result.Substring(0, result.IndexOf('\0'))
                : result;
            return result.Trim('\u0001');
        }

        public static IntPtr OpenProcess(Process proc, ProcessAccessFlags flags)
        {
            return OpenProcess(flags, false, proc.Id);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        };

        [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        public struct MODULEENTRY32
        {
            internal uint dwSize;
            internal uint th32ModuleID;
            internal uint th32ProcessID;
            internal uint GlblcntUsage;
            internal uint ProccntUsage;
            internal IntPtr modBaseAddr;
            internal uint modBaseSize;
            internal IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out, MarshalAs(UnmanagedType.AsAny)] object lpBuffer,
            int dwSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            IntPtr lpBuffer,
            int dwSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, Int32 nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(
          IntPtr hProcess,
          IntPtr lpBaseAddress,
          [MarshalAs(UnmanagedType.AsAny)] object lpBuffer,
          int dwSize,
          out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        [DllImport("kernel32.dll")]
        static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

        [Flags]
        private enum SnapshotFlags : uint
        {
            HeapList = 0x00000001,
            Process  = 0x00000002,
            Thread   = 0x00000004,
            Module   = 0x00000008,
            Module32 = 0x00000010,
            Inherit  = 0x80000000,
            All      = 0x0000001F,
            NoHeaps  = 0x40000000
        }

        [Flags]
        public enum ShowWindowCommands : uint
        {
            SW_HIDE            = 0,
            SW_SHOWNORMAL      = 1,
            SW_NORMAL          = 1,
            SW_SHOWMINIMIZED   = 2,
            SW_SHOWMAXIMIZED   = 3,
            SW_MAXIMIZE        = 3,
            SW_SHOWNOACTIVATE  = 4,
            SW_SHOW            = 5,
            SW_MINIMIZE        = 6,
            SW_SHOWMINNOACTIVE = 7,
            SW_SHOWNA          = 8,
            SW_RESTORE         = 9,
            SW_SHOWDEFAULT     = 10,
            SW_FORCEMINIMIZE   = 11,
            SW_MAX             = 11
        }
        
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        public static extern int SetWindowLong32(IntPtr hWnd, int nIndex, long dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);


        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
       
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();
        [Flags]
        public enum WindowStyles : uint
        {
            WS_OVERLAPPED       = 0x00000000,
            WS_POPUP            = 0x80000000,
            WS_CHILD            = 0x40000000,
            WS_MINIMIZE         = 0x20000000,
            WS_VISIBLE          = 0x10000000,
            WS_DISABLED         = 0x08000000,
            WS_CLIPSIBLINGS     = 0x04000000,
            WS_CLIPCHILDREN     = 0x02000000,
            WS_MAXIMIZE         = 0x01000000,
            WS_BORDER           = 0x00800000,
            WS_DLGFRAME         = 0x00400000,
            WS_VSCROLL          = 0x00200000,
            WS_HSCROLL          = 0x00100000,
            WS_SYSMENU          = 0x00080000,
            WS_THICKFRAME       = 0x00040000,
            WS_GROUP            = 0x00020000,
            WS_TABSTOP          = 0x00010000,
            WS_MINIMIZEBOX      = 0x00020000,
            WS_MAXIMIZEBOX      = 0x00010000,
            WS_CAPTION          = WS_BORDER | WS_DLGFRAME,
            WS_TILED            = WS_OVERLAPPED,
            WS_ICONIC           = WS_MINIMIZE,
            WS_SIZEBOX          = WS_THICKFRAME,
            WS_TILEDWINDOW      = WS_OVERLAPPEDWINDOW,
            WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX,
            WS_POPUPWINDOW      = WS_POPUP | WS_BORDER | WS_SYSMENU,
            WS_CHILDWINDOW      = WS_CHILD,
        }
        }
    }
