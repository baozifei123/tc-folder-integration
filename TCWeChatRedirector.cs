// Global Explorer -> Total Commander folder redirector.
// Runs per user at medium integrity. No admin, no keystroke logging.
//
// A-mode behavior: every new top-level File Explorer window is hidden,
// its folder is resolved, Total Commander is opened, and the Explorer
// window is closed. The window is shown before the asynchronous WinEvent
// callback arrives, so a brief flash is expected and accepted.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Native
{
    internal const int WM_CLOSE = 0x0010;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const uint EVENT_OBJECT_DESTROY = 0x8001;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint WINEVENT_OUTOFCONTEXT = 0;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    internal delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    internal delegate bool EnumWindowsProc(IntPtr h, IntPtr extra);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder s, int c);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] internal static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] internal static extern bool FlashWindow(IntPtr h, bool invert);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc cb, IntPtr extra);
}

internal sealed class Target
{
    public string Folder;
    public string File;
}

internal sealed class RedirectorContext : ApplicationContext
{
    private readonly string tcPath;
    private readonly string logPath;
    private readonly string stopName;
    private readonly NotifyIcon tray;
    private readonly Control dispatcher;
    private readonly System.Windows.Forms.Timer pollTimer;
    private readonly System.Windows.Forms.Timer focusTimer;
    private IntPtr focusWindow = IntPtr.Zero;
    private int focusTicks;
    private readonly EventWaitHandle stopEvent;
    private readonly RegisteredWaitHandle stopWait;
    private readonly Native.WinEventDelegate hookCallback;
    private readonly List<KeyValuePair<IntPtr, DateTime>> pending = new List<KeyValuePair<IntPtr, DateTime>>();
    private readonly HashSet<long> knownWindows = new HashSet<long>();
    private readonly object sync = new object();
    private IntPtr hookShow;
    private IntPtr hookDestroy;
    private volatile bool paused;

    public RedirectorContext()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        logPath = Path.Combine(exeDir, "explorer-redirect-log.txt");
        stopName = @"Local\TCWeChatRedirector-Stop-" + WindowsIdentity.GetCurrent().User.Value;
        tcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalCMD64", "TotalCMD64.exe");
        if (!File.Exists(tcPath)) throw new FileNotFoundException("未找到 Total Commander。", tcPath);

        dispatcher = new Control();
        IntPtr handle = dispatcher.Handle;
        tray = new NotifyIcon();
        try { tray.Icon = Icon.ExtractAssociatedIcon(tcPath) ?? SystemIcons.Application; }
        catch { tray.Icon = SystemIcons.Application; }
        tray.Text = "打开文件夹 → Total Commander（全局）";
        var menu = new ContextMenuStrip();
        menu.Items.Add("暂停/恢复转交", null, delegate { paused = !paused; UpdateTooltip(); Log(paused ? "已暂停" : "已恢复"); });
        menu.Items.Add("打开日志", null, delegate { try { Process.Start("notepad.exe", logPath); } catch { } });
        menu.Items.Add("卸载并退出（一键还原）", null, delegate { UninstallAndExit(); });
        menu.Items.Add("退出（保留登录启动）", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { try { Process.Start("notepad.exe", logPath); } catch { } };
        tray.Visible = true;

        hookCallback = OnWinEvent;
        hookShow = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, hookCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        hookDestroy = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_DESTROY, IntPtr.Zero, hookCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (hookShow == IntPtr.Zero || hookDestroy == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法安装窗口事件钩子");

        // Only visible windows are pre-marked. A hidden Explorer window that is
        // shown later should still be treated as a new window and redirected.
        foreach (var h in EnumerateVisibleExplorerWindows()) knownWindows.Add(h.ToInt64());

        pollTimer = new System.Windows.Forms.Timer { Interval = 100 };
        pollTimer.Tick += delegate { ProcessPending(); };
        pollTimer.Start();
        focusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        focusTimer.Tick += delegate { KeepFocus(); };

        stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, stopName);
        stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, delegate
        {
            try { dispatcher.BeginInvoke(new MethodInvoker(delegate { ExitThread(); })); } catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, true);

        Log("全局转交器已启动：所有新的资源管理器窗口都会转交到 TC");
    }

    private void UpdateTooltip()
    {
        tray.Text = paused ? "打开文件夹 → TC（已暂停）" : "打开文件夹 → Total Commander（全局）";
    }

