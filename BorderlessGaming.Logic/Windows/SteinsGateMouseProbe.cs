using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BorderlessGaming.Logic.Windows
{
    // Opt-in reproducer for a single game. Windows 10 version 1703 or newer.
    internal sealed class SteinsGateMouseProbe
    {
        private const uint WmSize = 0x0005;
        private const uint WmExitSizeMove = 0x0232;
        private const string InputOwner = "BorderlessGaming.SteinsGate.MouseOwner.v2";
        private const string InputRaw = "BorderlessGaming.SteinsGate.MouseRaw.v2";
        private const string InputMapped = "BorderlessGaming.SteinsGate.MouseMapped.v2";
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        private static readonly object LogLock = new object();
        private static readonly string LogFile = Environment.GetEnvironmentVariable("BG_MOUSE_PROBE_LOG") ??
            Path.Combine(Path.GetTempPath(), $"borderless-mouse-probe-{Process.GetCurrentProcess().Id}.log");

        private readonly IntPtr window;
        private readonly uint pid;
        private readonly Rectangle requested;
        private readonly string mode;
        private readonly Snapshot before;
        private bool attempted;
        private IntPtr ownerWindow;

        private SteinsGateMouseProbe(IntPtr window, uint pid, Rectangle requested, string mode)
        {
            this.window = window;
            this.pid = pid;
            this.requested = requested;
            this.mode = mode;
            before = Capture("before", true);
        }

        internal static SteinsGateMouseProbe Start(IntPtr window, Rectangle requested)
        {
            var exe = Environment.GetEnvironmentVariable("BG_MOUSE_PROBE_EXE");
            var autoDetect = string.IsNullOrWhiteSpace(exe) &&
                Environment.GetEnvironmentVariable("BG_MOUSE_PROBE_AUTO") == "1";
            if ((!autoDetect && string.IsNullOrWhiteSpace(exe)) ||
                GetWindowThreadProcessId(window, out uint pid) == 0)
                return null;

            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    var matches = autoDetect ? IsSteinsGate(window, process.ProcessName) :
                        string.Equals(Path.GetFileNameWithoutExtension(exe), process.ProcessName,
                            StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                        return null;
                }

                var mode = (Environment.GetEnvironmentVariable("BG_MOUSE_PROBE_MODE") ?? "observe")
                    .ToLowerInvariant();
                return new SteinsGateMouseProbe(window, pid, requested, mode);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                Log(ex.Message);
                return null;
            }
        }

        internal bool UseExactBounds =>
            (mode == "input" || mode == "release") &&
            Environment.GetEnvironmentVariable("BG_MOUSE_TEST_DATA") != null;

        private static bool IsSteinsGate(IntPtr window, string processName)
        {
            if (string.Equals(processName, "STEINSGATE", StringComparison.OrdinalIgnoreCase))
                return true;

            var buffer = new StringBuilder(260);
            GetWindowTextW(window, buffer, buffer.Capacity);
            var title = buffer.ToString().TrimStart();
            foreach (var prefix in new[] { "STEINS;GATE", "\u547D\u8FD0\u77F3\u4E4B\u95E8", "\u547D\u904B\u77F3\u4E4B\u9580" })
            {
                if (!title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = title.Substring(prefix.Length).TrimStart();
                return !suffix.StartsWith("0", StringComparison.Ordinal) &&
                       !suffix.StartsWith("ELITE", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal void AfterFrame()
        {
            Capture("after-frame", false);
        }

        internal void Finish(IntPtr owner)
        {
            ownerWindow = owner;
            Capture("after-final-position", true);
            _ = SampleSettled();
        }

        private async Task SampleSettled()
        {
            await Task.Delay(250).ConfigureAwait(false);
            Checkpoint("+250ms");
            await Task.Delay(750).ConfigureAwait(false);
            Checkpoint("+1000ms");
            await Task.Delay(1000).ConfigureAwait(false);
            Checkpoint("+2000ms");
            if (mode == "input")
            {
                await Task.Delay(4000).ConfigureAwait(false);
                Checkpoint("+6000ms");
            }
        }

        private void Checkpoint(string phase)
        {
            var now = Capture(phase, false);
            if (attempted || now == null || before == null || (mode != "input" && !now.Foreground) ||
                requested != now.ClientScreen || requested != now.Monitor ||
                !IsWide(before.ClientScreen) || !IsWide(now.ClientScreen) ||
                now.ClientScreen.Width <= before.ClientScreen.Width)
                return;

            if (mode == "input")
            {
                attempted = true;
                try
                {
                    var result = SgInstallMouseMapping(window, ownerWindow, (uint)before.LogicalClient.Width,
                        (uint)before.LogicalClient.Height);
                    Log($"input-mapping pid={pid} hwnd=0x{window.ToInt64():X} result={result}" +
                        $" native={before.LogicalClient.Width}x{before.LogicalClient.Height}" +
                        $" output={now.ClientScreen.Width}x{now.ClientScreen.Height}");
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException ||
                    ex is BadImageFormatException)
                {
                    Log("input-mapping failed: " + ex.Message);
                }
            }
            else if (mode == "release")
            {
                if (!LooksStale(before.ClientScreen, before.LogicalClient.Size, now.ClientScreen,
                    now.Clip, now.Desktop))
                    return;

                // This is shared desktop state. Recheck both the foreground and the clip.
                if (!ReleaseIfUnchanged(now.Clip))
                    return;
            }
            else if (mode == "size" || mode == "exit")
            {
                var width = now.LogicalClient.Width;
                var height = now.LogicalClient.Height;
                if (width <= 0 || width > ushort.MaxValue || height <= 0 || height > ushort.MaxValue ||
                    GetForegroundWindow() != window)
                    return;

                attempted = true;
                uint packed = mode == "size" ? (uint)(width | (height << 16)) : 0;
                IntPtr lParam = IntPtr.Size == 4 ? new IntPtr(unchecked((int)packed)) : new IntPtr(packed);
                SetLastError(0);
                var wParam = mode == "size" && now.Maximized ? new UIntPtr(2) : UIntPtr.Zero;
                var ok = SendMessageTimeoutW(window, mode == "size" ? WmSize : WmExitSizeMove,
                    wParam, lParam, 0x22, 200, out _); // ABORTIFHUNG | ERRORONEXIT
                Log(ok != IntPtr.Zero ? mode + " delivered" : mode + " failed error=" + Marshal.GetLastWin32Error());
            }
            else
            {
                return;
            }

            Capture("after-" + mode, false);
        }

        private bool ReleaseIfUnchanged(Rectangle expected)
        {
            IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorV2);
            if (previous == IntPtr.Zero)
                return false;

            try
            {
                if (!GetClipCursor(out Rect latest) || latest.Rectangle != expected ||
                    GetForegroundWindow() != window)
                    return false;

                attempted = true;
                var ok = ClipCursor(IntPtr.Zero);
                Log(ok ? "ClipCursor(NULL) succeeded" : "ClipCursor(NULL) error=" + Marshal.GetLastWin32Error());
                return true;
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        internal static bool LooksStale(Rectangle oldClient, Size oldLogical, Rectangle currentClient,
            Rectangle clip, Rectangle desktop)
        {
            if (clip == desktop || clip == currentClient || clip.IsEmpty)
                return false;

            return clip == oldClient ||
                   (clip.Size == oldClient.Size || clip.Size == oldLogical) &&
                   (clip.Location == Point.Empty || clip.Location == currentClient.Location);
        }

        private static bool IsWide(Rectangle rect)
        {
            return rect.Width > 0 && rect.Height > 0 &&
                Math.Abs(9L * rect.Width - 16L * rect.Height) <= 16;
        }

        private Snapshot Capture(string phase, bool measureMonitorDpi)
        {
            if (GetWindowThreadProcessId(window, out uint currentPid) == 0 || currentPid != pid)
                return null;

            try
            {
                var caller = GetThreadDpiAwarenessContext();
                var target = GetWindowDpiAwarenessContext(window);
                IntPtr previous = SetThreadDpiAwarenessContext(target);
                if (previous == IntPtr.Zero)
                    throw new Win32Exception();
                Rect logical;
                try
                {
                    Check(GetClientRect(window, out logical));
                }
                finally
                {
                    SetThreadDpiAwarenessContext(previous);
                }

                previous = SetThreadDpiAwarenessContext(PerMonitorV2);
                if (previous == IntPtr.Zero)
                    throw new Win32Exception();
                try
                {
                    Check(GetWindowRect(window, out Rect outer));
                    Check(GetClientRect(window, out Rect client));
                    var topLeft = new Point(client.Left, client.Top);
                    var bottomRight = new Point(client.Right, client.Bottom);
                    Check(ClientToScreen(window, ref topLeft));
                    Check(ClientToScreen(window, ref bottomRight));
                    Check(GetClipCursor(out Rect clip));
                    Check(GetPhysicalCursorPos(out Point cursor));
                    var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    var monitor = MonitorFromWindow(window, 2);
                    Check(GetMonitorInfoW(monitor, ref info));
                    var desktop = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77),
                        GetSystemMetrics(78), GetSystemMetrics(79));
                    var snapshot = new Snapshot
                    {
                        ClientScreen = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y),
                        Clip = clip.Rectangle,
                        LogicalClient = logical.Rectangle,
                        Monitor = info.Monitor.Rectangle,
                        Desktop = desktop,
                        Foreground = GetForegroundWindow() == window
                    };
                    int style = ReadStyle(window, -16);
                    snapshot.Maximized = (style & 0x01000000) != 0;
                    int exStyle = ReadStyle(window, -20);
                    uint monitorDpi = measureMonitorDpi ? GetMonitorDpi(info.Monitor) : 0;
                    bool iconic = IsIconic(window);
                    var mapping = mode == "input"
                        ? $" mappedSample={ReadPointProperty(window, InputRaw)}->{ReadPointProperty(window, InputMapped)}"
                        : "";
                    Log($"{phase} hwnd=0x{window.ToInt64():X} pid={pid} fg={snapshot.Foreground} " +
                        $"iconic={iconic} requested={Format(requested)} " +
                        $"windowPx={Format(outer.Rectangle)} clientPm={Format(client.Rectangle)} " +
                        $"clientScreenPx={Format(snapshot.ClientScreen)} clientGame={Format(snapshot.LogicalClient)} " +
                        $"clipPx={Format(snapshot.Clip)} cursorPx={cursor.X},{cursor.Y} " +
                        $"desktopPx={Format(desktop)} monitorPx={Format(snapshot.Monitor)} " +
                        $"workPx={Format(info.Work.Rectangle)} monitorDpi={(monitorDpi == 0 ? "-" : monitorDpi.ToString())} " +
                        $"windowDpi={GetDpiForWindow(window)} dpiAwareness(BG/game)={Awareness(caller)}/{Awareness(target)} " +
                        $"style=0x{style:X8} exStyle=0x{exStyle:X8} mode={mode}{mapping}");
                    return iconic ? null : snapshot;
                }
                finally
                {
                    SetThreadDpiAwarenessContext(previous);
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is EntryPointNotFoundException)
            {
                Log(phase + " " + ex.Message);
                return null;
            }
        }

        internal static void StopInputMapping(IntPtr hwnd)
        {
            if (GetPropW(hwnd, InputOwner) != IntPtr.Zero)
                RemovePropW(hwnd, InputOwner);
        }

        private static string ReadPointProperty(IntPtr hwnd, string name)
        {
            uint packed = unchecked((uint)GetPropW(hwnd, name).ToInt32());
            if (packed == 0)
                return "-";
            packed--;
            return $"{packed & 0xFFFF},{packed >> 16}";
        }

        private static uint GetMonitorDpi(Rect monitor)
        {
            // A hidden PMv2 HWND gets the effective DPI of the monitor at its position.
            IntPtr probe = CreateWindowExW(0, "STATIC", "", 0x80000000, monitor.Left + 1,
                monitor.Top + 1, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (probe == IntPtr.Zero)
                return 0;

            try { return GetDpiForWindow(probe); }
            finally { DestroyWindow(probe); }
        }

        private static string Awareness(IntPtr value)
        {
            return AreDpiAwarenessContextsEqual(value, PerMonitorV2) ? "PMv2" :
                GetAwarenessFromDpiAwarenessContext(value).ToString(); // 0 unaware, 1 system, 2 PMv1
        }

        private static int ReadStyle(IntPtr hwnd, int index)
        {
            SetLastError(0);
            int result = GetWindowLongW(hwnd, index);
            if (result == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception();
            return result;
        }

        private static void Check(bool ok)
        {
            if (!ok)
                throw new Win32Exception();
        }

        private static string Format(Rectangle r) => $"{r.Left},{r.Top}..{r.Right},{r.Bottom}";

        private static void Log(string line)
        {
            line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            Trace.WriteLine(line);
            try
            {
                lock (LogLock)
                    File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Snapshot
        {
            public Rectangle ClientScreen, LogicalClient, Clip, Desktop, Monitor;
            public bool Foreground, Maximized;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
        }

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out Rect r);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point p);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClipCursor(out Rect r);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClipCursor(IntPtr r);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPhysicalCursorPos(out Point p);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int GetWindowLongW(IntPtr hwnd, int index);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetPropW(IntPtr hwnd, string name);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RemovePropW(IntPtr hwnd, string name);

        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        [DllImport("SteinsGateInputFix.dll", ExactSpelling = true)]
        private static extern InputInstallResult SgInstallMouseMapping(IntPtr hwnd, IntPtr owner,
            uint nativeWidth, uint nativeHeight);

        private enum InputInstallResult
        {
            Installed, InvalidWindow, GameBuildNotSupported, ThreadHookFailed, GameDidNotAcknowledge
        }
    }
}
