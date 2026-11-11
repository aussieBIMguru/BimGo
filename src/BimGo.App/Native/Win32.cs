using System.Runtime.InteropServices;

// The class belongs to the Native namespace
namespace BimGo.Native
{
    /// <summary>
    /// Hand-written Win32 interop (user32, gdi32, kernel32) for the game window.
    /// Only what BimGo needs; no external packages.
    /// </summary>
    internal static unsafe class Win32
    {
        #region Constants

        public const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002, CS_OWNDC = 0x0020;
        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000;
        public const uint WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        public const int CW_USEDEFAULT = unchecked((int)0x80000000);
        public const int SW_SHOW = 5, SW_RESTORE = 9;
        public const int GWL_STYLE = -16;
        public const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        public const uint PM_REMOVE = 0x0001;

        public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_MOVE = 0x0003, WM_ACTIVATE = 0x0006;
        public const uint WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_CLOSE = 0x0010, WM_QUIT = 0x0012;
        public const uint WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_INPUT = 0x00FF;
        public const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
        public const uint WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCOMMAND = 0x0112;
        public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A;
        public const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        public const int XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
        public const uint MAPVK_VK_TO_CHAR = 2;
        public const uint WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;

        public const int SC_KEYMENU = 0xF100;
        public const int HTCLIENT = 1;
        public const int WA_INACTIVE = 0;

        public const ushort HID_USAGE_PAGE_GENERIC = 0x01, HID_USAGE_GENERIC_MOUSE = 0x02;
        public const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0;
        public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

        public const int IDC_ARROW = 32512;
        public const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40;
        public const uint MB_YESNOCANCEL = 0x3, MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30;
        public const int IDCANCEL = 2, IDYES = 6, IDNO = 7;
        public const uint WM_DROPFILES = 0x0233;
        public const int ASFW_ANY = -1;

        public const uint PFD_DRAW_TO_WINDOW = 0x00000004, PFD_SUPPORT_OPENGL = 0x00000020, PFD_DOUBLEBUFFER = 0x00000001;
        public const byte PFD_TYPE_RGBA = 0, PFD_MAIN_PLANE = 0;

        // Virtual keys
        public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22;
        public const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        public const int VK_F1 = 0x70, VK_F5 = 0x74, VK_F11 = 0x7A, VK_F12 = 0x7B;
        public const int VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD; // [ and ] on US layouts

        #endregion

        #region Structures

        [StructLayout(LayoutKind.Sequential)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public char* lpszMenuName;
            public char* lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public uint length;
            public uint flags;
            public uint showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public nint hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public nint hDevice;
            public nint wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort padding;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTMOUSE
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize;
            public ushort nVersion;
            public uint dwFlags;
            public byte iPixelType;
            public byte cColorBits;
            public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
            public byte cAlphaBits, cAlphaShift;
            public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
            public byte cDepthBits;
            public byte cStencilBits;
            public byte cAuxBuffers;
            public byte iLayerType;
            public byte bReserved;
            public uint dwLayerMask;
            public uint dwVisibleMask;
            public uint dwDamageMask;
        }

        #endregion

        #region user32

        [DllImport("user32.dll", SetLastError = true)]
        public static extern ushort RegisterClassExW(WNDCLASSEXW* wndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(nint hwnd);

        /// <summary>Round Z-E: a key's state right now (high bit = down), read once at start for safe start.</summary>
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(nint hwnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(nint hwnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hwnd);

        /// <summary>Translates a virtual key (MAPVK_VK_TO_CHAR: the unshifted character on the current layout; 0 if none).</summary>
        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
        public static extern uint MapVirtualKeyW(uint code, uint mapType);

        /// <summary>The do-nothing message.</summary>
        public const uint WM_NULL = 0x0000;

        [DllImport("user32.dll")]
        public static extern bool IsIconic(nint hwnd);

        [DllImport("user32.dll")]
        public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(in MSG msg);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessageW(in MSG msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint GetDC(nint hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(nint hwnd, nint hdc);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(nint hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(nint hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(nint hwnd, ref POINT point);

        /// <summary>Screen → client coordinates (gamepad cursor).</summary>
        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(nint hwnd, ref POINT point);

        /// <summary>The cursor's screen position (gamepad cursor).</summary>
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        /// <summary>Moves the cursor (screen coordinates; gamepad cursor).</summary>
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool ClipCursor(RECT* rect);

        [DllImport("user32.dll")]
        public static extern nint SetCursor(nint cursor);

        [DllImport("user32.dll")]
        public static extern nint LoadCursorW(nint instance, nint cursorName);

        [DllImport("user32.dll")]
        public static extern nint SetCapture(nint hwnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE* devices, uint count, uint size);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(nint rawInput, uint command, void* data, uint* size, uint headerSize);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetWindowTextW(nint hwnd, string text);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern nint GetWindowLongPtrW(nint hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPlacement(nint hwnd, in WINDOWPLACEMENT placement);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint FindWindowW(string className, string windowName);

        public const uint IMAGE_ICON = 1, LR_DEFAULTCOLOR = 0x0000, LR_LOADFROMFILE = 0x0010;
        public const int SM_CXICON = 11, SM_CYICON = 12, SM_CXSMICON = 49, SM_CYSMICON = 50;

        /// <summary>The icon resource id the C# compiler gives an ApplicationIcon.</summary>
        public const int APP_ICON_RESOURCE = 32512;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
        public static extern nint LoadImageFromFile(nint instance, string fileName, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll", EntryPoint = "LoadImageW")]
        public static extern nint LoadImageFromResource(nint instance, nint resourceId, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(nint value);

        /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
        public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        #endregion

        #region shell32 (drag and drop)

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(nint hwnd, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFileW(nint drop, uint index, char* file, uint length);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(nint drop);

        #endregion

        #region gdi32 / kernel32

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int ChoosePixelFormat(nint hdc, in PIXELFORMATDESCRIPTOR pfd);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool SetPixelFormat(nint hdc, int format, in PIXELFORMATDESCRIPTOR pfd);

        [DllImport("gdi32.dll")]
        public static extern bool SwapBuffers(nint hdc);

        // Font coverage checks for the UI atlas (Rendering/UiFont.cs, FontCoverage)
        [DllImport("gdi32.dll")]
        public static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        public static extern nint SelectObject(nint hdc, nint obj);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
            uint quality, uint pitchAndFamily, string faceName);

        [DllImport("gdi32.dll")]
        public static extern uint GetGlyphIndicesW(nint hdc, char* text, int count, ushort* indices, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string moduleName);

        #endregion

        #region Helpers

        /// <summary>Low 16 bits as signed.</summary>
        public static int LoWordSigned(nint value) => (short)((long)value & 0xFFFF);

        /// <summary>High 16 bits (of the low dword) as signed.</summary>
        public static int HiWordSigned(nint value) => (short)(((long)value >> 16) & 0xFFFF);

        #endregion
    }
}