    private void UninstallAndExit()
    {
        if (MessageBox.Show("确定要停止全局转交并移除登录启动项吗？", "全局转交", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = true });
        }
        catch (Exception ex) { Log("无法启动卸载程序: " + ex.Message); }
        ExitThread();
    }

    private void Log(string message)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > 1024 * 1024) File.WriteAllText(logPath, "");
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, new UTF8Encoding(true));
        }
        catch { }
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static List<IntPtr> EnumerateVisibleExplorerWindows()
    {
        var result = new List<IntPtr>();
        Native.EnumWindows(delegate(IntPtr h, IntPtr extra)
        {
            string cls = ClassName(h);
            if ((cls == "CabinetWClass" || cls == "ExploreWClass") && Native.IsWindowVisible(h)) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindTCWindow()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows(delegate(IntPtr h, IntPtr extra)
        {
            if (ClassName(h) != "TTOTAL_CMD") return true;
            if (!Native.IsWindowVisible(h) && !Native.IsIconic(h)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private void ActivateTC()
    {
        IntPtr target = IntPtr.Zero;
        for (int i = 0; i < 12 && target == IntPtr.Zero; i++)
        {
            target = FindTCWindow();
            if (target == IntPtr.Zero) Thread.Sleep(100);
        }
        if (target == IntPtr.Zero) { Log("置前失败: 未找到 TC 窗口"); return; }
        Log("置前: 找到 TC 窗口 hwnd=" + target.ToInt64());
        if (Native.IsIconic(target)) Native.ShowWindowAsync(target, 9);
        bool direct = Native.SetForegroundWindow(target);
        Log("置前: SetForegroundWindow=" + direct + " 当前前台=" + Native.GetForegroundWindow().ToInt64());
        if (direct) { StartFocusKeeper(target); return; }
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        bool focused;
        try { focused = Native.SetForegroundWindow(target); }
        finally { Native.keybd_event(0x12, 0, 2, UIntPtr.Zero); }
        Log("置前: Alt+SetForegroundWindow=" + focused + " 当前前台=" + Native.GetForegroundWindow().ToInt64());
        if (focused) { StartFocusKeeper(target); return; }
        Native.SwitchToThisWindow(target, true);
        IntPtr afterSwitch = Native.GetForegroundWindow();
        Log("置前: SwitchToThisWindow 后前台=" + afterSwitch.ToInt64());
        if (afterSwitch != target) { Native.FlashWindow(target, true); Log("置前: 仍失败，改为闪烁任务栏"); }
        StartFocusKeeper(target);
    }

    private void StartFocusKeeper(IntPtr target)
    {
        focusWindow = target;
        focusTicks = 0;
        focusTimer.Stop();
        focusTimer.Start();
    }

    private void KeepFocus()
    {
        focusTicks++;
        if (focusWindow == IntPtr.Zero) { focusTimer.Stop(); return; }
        IntPtr current = Native.GetForegroundWindow();
        if (current != focusWindow)
        {
            Log("焦点被切走，重新置前: 当前前台=" + current.ToInt64());
            if (Native.IsIconic(focusWindow)) Native.ShowWindowAsync(focusWindow, 9);
            bool ok = Native.SetForegroundWindow(focusWindow);
            if (!ok)
            {
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                try { ok = Native.SetForegroundWindow(focusWindow); }
                finally { Native.keybd_event(0x12, 0, 2, UIntPtr.Zero); }
            }
            if (!ok) Native.SwitchToThisWindow(focusWindow, true);
        }
        if (focusTicks >= 15)
        {
            focusTimer.Stop();
            Log("置前保持结束，当前前台=" + Native.GetForegroundWindow().ToInt64());
        }
    }

    private void OnWinEvent(IntPtr hookHandle, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        if (ev == Native.EVENT_OBJECT_DESTROY)
        {
            if (idObject == 0 && idChild == 0) { lock (sync) { knownWindows.Remove(hwnd.ToInt64()); } }
            return;
        }
        if (ev != Native.EVENT_OBJECT_SHOW || idObject != 0 || idChild != 0) return;
        string cls = ClassName(hwnd);
        if (cls != "CabinetWClass" && cls != "ExploreWClass") return;
        bool isNew;
        lock (sync) { isNew = knownWindows.Add(hwnd.ToInt64()); }
        if (!isNew) return;
        if (paused) return;

        Log("检测到新的资源管理器窗口，准备转交: hwnd=" + hwnd.ToInt64());
        Native.ShowWindow(hwnd, Native.SW_HIDE);
        if (Native.IsWindowVisible(hwnd)) Log("隐藏后窗口仍可见: hwnd=" + hwnd.ToInt64());
        lock (sync) { pending.Add(new KeyValuePair<IntPtr, DateTime>(hwnd, DateTime.UtcNow.AddMilliseconds(1800))); }
    }

    private Target Resolve(IntPtr hwnd)
    {
        object shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object windows = shell.GetType().InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            foreach (object w in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    object hwndValue = w.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, w, null);
                    if (Convert.ToInt64(hwndValue) != hwnd.ToInt64()) continue;
                    object fullName = w.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, w, null);
                    if (!string.Equals(Path.GetFileName(Convert.ToString(fullName)), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    object document = w.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, w, null);
                    object folder = document.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, document, null);
                    object self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                    string path = Convert.ToString(self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null));
                    string file = null;
                    try
                    {
                        object selected = document.GetType().InvokeMember("SelectedItems", BindingFlags.InvokeMethod, null, document, null);
                        int count = Convert.ToInt32(selected.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, selected, null));
                        if (count == 1)
                        {
                            object item = selected.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, selected, new object[] { 0 });
                            bool isFolder = Convert.ToBoolean(item.GetType().InvokeMember("IsFolder", BindingFlags.GetProperty, null, item, null));
                            if (!isFolder) file = Convert.ToString(item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null));
                        }
                    }
                    catch { }
                    return new Target { Folder = path, File = file };
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
        }
        return null;
    }

    private static bool IsLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            || path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool CloseExplorerWindow(IntPtr hwnd)
    {
        IntPtr result;
        Native.SendMessageTimeout(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out result);
        for (int i = 0; i < 20 && Native.IsWindow(hwnd); i++) Thread.Sleep(50);
        if (Native.IsWindow(hwnd))
        {
            Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < 10 && Native.IsWindow(hwnd); i++) Thread.Sleep(50);
        }
        return !Native.IsWindow(hwnd);
    }

    private void ProcessPending()
    {
        List<KeyValuePair<IntPtr, DateTime>> snapshot;
        lock (sync) { snapshot = new List<KeyValuePair<IntPtr, DateTime>>(pending); }
        foreach (var item in snapshot)
        {
            Target target = Resolve(item.Key);
            if (target != null)
            {
                lock (sync) { pending.Remove(item); }
                if (!IsLocalPath(target.Folder))
                {
                    Native.ShowWindow(item.Key, Native.SW_SHOW);
                    Log("跳过非本地路径: 类型=虚拟/其他");
                    continue;
                }
                string args = string.IsNullOrEmpty(target.File)
                    ? "/O /T /L=\"" + target.Folder + "\""
                    : "/O /A /S \"" + target.File + "\"";
                // Start TC first. If TC cannot start, the Explorer window is
                // still intact and can be shown again; closing it first would
                // lose the only window on failure.
                try
                {
                    Process.Start(new ProcessStartInfo(tcPath, args) { UseShellExecute = true });
                    Log("已启动 TC: 目录=" + target.Folder + (string.IsNullOrEmpty(target.File) ? "" : " 并定位文件"));
                }
                catch (Exception ex)
                {
                    Native.ShowWindow(item.Key, Native.SW_SHOW);
                    Log("启动 TC 失败，已恢复资源管理器窗口: " + ex.Message);
                    continue;
                }
                bool closed = CloseExplorerWindow(item.Key);
                if (closed) Log("已关闭资源管理器窗口");
                else
                {
                    Native.ShowWindow(item.Key, Native.SW_SHOW);
                    Log("未能关闭资源管理器窗口，已恢复显示（TC 已打开）");
                }
                ActivateTC();
                continue;
            }
            if (DateTime.UtcNow >= item.Value)
            {
                lock (sync) { pending.Remove(item); }
                Native.ShowWindow(item.Key, Native.SW_SHOW);
                Log("未能在时限内读取窗口路径，已恢复该资源管理器窗口");
            }
        }
    }

    protected override void ExitThreadCore()
    {
        if (hookShow != IntPtr.Zero) { Native.UnhookWinEvent(hookShow); hookShow = IntPtr.Zero; }
        if (hookDestroy != IntPtr.Zero) { Native.UnhookWinEvent(hookDestroy); hookDestroy = IntPtr.Zero; }
        if (pollTimer != null) pollTimer.Stop();
        if (focusTimer != null) focusTimer.Stop();

        // Never leave a window hidden because the process is exiting.
        List<IntPtr> hidden;
        lock (sync)
        {
            hidden = new List<IntPtr>();
            foreach (var item in pending) hidden.Add(item.Key);
            pending.Clear();
        }
        foreach (var h in hidden) { if (Native.IsWindow(h)) Native.ShowWindow(h, Native.SW_SHOW); }
        if (hidden.Count > 0) Log("退出时恢复了 " + hidden.Count + " 个被隐藏的资源管理器窗口");

        tray.Visible = false;
        tray.Dispose();
        if (stopWait != null) stopWait.Unregister(null);
        if (stopEvent != null) stopEvent.Dispose();
        dispatcher.Dispose();
        Log("全局转交器已退出");
        base.ExitThreadCore();
    }
}

