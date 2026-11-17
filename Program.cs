using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Program
{
    const string Target = @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe";
    const string Shortcut = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft SQL Server Tools 22\SQL Server Management Studio 22.lnk";
    delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, string file, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    sealed class WindowState
    {
        public uint Process;
        public IntPtr Large, Small;
    }
    static readonly Dictionary<IntPtr, WindowState> originals = new Dictionary<IntPtr, WindowState>();
    static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string LogPath = Path.Combine(Base, "运行日志.txt");
    static IntPtr large, small;
    static NotifyIcon tray;
    static Timer timer;
    static bool seenProcess;
    static DateTime started = DateTime.UtcNow, lastSeen = DateTime.UtcNow;
    static int applied;
    static System.Threading.EventWaitHandle stop;
    static bool taskbar = true;

    static void Log(string text)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, new UTF8Encoding(false)); }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--stop")
        {
            try { using (var signal = System.Threading.EventWaitHandle.OpenExisting(@"Local\SSMS22ClassicIconStop")) signal.Set(); }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        bool owns;
        using (var mutex = new System.Threading.Mutex(true, @"Local\SSMS22ClassicIconWatcher", out owns))
        {
            if (!owns)
            {
                if (Array.IndexOf(args, "--relaunch") >= 0)
                    Process.Start(new ProcessStartInfo(File.Exists(Shortcut) ? Shortcut : Target) { UseShellExecute = true });
                else MessageBox.Show("旧图标助手已经在运行，请查看系统托盘。", "SSMS 旧图标助手");
                return 0;
            }
            try
            {
                stop = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, @"Local\SSMS22ClassicIconStop");
                bool attach = false;
                string iconPath = Path.Combine(Base, "SSMS-Classic.ico");
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--attach") attach = true;
                    else if (args[i] == "--relaunch") { }
                    else if (args[i] == "--window-only") taskbar = false;
                    else if (args[i] == "--icon" && i + 1 < args.Length) iconPath = Path.GetFullPath(args[++i]);
                    else throw new ArgumentException("支持的参数：--attach、--icon <ICO路径>、--window-only、--stop。");
                }
                if (!File.Exists(iconPath)) throw new FileNotFoundException("找不到旧图标。", iconPath);
                large = LoadImage(IntPtr.Zero, iconPath, 1, SystemInformation.IconSize.Width, SystemInformation.IconSize.Height, 0x10);
                small = LoadImage(IntPtr.Zero, iconPath, 1, SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height, 0x10);
                if (large == IntPtr.Zero || small == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "读取 ICO 失败。");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var menu = new ContextMenuStrip();
                menu.Items.Add("立即重新应用图标", null, delegate { Tick(null, EventArgs.Empty); });
                menu.Items.Add("查看运行日志", null, delegate { Process.Start("notepad.exe", "\"" + LogPath + "\""); });
                menu.Items.Add("退出助手并恢复窗口图标", null, delegate { Application.Exit(); });
                tray = new NotifyIcon { Text = "SSMS 22 旧图标助手", Icon = Icon.FromHandle(small), ContextMenuStrip = menu, Visible = true };
                Log("启动；图标：" + iconPath);
                TaskbarStyle.IconPath = iconPath;
                TaskbarStyle.Target = Target;
                Tick(null, EventArgs.Empty);
                if (!attach && !seenProcess)
                {
                    string launch = File.Exists(Shortcut) ? Shortcut : Target;
                    if (!File.Exists(launch)) throw new FileNotFoundException("找不到 SSMS 22。", launch);
                    Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
                    Log("启动 SSMS 22。");
                }
                timer = new Timer { Interval = 1500 };
                timer.Tick += Tick;
                timer.Start();
                Application.Run();
                return 0;
            }
            catch (Exception ex) { Log("错误：" + ex.Message); MessageBox.Show(ex.Message, "SSMS 旧图标助手", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            finally
            {
                if (timer != null) timer.Dispose();
                TaskbarStyle.Restore();
                Restore();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
                Log("助手已退出。");
                if (owns) mutex.ReleaseMutex();
                if (stop != null) stop.Dispose();
            }
        }
    }

    static bool Message(IntPtr window, uint message, int type, IntPtr value, out IntPtr result)
    {
        return SendMessageTimeout(window, message, new IntPtr(type), value, 0x2 | 0x20, 500, out result) != IntPtr.Zero;
    }

    static void Tick(object sender, EventArgs e)
    {
        if (stop != null && stop.WaitOne(0)) { Application.Exit(); return; }
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("SSMS"))
        {
            using (process)
            {
                try
                {
                    if (String.Equals(process.MainModule.FileName, Target, StringComparison.OrdinalIgnoreCase)) ids.Add((uint)process.Id);
                }
                catch (Exception ex) { Log("无法检查进程 " + process.Id + "：" + ex.Message); }
            }
        }
        if (ids.Count > 0) { seenProcess = true; lastSeen = DateTime.UtcNow; }
        if ((seenProcess && ids.Count == 0 && (DateTime.UtcNow - lastSeen).TotalSeconds > 10)
            || (!seenProcess && (DateTime.UtcNow - started).TotalSeconds > 120))
        { Application.Exit(); return; }
        EnumWindows(delegate(IntPtr window, IntPtr state)
        {
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (!ids.Contains(pid) || !IsWindowVisible(window)) return true;
            if (taskbar)
            {
                try { if (TaskbarStyle.Apply(window, pid)) Log("已设置独立任务栏图标 HWND=" + window); }
                catch (Exception ex) { Log("任务栏属性设置失败：" + ex.Message); }
            }
            IntPtr currentLarge, currentSmall;
            if (!Message(window, 0x7F, 1, IntPtr.Zero, out currentLarge) || !Message(window, 0x7F, 0, IntPtr.Zero, out currentSmall)) return true;
            if (currentLarge == large && currentSmall == small) return true;
            WindowState original;
            if (!originals.TryGetValue(window, out original) || original.Process != pid)
            {
                original = new WindowState { Process = pid, Large = currentLarge, Small = currentSmall };
                originals[window] = original;
            }
            IntPtr previous;
            bool okLarge = Message(window, 0x80, 1, large, out previous);
            bool okSmall = Message(window, 0x80, 0, small, out previous);
            if (okLarge && okSmall)
            {
                applied++;
                var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
                Log("已设置窗口图标 HWND=" + window + " PID=" + pid + " 标题=" + title);
                if (applied == 1 && tray != null) tray.ShowBalloonTip(3500, "已设置 SSMS 窗口图标", "请检查运行中的任务栏图标是否已变为旧版。", ToolTipIcon.Info);
            }
            else Log("窗口拒绝设置图标 HWND=" + window + "；如 SSMS 以管理员运行，助手也需同等权限。");
            return true;
        }, IntPtr.Zero);
    }

    static void Restore()
    {
        foreach (var item in originals)
        {
            uint pid; GetWindowThreadProcessId(item.Key, out pid);
            if (!IsWindow(item.Key) || pid != item.Value.Process) continue;
            IntPtr current, ignored;
            if (Message(item.Key, 0x7F, 1, IntPtr.Zero, out current) && current == large) Message(item.Key, 0x80, 1, item.Value.Large, out ignored);
            if (Message(item.Key, 0x7F, 0, IntPtr.Zero, out current) && current == small) Message(item.Key, 0x80, 0, item.Value.Small, out ignored);
        }
    }
}