internal static class Program
{
    private static string Suffix() { return WindowsIdentity.GetCurrent().User.Value; }

    private static void WriteStatus(string name, string text)
    {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), text + Environment.NewLine, new UTF8Encoding(true));
    }

    private static bool AnyRunning(bool excludeCurrent)
    {
        int current = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("TCWeChatRedirector"))
        {
            try { if (!excludeCurrent || p.Id != current) return true; }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static void SignalStop(string stopName)
    {
        try { using (var stop = EventWaitHandle.OpenExisting(stopName)) stop.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static bool SelfTest()
    {
        bool ok = true;
        Func<string, bool> local = delegate(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            return (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) || p.StartsWith(@"\\", StringComparison.Ordinal);
        };
        if (!local(@"C:\Windows")) ok = false;
        if (!local(@"\\server\share")) ok = false;
        if (local(@"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")) ok = false;
        if (local("")) ok = false;
        return ok;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--run";
        string suffix = Suffix();
        string stopName = @"Local\TCWeChatRedirector-Stop-" + suffix;
        string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string runValue = "TCWeChatRedirector";
        string exe = Process.GetCurrentProcess().MainModule.FileName;

        if (mode == "--selftest")
        {
            bool ok = SelfTest() && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalCMD64", "TotalCMD64.exe"));
            WriteStatus("redirector-selftest.txt", ok ? "PASS" : "FAIL");
            return ok ? 0 : 1;
        }
        if (mode == "--quit")
        {
            SignalStop(stopName);
            return 0;
        }
        if (mode == "--install")
        {
            // Stop the old instance first so the new binary is loaded.
            SignalStop(stopName);
            for (int i = 0; i < 50 && AnyRunning(true); i++) Thread.Sleep(100);
            bool oldStillRunning = AnyRunning(true);
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey))
            {
                key.SetValue(runValue, "\"" + exe + "\" --run", Microsoft.Win32.RegistryValueKind.String);
            }
            if (!oldStillRunning) StartBackground(exe);
            Thread.Sleep(500);
            bool running = AnyRunning(true);
            string status = "installed=yes" + Environment.NewLine
                + "startup=" + exe + Environment.NewLine
                + "mode=global" + Environment.NewLine
                + "running=" + running;
            if (oldStillRunning) status += Environment.NewLine + "warning=previous_instance_still_running";
            WriteStatus("redirector-status.txt", status);
            return oldStillRunning ? 2 : 0;
        }
        if (mode == "--uninstall")
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
            {
                if (key != null) key.DeleteValue(runValue, false);
            }
            SignalStop(stopName);
            for (int i = 0; i < 50 && AnyRunning(true); i++) Thread.Sleep(100);
            bool stillRunning = AnyRunning(true);
            WriteStatus("redirector-status.txt", "installed=no" + Environment.NewLine
                + "running=" + stillRunning + Environment.NewLine
                + "mode=global");
            return stillRunning ? 2 : 0;
        }
        if (mode == "--status")
        {
            bool running = AnyRunning(true);
            bool startup;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, false))
            {
                startup = key != null && key.GetValue(runValue) != null;
            }
            WriteStatus("redirector-status.txt", "running=" + running + Environment.NewLine
                + "startup=" + startup + Environment.NewLine
                + "mode=global");
            return 0;
        }

        bool fresh;
        using (var mutex = new System.Threading.Mutex(true, @"Local\TCWeChatRedirector-Instance-" + suffix, out fresh))
        {
            if (!fresh) return 0;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RedirectorContext());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "全局转交器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    private static void StartBackground(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--run") { UseShellExecute = true });
        }
        catch { }
    }
}
