using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Management;

namespace CivilizationLauncher
{
    class Program
    {
        private static int port = 0;
        private static HttpListener listener;
        private static ManualResetEvent serverReady = new ManualResetEvent(false);
        private static bool isRunning = true;
        private static Process browserProcess;
        private static string appDir;

        private static string RepoZip = "https://github.com/civilkaserver-sketch/Civilka-version/archive/refs/heads/main.zip";
        private static string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CivilizationInstaller");
        private static string StateFile = Path.Combine(StateDir, "state.json");
        private static string CacheDir = Path.Combine(Path.GetTempPath(), "Civilka-version");
        private static string ExtractDir = Path.Combine(Path.GetTempPath(), "civilka_extracted");

        public class ProgressInfo
        {
            public int percent = 0;
            public string step = "";
            public bool active = false;
            public bool done = false;
            public string error = null;
            public bool cancelled = false;
        }

        private static ProgressInfo progress = new ProgressInfo();
        private static object progressLock = new object();
        private static volatile bool cancelRequested = false;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        private static object logLock = new object();
        private static string activeLogPath = null;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static string GetLogPath()
        {
            if (activeLogPath != null) return activeLogPath;

            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                using (var fs = new FileStream(local, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    // can write
                }
                activeLogPath = local;
                return activeLogPath;
            }
            catch { }

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CivilizationLauncher");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                activeLogPath = Path.Combine(dir, "launcher.log");
                return activeLogPath;
            }
            catch { }

            activeLogPath = Path.Combine(Path.GetTempPath(), "civilization_launcher.log");
            return activeLogPath;
        }

        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    string p = GetLogPath();
                    try
                    {
                        var fi = new FileInfo(p);
                        if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        {
                            string old = p + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(p, old);
                        }
                    }
                    catch { }

                    using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, Encoding.UTF8))
                    {
                        sw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg);
                    }
                }
            }
            catch { }
        }

        static void LogSystemDiagnostics()
        {
            try
            {
                Log("================================================================================");
                Log("Civilization Launcher Diagnostics Session");
                Log("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Log("OS: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
                Log("Process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " PID=" + Process.GetCurrentProcess().Id);
                Log("CLR Version: " + Environment.Version.ToString());
                Log("Machine: " + Environment.MachineName + " | User: " + Environment.UserName);
                Log("Base Dir: " + AppDomain.CurrentDomain.BaseDirectory);
                Log("Log File: " + GetLogPath());

                try
                {
                    var mem = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(mem))
                    {
                        Log(string.Format("Physical RAM: {0:F1} GB total, {1:F1} GB free", mem.ullTotalPhys / (1024.0 * 1024 * 1024), mem.ullAvailPhys / (1024.0 * 1024 * 1024)));
                    }
                }
                catch { }
                Log("Processors: " + Environment.ProcessorCount);

                try
                {
                    string defMc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
                    string detected = GetDetectedMcDir();
                    string installed = GetInstalledMcDir();
                    Log(string.Format("MC Paths: default='{0}' (exists={1}), detected='{2}', installed='{3}'", defMc, Directory.Exists(defMc), detected ?? "null", installed ?? "null"));
                    string java = FindJavaExe(detected ?? defMc);
                    Log("Java runtime: " + (java ?? "not found"));
                }
                catch (Exception ex)
                {
                    Log("Diagnostics error: " + ex.Message);
                }
                Log("================================================================================");
            }
            catch { }
        }

        static void CheckAndLogMinecraftCrash(string mcDir)
        {
            try
            {
                Log("--- Scanning for Minecraft crash details ---");
                string[] crashDirs = new string[] {
                    Path.Combine(mcDir, @"versions\Civilization\crash-reports"),
                    Path.Combine(mcDir, "crash-reports")
                };

                FileInfo latestCrash = null;
                foreach (var cd in crashDirs)
                {
                    if (Directory.Exists(cd))
                    {
                        var dirInfo = new DirectoryInfo(cd);
                        foreach (var f in dirInfo.GetFiles("crash-*.txt"))
                        {
                            if ((DateTime.Now - f.LastWriteTime).TotalMinutes < 15)
                            {
                                if (latestCrash == null || f.LastWriteTime > latestCrash.LastWriteTime)
                                    latestCrash = f;
                            }
                        }
                    }
                }

                if (latestCrash != null)
                {
                    Log("Found recent Minecraft crash report: " + latestCrash.FullName);
                    try
                    {
                        string[] lines = File.ReadAllLines(latestCrash.FullName, Encoding.UTF8);
                        int count = Math.Min(60, lines.Length);
                        Log("=== Crash Report Summary (Top " + count + " lines) ===");
                        for (int i = 0; i < count; i++)
                        {
                            Log("  " + lines[i]);
                        }
                        Log("=== End of Crash Report Summary ===");
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to read crash report: " + ex.Message);
                    }
                    return;
                }

                string[] logCandidates = new string[] {
                    Path.Combine(mcDir, @"versions\Civilization\logs\latest.log"),
                    Path.Combine(mcDir, @"logs\latest.log")
                };
                foreach (var lp in logCandidates)
                {
                    if (File.Exists(lp))
                    {
                        var fi = new FileInfo(lp);
                        if ((DateTime.Now - fi.LastWriteTime).TotalMinutes < 15)
                        {
                            Log("Reading tail of Minecraft log: " + lp);
                            try
                            {
                                using (var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var sr = new StreamReader(fs, Encoding.UTF8))
                                {
                                    var q = new Queue<string>();
                                    string line;
                                    while ((line = sr.ReadLine()) != null)
                                    {
                                        q.Enqueue(line);
                                        if (q.Count > 40) q.Dequeue();
                                    }
                                    Log("=== Minecraft latest.log Tail (Last " + q.Count + " lines) ===");
                                    foreach (var l in q) Log("  " + l);
                                    Log("=== End of latest.log Tail ===");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("Failed to read latest.log: " + ex.Message);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("CheckAndLogMinecraftCrash error: " + ex.Message);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("Unhandled: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "unknown"));
            appDir = AppDomain.CurrentDomain.BaseDirectory;
            LogSystemDiagnostics();
            ThreadPool.QueueUserWorkItem((s) => FetchRemoteShaInBackground());
            // In beta: do not auto-check launcher updates
            try
            {
                Process current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id != current.Id)
                    {
                        Log("Killing old launcher instance PID " + p.Id);
                        try
                        {
                            p.Kill();
                            p.WaitForExit(1500);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log("Cleanup error: " + ex.Message); }

            KillStaleBrowserProcesses();

            try { SetCurrentProcessExplicitAppUserModelID("Civilization.Launcher.CV.Beta"); } catch { }

            EnsureDefaultFiles();

            // Найти свободный порт для локального сервера
            for (int p = 52140; p < 52250; p++)
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", p));
                    try { listener.Prefixes.Add(string.Format("http://localhost:{0}/", p)); } catch { }
                    listener.Start();
                    port = p;
                    Log("Successfully bound to port " + p);
                    break;
                }
                catch (Exception ex)
                {
                    Log("Port " + p + " bind error: " + ex.Message);
                    if (listener != null)
                    {
                        try { listener.Close(); } catch { }
                        listener = null;
                    }
                    if (p > 52160) break;
                }
            }

            if (listener == null || port == 0)
            {
                Log("Failed to bind any port, exiting");
                MessageBox.Show("Не удалось занять локальный порт для лаунчера.", "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log("Port selected: " + port);

            // Фоновый поток обработки HTTP-запросов
            Thread serverThread = new Thread(ServerLoop);
            serverThread.IsBackground = true;
            serverThread.Start();
            serverReady.WaitOne(2000);
            Thread.Sleep(50);
            Log("Server started and ready");

            try { SetupScreenshotWatchers(); } catch { }
            try { AdServerBlocker.CleanAll(); } catch { }
            try { AdServerBlocker.SetupWatchers(); } catch { }

            // Запуск окна приложения
            LaunchBrowser();
            Log("LaunchBrowser completed");

            // Ожидание закрытия окна лаунчера
            while (isRunning)
            {
                Thread.Sleep(300);
            }
            Log("Main exiting, isRunning = false");

            try { listener.Stop(); } catch { }
            KillStaleBrowserProcesses();
            Log("Launcher shutdown complete");
        }

        private static void KillStaleBrowserProcesses()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe' OR Name = 'chrome.exe'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var cmd = obj["CommandLine"] as string;
                            if (!string.IsNullOrEmpty(cmd) && cmd.IndexOf("CivLauncherProfile", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                uint pid = (uint)obj["ProcessId"];
                                Log("Terminating stale browser process PID " + pid);
                                try
                                {
                                    var proc = Process.GetProcessById((int)pid);
                                    if (!proc.HasExited)
                                    {
                                        proc.Kill();
                                        proc.WaitForExit(1000);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("KillStaleBrowserProcesses warning: " + ex.Message);
            }
        }

        static void LaunchBrowser()
        {
            string url = string.Format("http://127.0.0.1:{0}/ui.html", port);
            string tempProfile = Path.Combine(Path.GetTempPath(), "CivLauncherProfileBeta");

            string[] edgePaths = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe")
            };

            string exeToUse = null;
            foreach (string p in edgePaths)
            {
                if (File.Exists(p)) { exeToUse = p; break; }
            }

            KillStaleBrowserProcesses();

            try
            {
                string defaultDir = Path.Combine(tempProfile, "Default");
                if (!Directory.Exists(defaultDir)) Directory.CreateDirectory(defaultDir);
                string prefPath = Path.Combine(defaultDir, "Preferences");
                string prefsContent = "{\"extensions\":{\"theme\":{\"id\":\"autogenerated_theme_id\"}},\"autogenerated\":{\"theme\":{\"color\":-15987700}}}";
                File.WriteAllText(prefPath, prefsContent, Encoding.UTF8);

                foreach (string f in Directory.GetFiles(defaultDir, "Favicons*"))
                {
                    try { File.Delete(f); } catch { }
                }
                string webAppDir = Path.Combine(defaultDir, "Web Applications");
                if (Directory.Exists(webAppDir))
                {
                    try { Directory.Delete(webAppDir, true); } catch { }
                }

                try
                {
                    string cacheDir = Path.Combine(tempProfile, "Default", "Cache");
                    if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
                    string codeCacheDir = Path.Combine(tempProfile, "Default", "Code Cache");
                    if (Directory.Exists(codeCacheDir)) Directory.Delete(codeCacheDir, true);
                }
                catch { }

                string lockFile = Path.Combine(tempProfile, "lockfile");
                if (File.Exists(lockFile))
                {
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            File.Delete(lockFile);
                            break;
                        }
                        catch
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Profile setup warning: " + ex.Message);
            }

            if (exeToUse != null)
            {
                Log("Starting browser process: " + exeToUse);
                var psi = new ProcessStartInfo();
                psi.FileName = exeToUse;
                psi.Arguments = string.Format(
                    "--app=\"{0}\" --window-size=1150,740 --user-data-dir=\"{1}\" --disable-http-cache " +
                    "--no-startup-boost --disable-features=WebAppIconInTitlebar,Translate --disable-background-mode " +
                    "--disable-background-networking --disable-component-update --disable-sync " +
                    "--no-first-run --no-default-browser-check",
                    url, tempProfile);
                psi.UseShellExecute = false;
                try
                {
                    browserProcess = Process.Start(psi);
                    Log("Browser started, PID " + (browserProcess != null ? browserProcess.Id.ToString() : "null"));
                    Thread styleThread = new Thread(StyleWindowLoop);
                    styleThread.IsBackground = true;
                    styleThread.Start();
                    return;
                }
                catch (Exception ex)
                {
                    Log("Browser start exception: " + ex.Message);
                }
            }

            Log("Fallback to default browser");
            Process.Start(url);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static System.Drawing.Icon appIconBig = null;
        private static System.Drawing.Icon appIconSmall = null;

        static void StyleWindowLoop()
        {
            try
            {
                int colorRef = 0x000C0C0C; // #0c0c0c in 0x00BBGGRR
                int dark = 1;

            IntPtr hIconBig = IntPtr.Zero;
            IntPtr hIconSmall = IntPtr.Zero;
            try
            {
                string icoPath = Path.Combine(appDir, "icon.ico");
                if (File.Exists(icoPath))
                {
                    appIconBig = new System.Drawing.Icon(icoPath, 64, 64);
                    appIconSmall = new System.Drawing.Icon(icoPath, 32, 32);
                    hIconBig = appIconBig.Handle;
                    hIconSmall = appIconSmall.Handle;
                }
                else
                {
                    var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (exeIcon != null)
                    {
                        appIconBig = exeIcon;
                        appIconSmall = exeIcon;
                        hIconBig = exeIcon.Handle;
                        hIconSmall = exeIcon.Handle;
                    }
                }
            }
            catch { }

            int styledCount = 0;
            IntPtr mainHwnd = IntPtr.Zero;

            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(200);

                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (IsWindowVisible(hWnd))
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        try
                        {
                            var p = Process.GetProcessById((int)pid);
                            string pname = p.ProcessName.ToLower();
                            if (pname.Contains("edge") || pname.Contains("chrome"))
                            {
                                var sb = new StringBuilder(256);
                                GetClassName(hWnd, sb, 256);
                                if (sb.ToString() == "Chrome_WidgetWin_1")
                                {
                                    var tb = new StringBuilder(256);
                                    GetWindowText(hWnd, tb, 256);
                                    string title = tb.ToString();
                                    if (string.IsNullOrEmpty(title) || title.Contains("Civilization") || title.Contains("Launcher") || title.Contains("127.0.0.1") || title.Contains("localhost"))
                                    {
                                        mainHwnd = hWnd;
                                        DwmSetWindowAttribute(hWnd, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
                                        DwmSetWindowAttribute(hWnd, 35, ref colorRef, 4); // DWMWA_CAPTION_COLOR
                                        DwmSetWindowAttribute(hWnd, 34, ref colorRef, 4); // DWMWA_BORDER_COLOR
                                        DwmSetWindowAttribute(hWnd, 36, ref colorRef, 4); // DWMWA_TEXT_COLOR (make title text invisible)
                                        SetWindowText(hWnd, "");
                                        SendMessage(hWnd, 0x0080, (IntPtr)0, IntPtr.Zero); // Remove icon from title bar
                                        if (hIconBig != IntPtr.Zero)
                                        {
                                            SendMessage(hWnd, 0x0080, (IntPtr)1, hIconBig); // Keep big icon for Alt+Tab / Taskbar
                                        }
                                        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027); // SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER
                                        styledCount++;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    return true;
                }, IntPtr.Zero);

                if (styledCount > 0 && i >= 10) break; // Window styled, exit polling loop
            }

            Log(string.Format("StyleWindowLoop finished styling. styledCount={0}, mainHwnd={1}", styledCount, mainHwnd));

            if (mainHwnd != IntPtr.Zero)
            {
                Log("Monitoring window HWND " + mainHwnd);
                bool wasGameRunning = false;
                while (isRunning)
                {
                    Thread.Sleep(300);
                    if (!IsWindow(mainHwnd))
                    {
                        if (browserProcess != null && !browserProcess.HasExited)
                        {
                            Thread.Sleep(500);
                            if (browserProcess != null && !browserProcess.HasExited) continue;
                        }
                        Log("Target window was closed by user");
                        break;
                    }

                    bool gameRunningNow = IsGameRunning();
                    if (wasGameRunning && !gameRunningNow)
                    {
                        Log("Minecraft exited while launcher window was open, restoring launcher focus");
                        try
                        {
                            ShowWindow(mainHwnd, 9); // SW_RESTORE
                            SetForegroundWindow(mainHwnd);
                        }
                        catch { }
                    }
                    wasGameRunning = gameRunningNow;
                }
            }
            else if (browserProcess != null && !browserProcess.HasExited)
            {
                Log("Target window HWND not found, falling back to browserProcess.WaitForExit()");
                browserProcess.WaitForExit();
            }
            else
            {
                Log("Neither target window nor running browser process was found.");
            }

            // Фишка лаунчера: если игра запущена в момент закрытия окна лаунчера,
            // не завершаем процесс лаунчера, а ждём в фоне закрытия Minecraft и заново открываем окно лаунчера!
            if (isRunning && IsGameRunning())
            {
                Log("Launcher window closed while Minecraft is running! Waiting in background until Minecraft exits...");
                KillStaleBrowserProcesses();

                while (isRunning && IsGameRunning())
                {
                    Thread.Sleep(1000);
                }

                if (isRunning)
                {
                    Log("Minecraft has exited! Re-opening launcher window...");
                    Thread.Sleep(600);
                    LaunchBrowser();
                    return; // LaunchBrowser стартует новый поток StyleWindowLoop
                }
            }

            Log("Browser window closed and Minecraft not running, terminating launcher");
            isRunning = false;
            }
            catch (Exception ex)
            {
                Log("StyleWindowLoop error: " + ex.ToString());
                isRunning = false;
            }
        }

        static void ServerLoop()
        {
            serverReady.Set();
            while (isRunning)
            {
                try
                {
                    var ctx = listener.GetContext();
                    ThreadPool.QueueUserWorkItem((state) => HandleRequest((HttpListenerContext)state), ctx);
                }
                catch
                {
                    if (!isRunning) break;
                }
            }
        }

        private static readonly byte[] TransparentIco = new byte[] {
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x20, 0x00, 0x30, 0x00,
            0x00, 0x00, 0x16, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        static byte[] GetEmbeddedResource(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string norm = name.Replace('\\', '/').TrimStart('/');

                using (var stream = asm.GetManifestResourceStream(norm))
                {
                    if (stream != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }

                string dotNorm = norm.Replace('/', '.');
                string[] allRes = asm.GetManifestResourceNames();
                foreach (string res in allRes)
                {
                    if (res.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                        res.Equals(dotNorm, StringComparison.OrdinalIgnoreCase) ||
                        res.EndsWith("." + dotNorm, StringComparison.OrdinalIgnoreCase) ||
                        res.EndsWith("." + norm, StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = asm.GetManifestResourceStream(res))
                        {
                            if (stream != null)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    stream.CopyTo(ms);
                                    return ms.ToArray();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static bool CreateDesktopShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktop, "CV Launcher Beta.lnk");
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = exePath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                    shortcut.Description = "Civilization Reborn Launcher (Beta)";
                    shortcut.IconLocation = exePath + ",0";
                    shortcut.Save();
                    Log("Created desktop shortcut: " + shortcutPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("CreateDesktopShortcut error: " + ex.Message);
            }
            return false;
        }

        static void EnsureDefaultFiles()
        {
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                var state = LoadState() ?? new Dictionary<string, object>();
                if (!state.ContainsKey("desktopShortcutCreated"))
                {
                    string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CV Launcher Beta.lnk");
                    if (!File.Exists(desktopLnk))
                    {
                        CreateDesktopShortcut();
                    }
                    state["desktopShortcutCreated"] = true;
                    string json = new JavaScriptSerializer().Serialize(state);
                    File.WriteAllText(StateFile, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log("EnsureDefaultFiles warning: " + ex.Message);
            }
        }

        static void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(path) || path == "ui.html")
                {
                    ServeFile(ctx, Path.Combine(appDir, "ui.html"), "text/html; charset=utf-8", "ui.html");
                    return;
                }

                if (path.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    string icoFile = Path.Combine(appDir, "icon.ico");
                    if (File.Exists(icoFile))
                    {
                        ServeBytes(ctx, File.ReadAllBytes(icoFile), "image/x-icon");
                        return;
                    }
                    byte[] embIco = GetEmbeddedResource("icon.ico");
                    if (embIco != null)
                    {
                        ServeBytes(ctx, embIco, "image/x-icon");
                        return;
                    }
                    string pngFile = Path.Combine(appDir, "assets", "icon.png");
                    if (File.Exists(pngFile))
                    {
                        ServeBytes(ctx, File.ReadAllBytes(pngFile), "image/png");
                        return;
                    }
                    byte[] embPng = GetEmbeddedResource("assets/icon.png");
                    if (embPng != null)
                    {
                        ServeBytes(ctx, embPng, "image/png");
                        return;
                    }
                    ServeBytes(ctx, TransparentIco, "image/x-icon");
                    return;
                }

                if (path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    ServeFile(ctx, Path.Combine(appDir, "manifest.json"), "application/manifest+json; charset=utf-8", "manifest.json");
                    return;
                }

                if (path.Equals("news.json", StringComparison.OrdinalIgnoreCase))
                {
                    SendJson(ctx, new object[] { });
                    return;
                }

                if (path.StartsWith("assets/pool/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("assets/news/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                if (path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    string filePath = Path.Combine(appDir, path.Replace('/', '\\'));
                    string mime = "application/octet-stream";
                    if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) mime = "image/png";
                    else if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) mime = "image/svg+xml";
                    else if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) mime = "image/x-icon";
                    else if (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) mime = "font/ttf";
                    else if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) mime = "text/css; charset=utf-8";
                    else if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) mime = "application/javascript; charset=utf-8";
                    ServeFile(ctx, filePath, mime, path);
                    return;
                }

                // API
                if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    HandleApi(ctx, path.Substring(4));
                    return;
                }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch { }
        }

        static void ServeFile(HttpListenerContext ctx, string filePath, string contentType, string resName = null)
        {
            byte[] bytes = null;
            if (!string.IsNullOrEmpty(resName))
            {
                bytes = GetEmbeddedResource(resName);
            }

            if (bytes == null && File.Exists(filePath))
            {
                try { bytes = File.ReadAllBytes(filePath); } catch { }
            }

            if (bytes == null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            try
            {
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.Headers.Add("Pragma", "no-cache");
                ctx.Response.Headers.Add("Expires", "0");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Flush();
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        static void ServeBytes(HttpListenerContext ctx, byte[] bytes, string contentType)
        {
            try
            {
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.Headers.Add("Pragma", "no-cache");
                ctx.Response.Headers.Add("Expires", "0");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Flush();
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        static void SendJson(HttpListenerContext ctx, object obj)
        {
            var jss = new JavaScriptSerializer();
            string json = jss.Serialize(obj);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            ctx.Response.Headers.Add("Pragma", "no-cache");
            ctx.Response.Headers.Add("Expires", "0");
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Flush();
            ctx.Response.Close();
        }

        static string ReadBody(HttpListenerContext ctx)
        {
            Encoding enc = Encoding.UTF8;
            try
            {
                if (ctx.Request.ContentEncoding != null && !ctx.Request.ContentEncoding.Equals(Encoding.Default))
                {
                    enc = ctx.Request.ContentEncoding;
                }
            }
            catch { }
            using (var reader = new StreamReader(ctx.Request.InputStream, enc))
            {
                return reader.ReadToEnd();
            }
        }

        static void SetProgress(int pct, string step, bool active, bool done, string error)
        {
            lock (progressLock)
            {
                progress.percent = pct;
                progress.step = step;
                progress.active = active;
                progress.done = done;
                progress.error = error;
                progress.cancelled = false;
            }
        }

        static void SetCancelledProgress(string step)
        {
            lock (progressLock)
            {
                progress.step = step;
                progress.active = false;
                progress.done = false;
                progress.error = null;
                progress.cancelled = true;
            }
        }

        class OperationCancelled : Exception
        {
            public OperationCancelled() : base("Остановлено пользователем") { }
        }

        static void CheckCancelled()
        {
            if (cancelRequested) throw new OperationCancelled();
        }

        static void HandleApi(HttpListenerContext ctx, string endpoint)
        {
            var jss = new JavaScriptSerializer();

            if (endpoint == "status")
            {
                string mcDir = GetInstalledMcDir();
                bool installed = !string.IsNullOrEmpty(mcDir) && TestIsInstalled(mcDir);
                if (string.IsNullOrEmpty(mcDir)) mcDir = GetDetectedMcDir();
                ThreadPool.QueueUserWorkItem((s) => { try { AdServerBlocker.CleanAll(mcDir); } catch { } });

                string installedAt = "";
                string localSha = "";
                var state = LoadState();
                if (state != null)
                {
                    if (state.ContainsKey("installedAt")) installedAt = (string)state["installedAt"];
                    if (state.ContainsKey("commitSha")) localSha = (string)state["commitSha"];
                }

                int jMaj = 0;
                int jNeed = 21;
                if (installed)
                {
                    var jinfo = GetRequiredJavaInfo(mcDir);
                    jNeed = jinfo.Major;
                    string jExe = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}\bin\java.exe", jinfo.Component));
                    jMaj = GetJavaMajor(jExe);
                }

                SendJson(ctx, new Dictionary<string, object> {
                    { "installed", installed },
                    { "updateAvailable", false },
                    { "localSha", localSha ?? "" },
                    { "remoteSha", "" },
                    { "launcherUpdateAvailable", false },
                    { "currentLauncherSha", "BETA" },
                    { "remoteLauncherSha", "" },
                    { "mcDir", mcDir ?? "" },
                    { "installedAt", installedAt },
                    { "javaMajor", jMaj },
                    { "javaNeed", jNeed },
                    { "javaOk", jMaj >= jNeed }
                });
                return;
            }

            if (endpoint == "check-update")
            {
                string mcDir = GetInstalledMcDir();
                bool installed = !string.IsNullOrEmpty(mcDir) && TestIsInstalled(mcDir);
                SendJson(ctx, new Dictionary<string, object> {
                    { "installed", installed },
                    { "updateAvailable", false },
                    { "localSha", "" },
                    { "remoteSha", "" }
                });
                return;
            }

            if (endpoint == "check-launcher-update")
            {
                SendJson(ctx, new Dictionary<string, object> {
                    { "updateAvailable", false },
                    { "currentSha", "BETA" },
                    { "remoteSha", "" }
                });
                return;
            }

            if (endpoint == "update-launcher")
            {
                ThreadPool.QueueUserWorkItem((s) => {
                    try
                    {
                        Log("=== Starting self-update for launcher ===");
                        string tempDir = Path.GetTempPath();
                        string newExePath = Path.Combine(tempDir, "CV_Launcher_new.exe");
                        string newUiPath = Path.Combine(tempDir, "ui_new.html");

                        string remoteSha = FetchLauncherRemoteShaSynchronous();
                        Log("Downloading new launcher binary from GitHub...");
                        bool okExe = DownloadFileFromRepo("CV Launcher.exe", newExePath);
                        if (!okExe)
                        {
                            Log("Failed to download CV Launcher.exe");
                            return;
                        }

                        Log("Downloading new ui.html from GitHub...");
                        bool okUi = DownloadFileFromRepo("ui.html", newUiPath);
                        if (!okUi)
                        {
                            Log("Warning: failed to download ui.html");
                        }

                        Log("Files downloaded successfully, triggering updater batch...");
                        ExecuteLauncherUpdate(newExePath, newUiPath, remoteSha);
                    }
                    catch (Exception ex)
                    {
                        Log("update-launcher error: " + ex.ToString());
                    }
                });

                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", true },
                    { "message", "Загрузка обновления начата..." }
                });
                return;
            }

            if (endpoint == "detect-mc")
            {
                string detected = GetDetectedMcDir();
                SendJson(ctx, new Dictionary<string, object> { { "path", detected ?? "" } });
                return;
            }

            if (endpoint == "pick-folder")
            {
                string chosen = null;
                string initial = GetTargetMcDir();
                var t = new Thread(() => {
                    chosen = ModernFolderBrowser.ShowDialog(IntPtr.Zero, "Выберите папку .minecraft", initial);
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();

                bool isInst = false;
                if (!string.IsNullOrEmpty(chosen))
                {
                    isInst = TestIsInstalled(chosen);
                    SaveCustomMcDir(chosen);
                    Log(string.Format("User selected custom Minecraft folder: '{0}' (Civilization detected: {1})", chosen, isInst));
                }

                SendJson(ctx, new Dictionary<string, object> { 
                    { "path", chosen ?? "" },
                    { "installed", isInst }
                });
                return;
            }

            if (endpoint == "progress")
            {
                lock (progressLock)
                {
                    SendJson(ctx, progress);
                }
                return;
            }

            if (endpoint == "cancel")
            {
                cancelRequested = true;
                SendJson(ctx, new Dictionary<string, object> { { "status", "cancelling" } });
                return;
            }

            if (endpoint == "install")
            {
                string body = ReadBody(ctx);
                Dictionary<string, object> data = null;
                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }
                string targetDir = null;
                if (data != null && data.ContainsKey("mcDir")) targetDir = (string)data["mcDir"];
                if (string.IsNullOrEmpty(targetDir)) targetDir = GetTargetMcDir();

                cancelRequested = false;
                SetProgress(5, "Подготовка к установке...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoInstall(targetDir));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "update")
            {
                string body = ReadBody(ctx);
                Dictionary<string, object> data = null;
                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }
                string targetDir = null;
                if (data != null && data.ContainsKey("mcDir")) targetDir = (string)data["mcDir"];
                if (string.IsNullOrEmpty(targetDir)) targetDir = GetInstalledMcDir();
                if (string.IsNullOrEmpty(targetDir)) targetDir = GetTargetMcDir();

                cancelRequested = false;
                SetProgress(10, "Проверяем наличие обновлений...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoUpdate(targetDir));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "uninstall")
            {
                string mcDir = GetInstalledMcDir();
                if (string.IsNullOrEmpty(mcDir))
                {
                    SendJson(ctx, new Dictionary<string, object> { { "error", "Civilization не установлена" } });
                    return;
                }
                cancelRequested = false;
                SetProgress(5, "Подготовка к удалению...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoUninstall(mcDir));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "open-folder")
            {
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    string target = Path.Combine(mcDir, @"versions\Civilization");
                    if (!Directory.Exists(target)) target = mcDir;
                    try { Process.Start("explorer.exe", target); } catch { }
                }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (endpoint == "open-log")
            {
                string logPath = GetLogPath();
                try
                {
                    if (!File.Exists(logPath))
                    {
                        File.WriteAllText(logPath, "Log initialized\r\n", Encoding.UTF8);
                    }
                    Process.Start("notepad.exe", logPath);
                    SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", logPath } });
                }
                catch (Exception ex)
                {
                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
                }
                return;
            }

            if (endpoint == "get-log")
            {
                string logPath = GetLogPath();
                string content = "";
                try
                {
                    if (File.Exists(logPath))
                    {
                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            var q = new Queue<string>();
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                q.Enqueue(line);
                                if (q.Count > 600) q.Dequeue();
                            }
                            content = string.Join("\r\n", q.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    content = "Error reading log: " + ex.Message;
                }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", logPath }, { "content", content } });
                return;
            }

            if (endpoint == "log-info")
            {
                string logPath = GetLogPath();
                long size = 0;
                try { if (File.Exists(logPath)) size = new FileInfo(logPath).Length; } catch { }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", logPath }, { "size", size } });
                return;
            }

            if (endpoint == "log")
            {
                string body = ReadBody(ctx);
                try
                {
                    var data = jss.Deserialize<Dictionary<string, object>>(body);
                    string lvl = data.ContainsKey("level") ? data["level"].ToString() : "UI";
                    string msg = data.ContainsKey("msg") ? data["msg"].ToString() : "";
                    string stack = data.ContainsKey("stack") ? data["stack"].ToString() : "";
                    string file = data.ContainsKey("file") ? data["file"].ToString() : "";
                    string line = data.ContainsKey("line") ? data["line"].ToString() : "";
                    string loc = (!string.IsNullOrEmpty(file) || !string.IsNullOrEmpty(line)) ? (" (" + file + ":" + line + ")") : "";
                    Log(string.Format("[{0}] {1}{2} {3}", lvl, msg, loc, stack).Trim());
                }
                catch { }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (endpoint == "open-screenshots")
            {
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                string sDir = null;
                if (!string.IsNullOrEmpty(mcDir))
                {
                    string[] candidates = new string[] {
                        Path.Combine(mcDir, @"versions\Civilization\screenshots"),
                        Path.Combine(mcDir, "screenshots")
                    };
                    foreach (string c in candidates)
                    {
                        if (Directory.Exists(c) && Directory.GetFiles(c, "*.png").Length > 0) { sDir = c; break; }
                    }
                    if (string.IsNullOrEmpty(sDir))
                    {
                        foreach (string c in candidates)
                        {
                            if (Directory.Exists(c)) { sDir = c; break; }
                        }
                    }
                }
                if (string.IsNullOrEmpty(sDir))
                {
                    sDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                }
                if (!Directory.Exists(sDir)) Directory.CreateDirectory(sDir);
                try { Process.Start("explorer.exe", sDir); } catch { }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (endpoint == "screenshots")
            {
                SetupScreenshotWatchers();
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                var list = new List<Dictionary<string, object>>();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    string[] candidates = new string[] {
                        Path.Combine(mcDir, @"versions\Civilization\screenshots"),
                        Path.Combine(mcDir, "screenshots")
                    };
                    var foundFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (string sDir in candidates)
                    {
                        if (Directory.Exists(sDir))
                        {
                            try
                            {
                                foreach (string f in Directory.GetFiles(sDir, "*.png"))
                                {
                                    var fi = new FileInfo(f);
                                    if (!foundFiles.ContainsKey(fi.Name))
                                    {
                                        foundFiles[fi.Name] = fi;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    var sorted = new List<FileInfo>(foundFiles.Values);
                    sorted.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

                    foreach (var fi in sorted)
                    {
                        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fi.FullName));
                        list.Add(new Dictionary<string, object> {
                            { "name", fi.Name },
                            { "date", fi.LastWriteTime.ToString("dd MMM yyyy, HH:mm") },
                            { "meta", string.Format("{0:0.#} MB", fi.Length / (1024.0 * 1024.0)) },
                            { "src", "/api/screenshot-file?b64=" + Uri.EscapeDataString(b64) + "&name=" + Uri.EscapeDataString(fi.Name) }
                        });
                    }
                }
                SendJson(ctx, list);
                return;
            }

            if (endpoint == "screenshots-status")
            {
                SendJson(ctx, new Dictionary<string, object> {
                    { "sig", GetScreenshotsSignature() }
                });
                return;
            }

            if (endpoint == "delete-screenshot")
            {
                string body = ReadBody(ctx);
                Dictionary<string, object> data = null;
                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }
                string name = null;
                if (data != null && data.ContainsKey("name")) name = data["name"].ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    string target = FindScreenshotByName(name);
                    if (target != null && File.Exists(target))
                    {
                        try
                        {
                            File.Delete(target);
                            Interlocked.Increment(ref screenshotsRevision);
                            Log("Deleted screenshot file: " + target);
                        }
                        catch (Exception ex)
                        {
                            Log("Error deleting screenshot: " + ex.Message);
                        }
                    }
                }
                SendJson(ctx, new Dictionary<string, object> { { "ok", true } });
                return;
            }

            if (endpoint.StartsWith("screenshot-file"))
            {
                string targetFile = null;

                // 1. Try Base64 path parameter ("b64")
                string b64 = ctx.Request.QueryString["b64"];
                if (!string.IsNullOrEmpty(b64))
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(b64);
                        string decoded = Encoding.UTF8.GetString(bytes);
                        if (File.Exists(decoded))
                        {
                            targetFile = decoded;
                        }
                    }
                    catch { }
                }

                // 2. Try file name parameter ("name" or "file")
                if (targetFile == null)
                {
                    string nameParam = ctx.Request.QueryString["name"] ?? ctx.Request.QueryString["file"];
                    if (!string.IsNullOrEmpty(nameParam))
                    {
                        targetFile = FindScreenshotByName(nameParam);
                    }
                }

                // 3. Try raw "path" parameter
                if (targetFile == null)
                {
                    string rawPath = ctx.Request.QueryString["path"];
                    if (!string.IsNullOrEmpty(rawPath))
                    {
                        if (File.Exists(rawPath))
                        {
                            targetFile = rawPath;
                        }
                        else
                        {
                            string fileName = Path.GetFileName(rawPath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                targetFile = FindScreenshotByName(fileName);
                            }
                        }
                    }
                }

                // 4. RawUrl fallback for query strings with percent-encoding or pluses
                if (targetFile == null)
                {
                    try
                    {
                        string rawUrl = ctx.Request.RawUrl;
                        int qIdx = rawUrl.IndexOf('?');
                        if (qIdx >= 0)
                        {
                            string query = rawUrl.Substring(qIdx + 1);
                            var pairs = query.Split('&');
                            foreach (var pair in pairs)
                            {
                                int eq = pair.IndexOf('=');
                                if (eq > 0)
                                {
                                    string key = pair.Substring(0, eq);
                                    string val = pair.Substring(eq + 1);
                                    if (key == "name" || key == "file" || key == "path")
                                    {
                                        string unescaped = Uri.UnescapeDataString(val);
                                        if (File.Exists(unescaped)) { targetFile = unescaped; break; }
                                        string found = FindScreenshotByName(Path.GetFileName(unescaped));
                                        if (found != null && File.Exists(found)) { targetFile = found; break; }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (targetFile != null && File.Exists(targetFile))
                {
                    ServeFile(ctx, targetFile, "image/png");
                    return;
                }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            if (endpoint == "game-status")
            {
                bool running = IsGameRunning();
                int pid = 0;
                try { if (activeGameProcess != null && !activeGameProcess.HasExited) pid = activeGameProcess.Id; } catch { }
                SendJson(ctx, new Dictionary<string, object> {
                    { "running", running },
                    { "pid", pid },
                    { "screenshotsSig", GetScreenshotsSignature() }
                });
                return;
            }

            if (endpoint == "start-game")
            {
                string mcDir = GetTargetMcDir();
                if (!TestIsInstalled(mcDir))
                {
                    SendJson(ctx, new Dictionary<string, object> {
                        { "ok", false },
                        { "msg", "Сборка ещё не установлена" }
                    });
                    return;
                }

                if (IsGameRunning())
                {
                    SendJson(ctx, new Dictionary<string, object> {
                        { "ok", true },
                        { "alreadyRunning", true },
                        { "msg", "Игра уже запущена!" }
                    });
                    return;
                }

                string err;
                bool started = LaunchCivilizationGame(mcDir, out err);
                int pid = 0;
                try { if (activeGameProcess != null && !activeGameProcess.HasExited) pid = activeGameProcess.Id; } catch { }
                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", started },
                    { "pid", pid },
                    { "msg", started ? "Игра запускается..." : (err ?? "Не удалось запустить игру") }
                });
                return;
            }

            if (endpoint == "stop-game")
            {
                string err;
                bool stopped = StopGame(out err);
                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", true },
                    { "stopped", stopped },
                    { "msg", stopped ? "Игра закрыта" : "Процесс игры не найден" }
                });
                return;
            }

            if (endpoint == "news")
            {
                SendJson(ctx, new object[] { });
                return;
            }

            if (endpoint == "create-desktop-shortcut")
            {
                bool created = CreateDesktopShortcut();
                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", created },
                    { "msg", created ? "Ярлык успешно создан на Рабочем столе!" : "Не удалось создать ярлык" }
                });
                return;
            }

            if (endpoint == "clean-servers")
            {
                string mcDir = GetTargetMcDir();
                int count = AdServerBlocker.CleanAll(mcDir);
                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", true },
                    { "removed", count },
                    { "msg", count > 0 ? string.Format("Удалено рекламных серверов: {0}", count) : "Список серверов чист (реклама не найдена)" }
                });
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        private static Process activeGameProcess = null;

        static bool StopGame(out string error)
        {
            error = null;
            bool killedAny = false;
            try
            {
                if (activeGameProcess != null)
                {
                    try
                    {
                        if (!activeGameProcess.HasExited)
                        {
                            int pid = activeGameProcess.Id;
                            var psi = new ProcessStartInfo("taskkill", string.Format("/PID {0} /T /F", pid));
                            psi.CreateNoWindow = true;
                            psi.UseShellExecute = false;
                            var p = Process.Start(psi);
                            if (p != null) p.WaitForExit(3000);
                            killedAny = true;
                        }
                    }
                    catch
                    {
                        try { activeGameProcess.Kill(); killedAny = true; } catch { }
                    }
                    activeGameProcess = null;
                }
            }
            catch (Exception ex)
            {
                Log("StopGame active proc error: " + ex.Message);
            }

            try
            {
                var procs = Process.GetProcessesByName("javaw");
                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            string title = p.MainWindowTitle ?? "";
                            if (title.IndexOf("Civilization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                int pid = p.Id;
                                try
                                {
                                    var psi = new ProcessStartInfo("taskkill", string.Format("/PID {0} /T /F", pid));
                                    psi.CreateNoWindow = true;
                                    psi.UseShellExecute = false;
                                    var kp = Process.Start(psi);
                                    if (kp != null) kp.WaitForExit(2000);
                                }
                                catch
                                {
                                    p.Kill();
                                }
                                killedAny = true;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("StopGame scan error: " + ex.Message);
            }

            activeGameProcess = null;
            return killedAny;
        }

        static bool IsGameRunning()
        {
            try
            {
                if (activeGameProcess != null)
                {
                    if (!activeGameProcess.HasExited) return true;
                    activeGameProcess = null;
                }
            }
            catch { }

            string[] procNames = new string[] { "javaw", "java" };
            foreach (string name in procNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                string title = p.MainWindowTitle ?? "";
                                if (title.IndexOf("Civilization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    title.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    activeGameProcess = p;
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return false;
        }

        private static List<FileSystemWatcher> screenshotWatchers = new List<FileSystemWatcher>();
        private static long screenshotsRevision = 1;
        private static string lastWatchedMcDir = null;

        static void SetupScreenshotWatchers()
        {
            try
            {
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                if (string.IsNullOrEmpty(mcDir) || mcDir == lastWatchedMcDir) return;
                lastWatchedMcDir = mcDir;

                foreach (var w in screenshotWatchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                }
                screenshotWatchers.Clear();

                var dirs = new List<string> {
                    Path.Combine(mcDir, "screenshots"),
                    Path.Combine(mcDir, @"versions\Civilization\screenshots")
                };

                foreach (var dir in dirs)
                {
                    try
                    {
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        var fsw = new FileSystemWatcher(dir, "*.png");
                        fsw.IncludeSubdirectories = false;
                        fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                        FileSystemEventHandler onChange = (s, e) => {
                            Interlocked.Increment(ref screenshotsRevision);
                        };
                        RenamedEventHandler onRename = (s, e) => {
                            Interlocked.Increment(ref screenshotsRevision);
                        };
                        fsw.Created += onChange;
                        fsw.Changed += onChange;
                        fsw.Deleted += onChange;
                        fsw.Renamed += onRename;
                        fsw.EnableRaisingEvents = true;
                        screenshotWatchers.Add(fsw);
                    }
                    catch (Exception ex)
                    {
                        Log("SetupScreenshotWatcher error for " + dir + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("SetupScreenshotWatchers general error: " + ex.Message);
            }
        }

        static string GetScreenshotsSignature()
        {
            try
            {
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                if (string.IsNullOrEmpty(mcDir)) return screenshotsRevision.ToString();

                var candidates = new string[] {
                    Path.Combine(mcDir, @"versions\Civilization\screenshots"),
                    Path.Combine(mcDir, "screenshots")
                };

                int totalCount = 0;
                long maxTicks = 0;
                foreach (var d in candidates)
                {
                    if (Directory.Exists(d))
                    {
                        try
                        {
                            var files = Directory.GetFiles(d, "*.png");
                            totalCount += files.Length;
                            foreach (var f in files)
                            {
                                long ticks = File.GetLastWriteTimeUtc(f).Ticks;
                                if (ticks > maxTicks) maxTicks = ticks;
                            }
                        }
                        catch { }
                    }
                }
                return string.Format("{0}_{1}_{2}", screenshotsRevision, totalCount, maxTicks);
            }
            catch
            {
                return screenshotsRevision.ToString();
            }
        }

        static string FindScreenshotByName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
            if (string.IsNullOrEmpty(mcDir)) return null;

            string[] candidates = new string[] {
                Path.Combine(mcDir, @"versions\Civilization\screenshots"),
                Path.Combine(mcDir, "screenshots")
            };

            var namesToTry = new List<string>();
            namesToTry.Add(fileName);
            try
            {
                string unescaped = Uri.UnescapeDataString(fileName);
                if (!namesToTry.Contains(unescaped)) namesToTry.Add(unescaped);
            }
            catch { }

            foreach (string dir in candidates)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string n in namesToTry)
                {
                    string direct = Path.Combine(dir, n);
                    if (File.Exists(direct)) return direct;
                }

                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.png"))
                    {
                        string fn = Path.GetFileName(f);
                        foreach (string n in namesToTry)
                        {
                            if (string.Equals(fn, n, StringComparison.OrdinalIgnoreCase)) return f;
                            if (string.Equals(fn.Replace("+", " "), n.Replace("+", " "), StringComparison.OrdinalIgnoreCase)) return f;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        static string FindTLauncherExe(string mcDir)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(mcDir))
            {
                candidates.Add(Path.Combine(mcDir, "TLauncher.exe"));
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\TLauncher.exe"));
            candidates.Add(@"E:\Games\.minecraft\TLauncher.exe");
            candidates.Add(@"D:\Games\.minecraft\TLauncher.exe");
            candidates.Add(@"C:\Games\.minecraft\TLauncher.exe");

            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\TLauncher.lnk");
                if (File.Exists(lnk))
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType);
                        dynamic shortcut = shell.CreateShortcut(lnk);
                        string target = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(target) && File.Exists(target)) candidates.Insert(0, target);
                    }
                }
            }
            catch { }

            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        static string FindJavaExe(string mcDir)
        {
            string[] candidateRuntimes = new string[] {
                "java-runtime-delta",
                "java-runtime-gamma",
                "java-runtime-beta",
                "java-runtime-alpha",
                "jre-x64",
                "java"
            };
            foreach (string r in candidateRuntimes)
            {
                string p1 = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}\bin\javaw.exe", r));
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(mcDir, string.Format(@"runtime\{0}\bin\javaw.exe", r));
                if (File.Exists(p2)) return p2;
            }

            string tlJvms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\jvms");
            if (Directory.Exists(tlJvms))
            {
                foreach (var d in Directory.GetDirectories(tlJvms))
                {
                    string jw = Path.Combine(d, @"bin\javaw.exe");
                    if (File.Exists(jw) && GetJavaMajor(jw) >= 21) return jw;
                }
            }

            string[] pfDirs = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string pf in pfDirs)
            {
                string jDir = Path.Combine(pf, "Java");
                if (Directory.Exists(jDir))
                {
                    foreach (var d in Directory.GetDirectories(jDir))
                    {
                        string jw = Path.Combine(d, @"bin\javaw.exe");
                        if (File.Exists(jw) && GetJavaMajor(jw) >= 21) return jw;
                    }
                }
                string msDir = Path.Combine(pf, @"Microsoft\jdk");
                if (Directory.Exists(msDir))
                {
                    foreach (var d in Directory.GetDirectories(msDir))
                    {
                        string jw = Path.Combine(d, @"bin\javaw.exe");
                        if (File.Exists(jw) && GetJavaMajor(jw) >= 21) return jw;
                    }
                }
            }

            string jh = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(jh))
            {
                string jw = Path.Combine(jh, @"bin\javaw.exe");
                if (File.Exists(jw)) return jw;
            }

            return "javaw.exe";
        }

        static bool LaunchCivilizationGame(string mcDir, out string error)
        {
            error = null;
            try
            {
                try { AdServerBlocker.CleanAll(mcDir); } catch { }
                string civDir = Path.Combine(mcDir, @"versions\Civilization");
                string jsonPath = Path.Combine(civDir, "Civilization.json");
                if (!File.Exists(jsonPath))
                {
                    error = "Файл версии Civilization.json не найден";
                    return false;
                }

                // 1. Считываем аккаунт из TlauncherProfiles.json
                string username = "gugomot";
                string uuid = "ee8cd5dee7bb400ba8e50e60210c3926";
                try
                {
                    string tpPath = Path.Combine(mcDir, "TlauncherProfiles.json");
                    if (!File.Exists(tpPath)) tpPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".minecraft\TlauncherProfiles.json");
                    if (File.Exists(tpPath))
                    {
                        var tp = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(tpPath, Encoding.UTF8));
                        string selUuid = tp.ContainsKey("selectedAccountUUID") ? (string)tp["selectedAccountUUID"] : null;
                        if (string.IsNullOrEmpty(selUuid) && tp.ContainsKey("freeAccountUUID")) selUuid = (string)tp["freeAccountUUID"];
                        if (tp.ContainsKey("accounts") && tp["accounts"] is Dictionary<string, object>)
                        {
                            var accs = (Dictionary<string, object>)tp["accounts"];
                            Dictionary<string, object> chosen = null;
                            if (!string.IsNullOrEmpty(selUuid) && accs.ContainsKey(selUuid))
                                chosen = accs[selUuid] as Dictionary<string, object>;
                            else
                            {
                                foreach (var v in accs.Values)
                                {
                                    chosen = v as Dictionary<string, object>;
                                    if (chosen != null) break;
                                }
                            }
                            if (chosen != null)
                            {
                                if (chosen.ContainsKey("username")) username = (string)chosen["username"];
                                else if (chosen.ContainsKey("displayName")) username = (string)chosen["displayName"];
                                if (chosen.ContainsKey("uuid")) uuid = ((string)chosen["uuid"]).Replace("-", "");
                            }
                        }
                    }
                }
                catch { }

                // 2. RAM из tlauncher-2.0.properties и синхронизация свойств
                int ramMb = 3000;
                try
                {
                    string tlProps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\tlauncher-2.0.properties");
                    if (File.Exists(tlProps))
                    {
                        var lines = new List<string>(File.ReadAllLines(tlProps, Encoding.UTF8));
                        bool hasVer = false;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var m = Regex.Match(lines[i], @"^minecraft\.memory\.ram2=(\d+)");
                            if (m.Success) ramMb = int.Parse(m.Groups[1].Value);

                            if (lines[i].StartsWith("login.version.game="))
                            {
                                lines[i] = "login.version.game=Civilization";
                                hasVer = true;
                            }
                            if (lines[i].StartsWith("minecraft.onlaunch="))
                            {
                                lines[i] = "minecraft.onlaunch=hide";
                            }
                        }
                        if (!hasVer) lines.Add("login.version.game=Civilization");
                        File.WriteAllLines(tlProps, lines.ToArray(), Encoding.UTF8);
                    }
                }
                catch { }
                if (ramMb < 1500) ramMb = 3000;

                // 3. Сборка classpath из библиотек Civilization.json
                var cpList = new List<string>();
                string jsonTxt = File.ReadAllText(jsonPath, Encoding.UTF8);
                var jss = new JavaScriptSerializer();
                jss.MaxJsonLength = int.MaxValue;
                var root = jss.Deserialize<Dictionary<string, object>>(jsonTxt);

                if (root.ContainsKey("libraries") && root["libraries"] is ArrayList)
                {
                    var libs = (ArrayList)root["libraries"];
                    foreach (object item in libs)
                    {
                        var lib = item as Dictionary<string, object>;
                        if (lib == null) continue;

                        if (lib.ContainsKey("rules") && lib["rules"] is ArrayList)
                        {
                            var rules = (ArrayList)lib["rules"];
                            bool allowed = false;
                            foreach (object rObj in rules)
                            {
                                var rule = rObj as Dictionary<string, object>;
                                if (rule == null) continue;
                                string action = (string)rule["action"];
                                if (rule.ContainsKey("os") && rule["os"] is Dictionary<string, object>)
                                {
                                    var osDict = (Dictionary<string, object>)rule["os"];
                                    string osName = (string)osDict["name"];
                                    if (osName == "windows") allowed = (action == "allow");
                                }
                                else
                                {
                                    allowed = (action == "allow");
                                }
                            }
                            if (!allowed) continue;
                        }

                        if (lib.ContainsKey("name"))
                        {
                            string coord = (string)lib["name"];
                            string[] parts = coord.Split(':');
                            if (parts.Length >= 3)
                            {
                                string group = parts[0].Replace('.', '\\');
                                string artifact = parts[1];
                                string version = parts[2];
                                string classifier = parts.Length > 3 ? ("-" + parts[3]) : "";
                                string jarName = string.Format("{0}-{1}{2}.jar", artifact, version, classifier);
                                string jarPath = Path.Combine(mcDir, "libraries", group, artifact, version, jarName);
                                if (File.Exists(jarPath) && !cpList.Contains(jarPath))
                                {
                                    cpList.Add(jarPath);
                                }
                            }
                        }
                    }
                }

                string civJar = Path.Combine(civDir, "Civilization.jar");
                if (File.Exists(civJar) && !cpList.Contains(civJar)) cpList.Add(civJar);

                string natives = Path.Combine(civDir, "natives");
                if (!Directory.Exists(natives)) Directory.CreateDirectory(natives);

                string javaExe = FindJavaExe(mcDir);

                // Если javaw не найден или classpath пуст, запасной вариант — TLauncher в фоне / свернуто
                if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe) || cpList.Count == 0)
                {
                    Log(string.Format("Direct launch not possible (javaExe='{0}', cpCount={1}). Trying TLauncher...", javaExe, cpList.Count));
                    string tlExe = FindTLauncherExe(mcDir);
                    if (!string.IsNullOrEmpty(tlExe) && File.Exists(tlExe))
                    {
                        var psiTl = new ProcessStartInfo(tlExe);
                        psiTl.WorkingDirectory = Path.GetDirectoryName(tlExe);
                        psiTl.WindowStyle = ProcessWindowStyle.Minimized;
                        activeGameProcess = Process.Start(psiTl);
                        Log("Started TLauncher as fallback PID: " + (activeGameProcess != null ? activeGameProcess.Id.ToString() : "null"));
                        return true;
                    }
                    error = "Не найден Java 21 и TLauncher.exe для запуска игры";
                    Log("Launch error: " + error);
                    return false;
                }

                // 4. Построение аргументов команды
                var args = new StringBuilder();
                args.Append("-Dos.name=\"Windows 10\" ");
                args.Append("-Dos.version=10.0 ");
                args.Append("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump ");
                args.Append("-Xss1M ");
                args.Append(string.Format("-Djava.library.path=\"{0}\" ", natives));
                args.Append(string.Format("-Djna.tmpdir=\"{0}\" ", natives));
                args.Append(string.Format("-Dorg.lwjgl.system.SharedLibraryExtractPath=\"{0}\" ", natives));
                args.Append(string.Format("-Dio.netty.native.workdir=\"{0}\" ", natives));
                args.Append("-Dminecraft.launcher.brand=minecraft-launcher ");
                args.Append("-Dminecraft.launcher.version=2.3.173 ");
                args.Append("-Djava.net.preferIPv6Addresses=system ");
                args.Append("-Djava.net.preferIPv4Stack=true ");
                args.Append(string.Format("-Xmx{0}M ", ramMb));
                args.Append("-XX:+UnlockExperimentalVMOptions ");
                args.Append("-XX:+UseG1GC ");
                args.Append("-XX:G1NewSizePercent=20 ");
                args.Append("-XX:G1ReservePercent=20 ");
                args.Append("-XX:MaxGCPauseMillis=50 ");
                args.Append("-XX:G1HeapRegionSize=32M ");
                args.Append("-Dfml.ignoreInvalidMinecraftCertificates=true ");
                args.Append("-Dfml.ignorePatchDiscrepancies=true ");
                args.Append(string.Format("-Dminecraft.applet.TargetDirectory=\"{0}\" ", civDir));
                args.Append(string.Format("-DlibraryDirectory=\"{0}\" ", Path.Combine(mcDir, "libraries")));
                args.Append(string.Format("-DLibLoader.modsFolder=\"{0}\" ", Path.Combine(civDir, "mods")));

                string log4j = Path.Combine(mcDir, @"assets\log_configs\client-1.12.xml");
                if (File.Exists(log4j))
                {
                    args.Append(string.Format("-Dlog4j.configurationFile=\"{0}\" ", log4j));
                }

                args.Append(string.Format("-cp \"{0}\" ", string.Join(";", cpList.ToArray())));
                args.Append("net.minecraftforge.bootstrap.ForgeBootstrap ");

                args.Append(string.Format("--username \"{0}\" ", username));
                args.Append("--version \"Civilization\" ");
                args.Append(string.Format("--gameDir \"{0}\" ", civDir));
                args.Append(string.Format("--assetsDir \"{0}\" ", Path.Combine(mcDir, "assets")));
                args.Append("--assetIndex \"26\" ");
                args.Append(string.Format("--uuid \"{0}\" ", uuid));
                args.Append("--accessToken \"null\" ");
                args.Append("--clientId \"null\" ");
                args.Append("--xuid \"null\" ");
                args.Append("--userType \"mojang\" ");
                args.Append("--versionType \"release\" ");
                args.Append("--width 925 ");
                args.Append("--height 530 ");
                args.Append("--launchTarget \"forge_client\"");

                Log("Launching game directly:");
                Log(string.Format("  Target Dir: {0}", civDir));
                Log(string.Format("  Java Exe:   {0}", javaExe));
                Log(string.Format("  RAM:        {0} MB", ramMb));
                Log(string.Format("  Player:     {0} (UUID: {1})", username, uuid));
                Log(string.Format("  Classpath:  {0} libraries", cpList.Count));
                Log(string.Format("  Arguments:  {0}", args.ToString()));

                var psi = new ProcessStartInfo();
                psi.FileName = javaExe;
                psi.Arguments = args.ToString();
                psi.WorkingDirectory = civDir;
                psi.UseShellExecute = false;

                activeGameProcess = Process.Start(psi);
                if (activeGameProcess != null)
                {
                    int pid = activeGameProcess.Id;
                    Log(string.Format("Game process spawned successfully! PID: {0}", pid));
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        try
                        {
                            Process proc = (Process)state;
                            proc.WaitForExit();
                            int exitCode = -1;
                            try { exitCode = proc.ExitCode; } catch { }
                            Log(string.Format("Game process (PID: {0}) exited with code: {1}", pid, exitCode));
                            if (exitCode != 0)
                            {
                                Log(string.Format("Non-zero exit code detected ({0}). Checking for crash reports...", exitCode));
                                Thread.Sleep(1000);
                                CheckAndLogMinecraftCrash(mcDir);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log("Game monitor thread exception: " + ex.Message);
                        }
                    }, activeGameProcess);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Launch failed with exception: " + ex.ToString());
                try
                {
                    string tlExe = FindTLauncherExe(mcDir);
                    if (!string.IsNullOrEmpty(tlExe) && File.Exists(tlExe))
                    {
                        Log("Attempting fallback to TLauncher after launch exception: " + tlExe);
                        var psiTl = new ProcessStartInfo(tlExe);
                        psiTl.WorkingDirectory = Path.GetDirectoryName(tlExe);
                        psiTl.WindowStyle = ProcessWindowStyle.Minimized;
                        activeGameProcess = Process.Start(psiTl);
                        return true;
                    }
                }
                catch (Exception exTl)
                {
                    Log("Fallback to TLauncher failed: " + exTl.Message);
                }
                error = ex.Message;
                return false;
            }
        }

        // ====================== ЛОГИКА УСТАНОВКИ (из Setup CV.bat) ======================

        static Dictionary<string, object> LoadState()
        {
            if (File.Exists(StateFile))
            {
                try
                {
                    string json = File.ReadAllText(StateFile, Encoding.UTF8);
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                }
                catch { }
            }
            return null;
        }

        private static string cachedRemoteSha = null;
        private static DateTime lastRemoteCheck = DateTime.MinValue;
        private static bool isCheckingUpdate = false;
        private static object updateCheckLock = new object();

        static string GetCachedOrFetchRemoteSha()
        {
            lock (updateCheckLock)
            {
                if (cachedRemoteSha != null && (DateTime.Now - lastRemoteCheck).TotalSeconds < 30)
                {
                    return cachedRemoteSha;
                }
            }

            if (!isCheckingUpdate)
            {
                ThreadPool.QueueUserWorkItem((s) => FetchRemoteShaInBackground());
            }

            return cachedRemoteSha;
        }

        static string FetchRemoteShaSynchronous()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/civilkaserver-sketch/Civilka-version/commits/main");
                req.UserAgent = "CivilizationLauncher";
                req.Timeout = 6000;
                req.Headers.Add("Authorization", "Bearer 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");
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (dict != null && dict.ContainsKey("sha"))
                    {
                        string sha = dict["sha"].ToString().Trim();
                        lock (updateCheckLock)
                        {
                            cachedRemoteSha = sha;
                            lastRemoteCheck = DateTime.Now;
                        }
                        return sha;
                    }
                }
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo("git", "ls-remote https://github.com/civilkaserver-sketch/Civilka-version.git refs/heads/main");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string outStr = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(6000);
                    if (!string.IsNullOrEmpty(outStr))
                    {
                        var parts = outStr.Split(new char[] { '\t', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && parts[0].Length >= 20)
                        {
                            string sha = parts[0].Trim();
                            lock (updateCheckLock)
                            {
                                cachedRemoteSha = sha;
                                lastRemoteCheck = DateTime.Now;
                            }
                            return sha;
                        }
                    }
                }
            }
            catch { }

            return cachedRemoteSha;
        }

        static void FetchRemoteShaInBackground()
        {
            if (isCheckingUpdate) return;
            isCheckingUpdate = true;
            try
            {
                string sha = FetchRemoteShaSynchronous();
                if (!string.IsNullOrEmpty(sha))
                {
                    Log("Remote version SHA detected: " + sha);
                }
            }
            catch (Exception ex)
            {
                Log("FetchRemoteShaInBackground error: " + ex.Message);
            }
            finally
            {
                isCheckingUpdate = false;
            }
        }

        private static string CurrentLauncherSha = "7698d7a";
        private static string cachedLauncherRemoteSha = null;
        private static DateTime lastLauncherCheck = DateTime.MinValue;
        private static bool isCheckingLauncherUpdate = false;
        private static object launcherCheckLock = new object();

        static string GetCurrentLauncherSha()
        {
            try
            {
                string p = Path.Combine(appDir ?? AppDomain.CurrentDomain.BaseDirectory, "version.sha");
                if (File.Exists(p))
                {
                    string s = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return CurrentLauncherSha;
        }

        static string GetCachedOrFetchLauncherRemoteSha()
        {
            lock (launcherCheckLock)
            {
                if (cachedLauncherRemoteSha != null && (DateTime.Now - lastLauncherCheck).TotalSeconds < 30)
                {
                    return cachedLauncherRemoteSha;
                }
            }

            if (!isCheckingLauncherUpdate)
            {
                ThreadPool.QueueUserWorkItem((s) => FetchLauncherRemoteShaInBackground());
            }

            return cachedLauncherRemoteSha;
        }

        static string FetchLauncherRemoteShaSynchronous()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/civilkaserver-sketch/CV-LAUNCHER/commits/main");
                req.UserAgent = "CV-Launcher";
                req.Timeout = 6000;
                req.Headers.Add("Authorization", "Bearer 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");
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (dict != null && dict.ContainsKey("sha"))
                    {
                        string sha = dict["sha"].ToString().Trim();
                        lock (launcherCheckLock)
                        {
                            cachedLauncherRemoteSha = sha;
                            lastLauncherCheck = DateTime.Now;
                        }
                        return sha;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("FetchLauncherRemoteSha error: " + ex.Message);
            }

            try
            {
                var psi = new ProcessStartInfo("git", "ls-remote https://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@github.com/civilkaserver-sketch/CV-LAUNCHER.git refs/heads/main");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string outStr = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(6000);
                    if (!string.IsNullOrEmpty(outStr))
                    {
                        var parts = outStr.Split(new char[] { '\t', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && parts[0].Length >= 20)
                        {
                            string sha = parts[0].Trim();
                            lock (launcherCheckLock)
                            {
                                cachedLauncherRemoteSha = sha;
                                lastLauncherCheck = DateTime.Now;
                            }
                            return sha;
                        }
                    }
                }
            }
            catch { }

            return cachedLauncherRemoteSha;
        }

        static void FetchLauncherRemoteShaInBackground()
        {
            if (isCheckingLauncherUpdate) return;
            isCheckingLauncherUpdate = true;
            try
            {
                string sha = FetchLauncherRemoteShaSynchronous();
                if (!string.IsNullOrEmpty(sha))
                {
                    Log("Remote launcher SHA detected: " + sha);
                }
            }
            catch (Exception ex)
            {
                Log("FetchLauncherRemoteShaInBackground error: " + ex.Message);
            }
            finally
            {
                isCheckingLauncherUpdate = false;
            }
        }

        static bool DownloadFileFromRepo(string repoFileName, string destinationPath)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string url = "https://api.github.com/repos/civilkaserver-sketch/CV-LAUNCHER/contents/" + Uri.EscapeDataString(repoFileName) + "?ref=main";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "CV-Launcher";
                req.Timeout = 60000;
                req.Headers.Add("Authorization", "Bearer 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");
                req.Accept = "application/vnd.github.v3.raw";

                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, read);
                    }
                }

                var fi = new FileInfo(destinationPath);
                if (fi.Exists && fi.Length > 1024)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("DownloadFileFromRepo (" + repoFileName + ") error: " + ex.Message);
            }
            return false;
        }

        static void ExecuteLauncherUpdate(string newExePath, string newUiPath, string remoteSha)
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string currentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string currentUi = Path.Combine(currentDir, "ui.html");
                string versionShaFile = Path.Combine(currentDir, "version.sha");

                string tempDir = Path.GetTempPath();
                string batPath = Path.Combine(tempDir, "cv_launcher_updater.bat");

                string newShaFile = Path.Combine(tempDir, "version_new.sha");
                if (!string.IsNullOrEmpty(remoteSha))
                {
                    File.WriteAllText(newShaFile, remoteSha);
                }

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 > nul");
                sb.AppendLine("timeout /t 1 /nobreak > nul");
                sb.AppendLine("taskkill /f /im \"CV Launcher.exe\" > nul 2>&1");
                sb.AppendLine(":retry_copy_exe");
                sb.AppendLine(string.Format("copy /y \"{0}\" \"{1}\" > nul 2>&1", newExePath, currentExe));
                sb.AppendLine("if errorlevel 1 (");
                sb.AppendLine("    timeout /t 1 /nobreak > nul");
                sb.AppendLine("    goto retry_copy_exe");
                sb.AppendLine(")");
                sb.AppendLine(string.Format("if exist \"{0}\" (", newUiPath));
                sb.AppendLine(string.Format("    copy /y \"{0}\" \"{1}\" > nul 2>&1", newUiPath, currentUi));
                sb.AppendLine(string.Format("    del /f /q \"{0}\" > nul 2>&1", newUiPath));
                sb.AppendLine(")");
                sb.AppendLine(string.Format("if exist \"{0}\" (", newShaFile));
                sb.AppendLine(string.Format("    copy /y \"{0}\" \"{1}\" > nul 2>&1", newShaFile, versionShaFile));
                sb.AppendLine(string.Format("    del /f /q \"{0}\" > nul 2>&1", newShaFile));
                sb.AppendLine(")");
                sb.AppendLine(string.Format("del /f /q \"{0}\" > nul 2>&1", newExePath));
                sb.AppendLine(string.Format("cd /d \"{0}\"", currentDir));
                sb.AppendLine(string.Format("start \"\" \"{0}\"", currentExe));
                sb.AppendLine("del \"%~f0\" & exit");

                File.WriteAllText(batPath, sb.ToString(), Encoding.GetEncoding(65001));

                Log("Launching updater batch: " + batPath);
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + batPath + "\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                Process.Start(psi);

                ThreadPool.QueueUserWorkItem((s) => {
                    Thread.Sleep(600);
                    Log("Terminating process for updater restart...");
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                Log("ExecuteLauncherUpdate error: " + ex.Message);
                throw;
            }
        }

        static void SaveState(string mcDir, List<string> files, string commitSha = null)
        {
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                var dict = LoadState() ?? new Dictionary<string, object>();
                dict["mcDir"] = mcDir;
                dict["installedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                dict["files"] = files;
                if (!string.IsNullOrEmpty(commitSha))
                {
                    dict["commitSha"] = commitSha;
                }
                string json = new JavaScriptSerializer().Serialize(dict);
                File.WriteAllText(StateFile, json, Encoding.UTF8);
            }
            catch { }
        }

        static void ClearState()
        {
            try
            {
                if (File.Exists(StateFile)) File.Delete(StateFile);
            }
            catch { }
        }

        static void SaveCustomMcDir(string mcDir)
        {
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                var dict = LoadState() ?? new Dictionary<string, object>();
                dict["mcDir"] = mcDir;
                if (!dict.ContainsKey("installedAt")) dict["installedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string json = new JavaScriptSerializer().Serialize(dict);
                File.WriteAllText(StateFile, json, Encoding.UTF8);
            }
            catch { }
        }

        static bool TestIsInstalled(string mcDir)
        {
            if (string.IsNullOrEmpty(mcDir)) return false;
            return File.Exists(Path.Combine(mcDir, @"versions\Civilization\Civilization.json"));
        }

        static List<string> GetAllPotentialMcDirs()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string> add = (p) =>
            {
                if (!string.IsNullOrEmpty(p))
                {
                    string trimmed = p.Trim().TrimEnd('\\', '/');
                    if (trimmed.Length > 0 && !seen.Contains(trimmed))
                    {
                        seen.Add(trimmed);
                        list.Add(trimmed);
                    }
                }
            };

            // 1. Saved state mcDir
            var state = LoadState();
            if (state != null && state.ContainsKey("mcDir"))
            {
                add((string)state["mcDir"]);
            }

            // 2. TLauncher properties
            try
            {
                string tlProps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\tlauncher-2.0.properties");
                if (File.Exists(tlProps))
                {
                    string[] lines = File.ReadAllLines(tlProps, Encoding.UTF8);
                    foreach (string l in lines)
                    {
                        var m = Regex.Match(l, @"^minecraft\.gamedir=(.+)$");
                        if (m.Success)
                        {
                            string raw = m.Groups[1].Value.Replace(@"\:", ":").Replace(@"\\", @"\").Trim();
                            add(raw);
                        }
                    }
                }
            }
            catch { }

            // 3. Official AppData .minecraft
            string appDataMc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            add(appDataMc);

            // 4. Common locations across all drive letters
            string[] subPaths = new string[] {
                @".minecraft",
                @"Games\.minecraft",
                @"Games\Minecraft\.minecraft"
            };

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                    {
                        foreach (string sub in subPaths)
                        {
                            try
                            {
                                string candidate = Path.Combine(drive.RootDirectory.FullName, sub);
                                add(candidate);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch
            {
                add(@"C:\Games\.minecraft");
                add(@"D:\Games\.minecraft");
                add(@"E:\Games\.minecraft");
                add(@"C:\.minecraft");
                add(@"D:\.minecraft");
                add(@"E:\.minecraft");
            }

            // 5. Check launcher_profiles.json in found directories for gameDir
            var currentList = new List<string>(list);
            foreach (string dir in currentList)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    string lp = Path.Combine(dir, "launcher_profiles.json");
                    if (File.Exists(lp))
                    {
                        string txt = File.ReadAllText(lp, Encoding.UTF8);
                        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(txt);
                        if (root != null && root.ContainsKey("profiles") && root["profiles"] is Dictionary<string, object>)
                        {
                            var profiles = (Dictionary<string, object>)root["profiles"];
                            foreach (var kvp in profiles)
                            {
                                var pObj = kvp.Value as Dictionary<string, object>;
                                if (pObj != null && pObj.ContainsKey("gameDir"))
                                {
                                    string gd = (string)pObj["gameDir"];
                                    add(gd);
                                    if (gd.EndsWith(@"versions\Civilization", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string parent = Path.GetDirectoryName(Path.GetDirectoryName(gd));
                                        add(parent);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return list;
        }

        public static string GetInstalledMcDir()
        {
            var potential = GetAllPotentialMcDirs();
            foreach (string dir in potential)
            {
                if (Directory.Exists(dir) && TestIsInstalled(dir))
                {
                    return dir;
                }
            }
            return null;
        }

        public static string GetDetectedMcDir()
        {
            // First check if already installed somewhere
            string inst = GetInstalledMcDir();
            if (!string.IsNullOrEmpty(inst)) return inst;

            // Otherwise, find first existing .minecraft candidate
            var potential = GetAllPotentialMcDirs();
            foreach (string dir in potential)
            {
                if (Directory.Exists(dir)) return dir;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        }

        static string GetTargetMcDir()
        {
            return GetInstalledMcDir() ?? GetDetectedMcDir();
        }

        public class JavaReqInfo
        {
            public string Component = "java-runtime-delta";
            public int Major = 21;
        }

        static JavaReqInfo GetRequiredJavaInfo(string mcDir, string sourceDir = null)
        {
            var def = new JavaReqInfo();
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(mcDir)) candidates.Add(Path.Combine(mcDir, @"versions\Civilization\Civilization.json"));
            if (!string.IsNullOrEmpty(sourceDir)) candidates.Add(Path.Combine(sourceDir, @"versions\Civilization\Civilization.json"));
            candidates.Add(Path.Combine(appDir, @"versions\Civilization\Civilization.json"));
            candidates.Add(Path.Combine(CacheDir, @"versions\Civilization\Civilization.json"));

            foreach (string p in candidates)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        string txt = File.ReadAllText(p, Encoding.UTF8);
                        var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(txt);
                        if (dict.ContainsKey("javaVersion"))
                        {
                            var jv = dict["javaVersion"] as Dictionary<string, object>;
                            if (jv != null)
                            {
                                if (jv.ContainsKey("majorVersion")) def.Major = Convert.ToInt32(jv["majorVersion"]);
                                if (jv.ContainsKey("component")) def.Component = (string)jv["component"];
                                return def;
                            }
                        }
                    }
                    catch { }
                    break;
                }
            }
            return def;
        }

        static int GetJavaMajor(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return 0;
            try
            {
                var psi = new ProcessStartInfo(exe, "-version");
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string outStr = p.StandardError.ReadToEnd();
                    p.WaitForExit(3000);
                    var m = Regex.Match(outStr, @"version\s+""(\d+)(?:\.(\d+))?");
                    if (m.Success)
                    {
                        int maj = int.Parse(m.Groups[1].Value);
                        if (maj == 1 && m.Groups[2].Success) maj = int.Parse(m.Groups[2].Value);
                        return maj;
                    }
                }
            }
            catch { }
            return 0;
        }

        static List<string> FindJavaHomes(string mcDir)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(mcDir))
            {
                string rt = Path.Combine(mcDir, "runtime");
                if (Directory.Exists(rt))
                {
                    foreach (string c in Directory.GetDirectories(rt))
                    {
                        string w = Path.Combine(c, "windows");
                        if (Directory.Exists(w))
                        {
                            foreach (string h in Directory.GetDirectories(w)) list.Add(h);
                        }
                    }
                }
            }

            string tlj = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\jvms");
            if (Directory.Exists(tlj))
            {
                foreach (string h in Directory.GetDirectories(tlj)) list.Add(h);
            }

            string envJh = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(envJh) && Directory.Exists(envJh)) list.Add(envJh);

            string[] roots = new string[] {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files (x86)\Java"
            };
            foreach (string r in roots)
            {
                if (Directory.Exists(r))
                {
                    foreach (string h in Directory.GetDirectories(r)) list.Add(h);
                }
            }
            return list;
        }

        static bool InstallJavaHome(string srcHome, string runtimeDir, string comp, int need)
        {
            try
            {
                string srcExe = Path.Combine(srcHome, @"bin\java.exe");
                if (!File.Exists(srcExe)) return false;
                if (srcHome.TrimEnd('\\').Equals(runtimeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return GetJavaMajor(srcExe) >= need;
                }

                string winDir = Path.GetDirectoryName(runtimeDir);
                if (!Directory.Exists(winDir)) Directory.CreateDirectory(winDir);
                if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
                Directory.CreateDirectory(runtimeDir);

                CopyDirectory(srcHome, runtimeDir);

                string ver = need + ".0";
                string relFile = Path.Combine(srcHome, "release");
                if (File.Exists(relFile))
                {
                    foreach (string l in File.ReadAllLines(relFile))
                    {
                        var m = Regex.Match(l, @"^JAVA_VERSION=""?([^""]+)""?");
                        if (m.Success) { ver = m.Groups[1].Value; break; }
                    }
                }
                File.WriteAllText(Path.Combine(winDir, ".version"), ver, Encoding.ASCII);

                string sha = Path.Combine(winDir, comp + ".sha1");
                if (File.Exists(sha)) File.Delete(sha);

                int mActual = GetJavaMajor(Path.Combine(runtimeDir, @"bin\java.exe"));
                return mActual >= need;
            }
            catch { return false; }
        }

        static string DownloadJava(int major)
        {
            string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            string url = string.Format("https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/{1}/jre/hotspot/normal/eclipse", major, arch);
            string zip = Path.Combine(Path.GetTempPath(), string.Format("temurin-jre-{0}.zip", major));
            string ext = Path.Combine(Path.GetTempPath(), string.Format("temurin-jre-{0}", major));

            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(ext)) Directory.Delete(ext, true);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            DownloadFileCancellable(url, zip, (pct) =>
            {
                SetProgress(90 + (pct / 10), string.Format("Загрузка Java {0} ({1}%)...", major, pct), true, false, null);
            });

            if (!File.Exists(zip) || new FileInfo(zip).Length < 1024 * 1024)
            {
                throw new Exception("Не удалось скачать Java 21");
            }

            ZipFile.ExtractToDirectory(zip, ext);
            File.Delete(zip);

            string[] javaExes = Directory.GetFiles(ext, "java.exe", SearchOption.AllDirectories);
            foreach (string je in javaExes)
            {
                if (Path.GetFileName(Path.GetDirectoryName(je)).Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(Path.GetDirectoryName(je));
                }
            }
            throw new Exception("В скачанном архиве Java не найден java.exe");
        }

        static bool EnsureJava(string mcDir, string sourceDir)
        {
            SetProgress(88, "Проверка Java 21...", true, false, null);
            var info = GetRequiredJavaInfo(mcDir, sourceDir);
            int need = info.Major;
            string comp = info.Component;

            string runtimeDir = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}", comp));
            string runtimeJava = Path.Combine(runtimeDir, @"bin\java.exe");
            int major = GetJavaMajor(runtimeJava);
            Log(string.Format("EnsureJava: required major={0} ({1}). Current runtime Java: '{2}' (detected major: {3})", need, comp, runtimeJava, major));
            if (major >= need) return true;

            Log("Searching installed Java installations on system...");
            foreach (string jh in FindJavaHomes(mcDir))
            {
                int m = GetJavaMajor(Path.Combine(jh, @"bin\java.exe"));
                if (m >= need)
                {
                    Log(string.Format("Found compatible Java in '{0}' (major {1}). Copying to runtime...", jh, m));
                    SetProgress(92, "Копирование Java 21 в лаунчер...", true, false, null);
                    if (InstallJavaHome(jh, runtimeDir, comp, need))
                    {
                        Log("Successfully installed Java from " + jh);
                        return true;
                    }
                }
            }

            // Скачивание
            Log(string.Format("No suitable local Java found. Downloading Eclipse Temurin Java {0}...", need));
            SetProgress(90, "Загрузка Java 21 (Eclipse Temurin)...", true, false, null);
            try
            {
                string downloaded = DownloadJava(need);
                Log("Downloaded Java into " + downloaded + ", installing into runtime...");
                SetProgress(97, "Установка Java 21...", true, false, null);
                bool res = InstallJavaHome(downloaded, runtimeDir, comp, need);
                Log("Java installation result: " + res);
                return res;
            }
            catch (OperationCancelled) { throw; }
            catch (Exception ex)
            {
                Log("EnsureJava download/install error: " + ex.ToString());
                SetProgress(97, "Предупреждение: Java не установлена: " + ex.Message, true, false, null);
                return false;
            }
        }

        static void CopyDirectory(string sourceDir, string targetDir)
        {
            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(sourceDir.Length).TrimStart('\\');
                string sub = Path.Combine(targetDir, rel);
                if (!Directory.Exists(sub)) Directory.CreateDirectory(sub);
            }
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(sourceDir.Length).TrimStart('\\');
                string target = Path.Combine(targetDir, rel);
                string td = Path.GetDirectoryName(target);
                if (!Directory.Exists(td)) Directory.CreateDirectory(td);
                File.Copy(file, target, true);
            }
        }

        static void CopyTracked(string src, string dst, string prefix, List<string> files)
        {
            if (!Directory.Exists(src)) return;
            if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);

            string[] allFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            for (int i = 0; i < allFiles.Length; i++)
            {
                if ((i & 31) == 0) CheckCancelled();
                string item = allFiles[i];
                string rel = item.Substring(src.Length).TrimStart('\\');
                string target = Path.Combine(dst, rel);
                string td = Path.GetDirectoryName(target);
                if (!Directory.Exists(td)) Directory.CreateDirectory(td);
                File.Copy(item, target, true);
                files.Add(Path.Combine(prefix, rel));
            }
        }

        static List<string> InstallFiles(string sourceDir, string mcDir)
        {
            var files = new List<string>();
            SetProgress(60, "Копирование библиотек Forge...", true, false, null);
            CopyTracked(Path.Combine(sourceDir, "libraries"), Path.Combine(mcDir, "libraries"), "libraries", files);

            SetProgress(75, "Копирование модов, ресурспаков и конфигов...", true, false, null);
            CopyTracked(Path.Combine(sourceDir, @"versions\Civilization"), Path.Combine(mcDir, @"versions\Civilization"), @"versions\Civilization", files);

            return files;
        }

        static void RemoveStale(string mcDir, List<string> oldFiles, List<string> newFiles)
        {
            if (oldFiles == null || oldFiles.Count == 0) return;
            var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (string o in oldFiles)
            {
                if (!newSet.Contains(o))
                {
                    string p = Path.Combine(mcDir, o);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); removed++; } catch { }
                    }
                }
            }

            string civDir = Path.Combine(mcDir, @"versions\Civilization");
            if (Directory.Exists(civDir))
            {
                CleanEmptyDirs(civDir);
            }
        }

        static void CleanEmptyDirs(string startLocation)
        {
            foreach (var directory in Directory.GetDirectories(startLocation))
            {
                CleanEmptyDirs(directory);
                if (Directory.GetFiles(directory).Length == 0 && Directory.GetDirectories(directory).Length == 0)
                {
                    try { Directory.Delete(directory, false); } catch { }
                }
            }
        }

        static void RegisterProfile(string mcDir, string civDst)
        {
            string[] configs = new string[] { "launcher_profiles.json", "TlauncherProfiles.json" };
            var jss = new JavaScriptSerializer();

            foreach (string cfg in configs)
            {
                string path = Path.Combine(mcDir, cfg);
                if (!File.Exists(path)) continue;
                try
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    var root = jss.Deserialize<Dictionary<string, object>>(content);
                    if (!root.ContainsKey("profiles")) root["profiles"] = new Dictionary<string, object>();

                    var profiles = root["profiles"] as Dictionary<string, object>;
                    if (profiles == null)
                    {
                        profiles = new Dictionary<string, object>();
                        root["profiles"] = profiles;
                    }

                    var civProfile = new Dictionary<string, object> {
                        { "created", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                        { "icon", "Furnace" },
                        { "lastUsed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                        { "lastVersionId", "Civilization" },
                        { "name", "Civilization" },
                        { "type", "custom" },
                        { "gameDir", civDst }
                    };

                    profiles["Civilization"] = civProfile;
                    File.WriteAllText(path, jss.Serialize(root), Encoding.UTF8);
                }
                catch { }
            }
        }

        static void UnregisterProfile(string mcDir)
        {
            string[] configs = new string[] { "launcher_profiles.json", "TlauncherProfiles.json" };
            var jss = new JavaScriptSerializer();

            foreach (string cfg in configs)
            {
                string path = Path.Combine(mcDir, cfg);
                if (!File.Exists(path)) continue;
                try
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    var root = jss.Deserialize<Dictionary<string, object>>(content);
                    if (root.ContainsKey("profiles"))
                    {
                        var profiles = root["profiles"] as Dictionary<string, object>;
                        if (profiles != null && profiles.ContainsKey("Civilization"))
                        {
                            profiles.Remove("Civilization");
                            File.WriteAllText(path, jss.Serialize(root), Encoding.UTF8);
                        }
                    }
                }
                catch { }
            }
        }

        static string DownloadBuild(bool isUpdate = false)
        {
            Log(string.Format("DownloadBuild started (isUpdate={0})", isUpdate));
            string labelPrep = isUpdate ? "Подготовка к загрузке обновления..." : "Подготовка к загрузке сборки...";
            int initialPct = isUpdate ? 25 : 5;
            SetProgress(initialPct, labelPrep, true, false, null);
            CheckCancelled();

            // Пробуем git
            try
            {
                Log("Attempting git clone from https://github.com/civilkaserver-sketch/Civilka-version.git...");
                var psi = new ProcessStartInfo("git", string.Format("clone --depth 1 \"https://github.com/civilkaserver-sketch/Civilka-version.git\" \"{0}\"", CacheDir));
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
                using (var p = Process.Start(psi))
                {
                    int waited = 0;
                    bool exited = false;
                    while (waited < 15000)
                    {
                        if (p.WaitForExit(200)) { exited = true; break; }
                        waited += 200;
                        if (cancelRequested)
                        {
                            try { p.Kill(); } catch { }
                            throw new OperationCancelled();
                        }
                    }
                    if (exited && p.ExitCode == 0 && File.Exists(Path.Combine(CacheDir, @"versions\Civilization\Civilization.json")))
                    {
                        Log("Git clone succeeded into " + CacheDir);
                        return CacheDir;
                    }
                    else
                    {
                        Log(string.Format("Git clone not usable (exited={0}, code={1})", exited, exited ? p.ExitCode : -1));
                    }
                }
            }
            catch (OperationCancelled) { throw; }
            catch (Exception ex)
            {
                Log("Git clone skipped or failed: " + ex.Message);
            }

            // Загрузка через zip
            CheckCancelled();
            string zipPath = Path.Combine(Path.GetTempPath(), "civilka_full.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(ExtractDir)) Directory.Delete(ExtractDir, true);

            Log("Downloading zip archive from " + RepoZip + " to " + zipPath);
            DownloadFileCancellable(RepoZip, zipPath, (pct) =>
            {
                int p = isUpdate ? (25 + (int)(pct * 0.45)) : (10 + (int)(pct * 0.4));
                string txt = isUpdate
                    ? string.Format("Загрузка обновления ({0}%)...", pct)
                    : string.Format("Загрузка сборки ({0}%)...", pct);
                SetProgress(p, txt, true, false, null);
            });

            if (!File.Exists(zipPath))
            {
                Log("Zip archive was not found after download");
                throw new Exception("Не удалось скачать архив сборки с GitHub.");
            }

            Log(string.Format("Zip archive downloaded successfully, size: {0} bytes", new FileInfo(zipPath).Length));
            CheckCancelled();
            SetProgress(isUpdate ? 72 : 52, isUpdate ? "Распаковка обновления..." : "Распаковка сборки...", true, false, null);
            Log("Extracting zip to: " + ExtractDir);
            ZipFile.ExtractToDirectory(zipPath, ExtractDir);
            File.Delete(zipPath);
            CheckCancelled();

            string unpacked = Path.Combine(ExtractDir, "Civilka-version-main");
            if (!Directory.Exists(unpacked))
            {
                foreach (string d in Directory.GetDirectories(ExtractDir))
                {
                    if (File.Exists(Path.Combine(d, @"versions\Civilization\Civilization.json")))
                    {
                        unpacked = d;
                        break;
                    }
                }
            }

            if (File.Exists(Path.Combine(unpacked, @"versions\Civilization\Civilization.json")))
            {
                Log("Build extracted and verified in " + unpacked);
                return unpacked;
            }
            Log("Civilization.json was not found in unpacked directory: " + unpacked);
            throw new Exception("В архиве отсутствует файл сборки Civilization.json");
        }

        static void DownloadFileCancellable(string url, string destPath, Action<int> onPercent)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            using (var done = new ManualResetEvent(false))
            {
                Exception dlError = null;
                bool dlCancelled = false;
                int lastPct = -1;

                wc.DownloadProgressChanged += (s, e) =>
                {
                    if (e.ProgressPercentage != lastPct)
                    {
                        lastPct = e.ProgressPercentage;
                        if (onPercent != null) onPercent(e.ProgressPercentage);
                    }
                    if (cancelRequested)
                    {
                        try { wc.CancelAsync(); } catch { }
                    }
                };
                wc.DownloadFileCompleted += (s, e) =>
                {
                    dlError = e.Error;
                    dlCancelled = e.Cancelled;
                    try { done.Set(); } catch { }
                };

                wc.DownloadFileAsync(new Uri(url), destPath);

                while (!done.WaitOne(200))
                {
                    if (cancelRequested)
                    {
                        try { wc.CancelAsync(); } catch { }
                    }
                }

                if (cancelRequested || dlCancelled) throw new OperationCancelled();
                if (dlError != null) throw dlError;
            }
        }

        static void DoInstall(string mcDir)
        {
            Log("=== Starting Installation: mcDir=" + mcDir + " ===");
            try
            {
                if (!Directory.Exists(mcDir)) Directory.CreateDirectory(mcDir);

                string sourceDir = DownloadBuild(false);
                CheckCancelled();
                Log("Copying files from: " + sourceDir);
                var files = InstallFiles(sourceDir, mcDir);
                Log(string.Format("Copied {0} files", files.Count));

                CheckCancelled();
                SetProgress(82, "Регистрация профиля Civilization...", true, false, null);
                RegisterProfile(mcDir, Path.Combine(mcDir, @"versions\Civilization"));
                string currentSha = cachedRemoteSha ?? FetchRemoteShaSynchronous();
                SaveState(mcDir, files, currentSha);
                Log("Profile registered and state saved (SHA: " + currentSha + ").");

                CheckCancelled();
                EnsureJava(mcDir, sourceDir);

                SetProgress(100, "Установка успешно завершена!", false, true, null);
                Log("=== Installation completed successfully ===");
            }
            catch (OperationCancelled)
            {
                cancelRequested = false;
                Log("Installation was cancelled by user.");
                SetCancelledProgress("Установка остановлена");
            }
            catch (Exception ex)
            {
                cancelRequested = false;
                Log("Installation error: " + ex.ToString());
                SetProgress(0, "Ошибка установки: " + ex.Message, false, false, ex.Message);
            }
        }

        static void DoUpdate(string mcDir)
        {
            Log("=== Starting Update: mcDir=" + mcDir + " ===");
            try
            {
                SetProgress(10, "Проверяем наличие обновлений...", true, false, null);
                Thread.Sleep(700);
                CheckCancelled();

                if (!Directory.Exists(mcDir)) Directory.CreateDirectory(mcDir);

                SetProgress(20, "Обновление найдено, подготовка к загрузке...", true, false, null);
                string sourceDir = DownloadBuild(true);
                CheckCancelled();

                SetProgress(75, "Распаковка и установка файлов...", true, false, null);

                var state = LoadState();
                var oldFiles = new List<string>();
                if (state != null && state.ContainsKey("files"))
                {
                    var arr = state["files"] as ArrayList;
                    if (arr != null)
                    {
                        foreach (var it in arr) oldFiles.Add(it.ToString());
                    }
                }
                Log(string.Format("Previous state had {0} files", oldFiles.Count));

                var files = InstallFiles(sourceDir, mcDir);
                Log(string.Format("Copied {0} new files", files.Count));

                SetProgress(88, "Очистка устаревших файлов...", true, false, null);
                RemoveStale(mcDir, oldFiles, files);

                SetProgress(94, "Обновление профиля игры...", true, false, null);
                RegisterProfile(mcDir, Path.Combine(mcDir, @"versions\Civilization"));
                string currentSha = cachedRemoteSha ?? FetchRemoteShaSynchronous();
                SaveState(mcDir, files, currentSha);
                Log("Profile registered and state saved (SHA: " + currentSha + ").");

                EnsureJava(mcDir, sourceDir);

                SetProgress(100, "Обновление успешно завершено!", false, true, null);
                Log("=== Update completed successfully ===");
            }
            catch (OperationCancelled)
            {
                cancelRequested = false;
                Log("Update was cancelled by user.");
                SetCancelledProgress("Обновление остановлено");
            }
            catch (Exception ex)
            {
                cancelRequested = false;
                Log("Update error: " + ex.ToString());
                SetProgress(0, "Ошибка обновления: " + ex.Message, false, false, ex.Message);
            }
        }

        static void DoUninstall(string mcDir)
        {
            Log("=== Starting Uninstall: mcDir=" + mcDir + " ===");
            try
            {
                SetProgress(10, "Удаление профиля Civilization...", true, false, null);
                UnregisterProfile(mcDir);

                var state = LoadState();
                var files = new List<string>();
                if (state != null && state.ContainsKey("files"))
                {
                    var arr = state["files"] as ArrayList;
                    if (arr != null)
                    {
                        foreach (var it in arr) files.Add(it.ToString());
                    }
                }

                SetProgress(30, "Удаление установленных файлов...", true, false, null);
                int delCount = 0;
                foreach (string rel in files)
                {
                    string p = Path.Combine(mcDir, rel);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); delCount++; } catch { }
                    }
                }
                Log(string.Format("Deleted {0} of {1} tracked files", delCount, files.Count));

                string civDst = Path.Combine(mcDir, @"versions\Civilization");
                if (Directory.Exists(civDst))
                {
                    try { Directory.Delete(civDst, true); Log("Deleted versions/Civilization directory"); } catch (Exception ex) { Log("Failed to delete civDst: " + ex.Message); }
                }

                string libRoot = Path.Combine(mcDir, "libraries");
                if (Directory.Exists(libRoot))
                {
                    CleanEmptyDirs(libRoot);
                }

                ClearState();
                SetProgress(100, "Сборка успешно удалена.", false, true, null);
                Log("=== Uninstall completed successfully ===");
            }
            catch (Exception ex)
            {
                Log("Uninstall error: " + ex.ToString());
                SetProgress(0, "Ошибка удаления: " + ex.Message, false, false, ex.Message);
            }
        }
    }

    public static class ModernFolderBrowser
    {
        public static string ShowDialog(IntPtr ownerHandle, string title = "Выберите папку .minecraft", string initialFolder = null)
        {
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogInternal();
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title))
                {
                    dialog.SetTitle(title);
                }

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    IShellItem folderItem;
                    Guid iidShellItem = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iidShellItem, out folderItem) == 0 && folderItem != null)
                    {
                        dialog.SetFolder(folderItem);
                    }
                }

                int hr = dialog.Show(ownerHandle);
                if (hr == 0) // S_OK
                {
                    IShellItem item;
                    dialog.GetResult(out item);
                    if (item != null)
                    {
                        string path;
                        item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                        return path;
                    }
                }
                return null;
            }
            catch
            {
                // Резервный вызов FolderBrowserDialog, если COM недоступен
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = title ?? "Выберите папку .minecraft";
                    fbd.ShowNewFolderButton = true;
                    if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                    {
                        fbd.SelectedPath = initialFolder;
                    }
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        return fbd.SelectedPath;
                    }
                }
                return null;
            }
        }

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        [CoClass(typeof(FileOpenDialogInternal))]
        private interface NativeFileOpenDialog : IFileOpenDialog
        {
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        [ClassInterface(ClassInterfaceType.None)]
        [TypeLibType(TypeLibTypeFlags.FCanCreate)]
        private class FileOpenDialogInternal
        {
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }

    public static class AdServerBlocker
    {
        private static readonly HashSet<string> BlockedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mc.dexland.org:25565", "mc.dexland.org",
            "mc.masedworld.net:25565", "mc.masedworld.net",
            "mc.skycave.pro:25565", "mc.skycave.pro",
            "mc.hypemc.pro:25565", "mc.hypemc.pro",
            "mc.vimemc.net:25565", "mc.vimemc.net",
            "go.bigcraft.pro:25565", "go.bigcraft.pro",
            "mc.mineblaze.net:25565", "mc.mineblaze.net"
        };

        private static readonly string[] BlockedKeywords = new string[]
        {
            "dexland", "masedworld", "skycave", "hypemc", "vimemc", "bigcraft", "mineblaze"
        };

        private static List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private static object cleanLock = new object();
        private static System.Threading.Timer debounceTimer = null;

        public static bool IsBlocked(string ip, string name)
        {
            if (!string.IsNullOrEmpty(ip))
            {
                string cleanIp = ip.Trim().ToLowerInvariant();
                if (BlockedIps.Contains(cleanIp)) return true;
                foreach (var kw in BlockedKeywords)
                {
                    if (cleanIp.Contains(kw)) return true;
                }
            }
            if (!string.IsNullOrEmpty(name))
            {
                string cleanName = name.Trim().ToLowerInvariant();
                foreach (var kw in BlockedKeywords)
                {
                    if (cleanName.Contains(kw)) return true;
                }
            }
            return false;
        }

        public static int CleanServersDat(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fs.Length];
                    fs.Read(bytes, 0, bytes.Length);
                }
                if (bytes == null || bytes.Length < 10) return 0;

                bool isGzip = (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b);
                byte[] rawBytes = bytes;
                if (isGzip)
                {
                    using (var msIn = new MemoryStream(bytes))
                    using (var gz = new GZipStream(msIn, CompressionMode.Decompress))
                    using (var msOut = new MemoryStream())
                    {
                        gz.CopyTo(msOut);
                        rawBytes = msOut.ToArray();
                    }
                }

                using (var inMs = new MemoryStream(rawBytes))
                using (var br = new BinaryReader(inMs))
                {
                    byte rootType = br.ReadByte();
                    if (rootType != 10) return 0;
                    string rootName = ReadNbtString(br);

                    using (var outMs = new MemoryStream())
                    using (var bw = new BinaryWriter(outMs))
                    {
                        bw.Write(rootType);
                        WriteNbtString(bw, rootName);

                        int removedCount = 0;
                        while (inMs.Position < inMs.Length)
                        {
                            byte tagType = br.ReadByte();
                            if (tagType == 0)
                            {
                                bw.Write((byte)0);
                                break;
                            }

                            string tagName = ReadNbtString(br);
                            if (tagType == 9 && tagName == "servers")
                            {
                                byte listElemType = br.ReadByte();
                                int count = ReadNbtInt(br);

                                var keptCompounds = new List<byte[]>();
                                for (int i = 0; i < count; i++)
                                {
                                    long startPos = inMs.Position;
                                    string sIp = null;
                                    string sName = null;

                                    while (true)
                                    {
                                        byte fType = br.ReadByte();
                                        if (fType == 0) break;
                                        string fName = ReadNbtString(br);
                                        object val = ReadNbtTagValue(fType, br);
                                        if (fName == "ip" && val is string) sIp = (string)val;
                                        if (fName == "name" && val is string) sName = (string)val;
                                    }
                                    long endPos = inMs.Position;
                                    int cLen = (int)(endPos - startPos);

                                    if (IsBlocked(sIp, sName))
                                    {
                                        removedCount++;
                                        Program.Log(string.Format("AdServerBlocker: removing ad server: ip='{0}', name='{1}' from {2}", sIp, sName, filePath));
                                    }
                                    else
                                    {
                                        byte[] cBytes = new byte[cLen];
                                        Array.Copy(rawBytes, (int)startPos, cBytes, 0, cLen);
                                        keptCompounds.Add(cBytes);
                                    }
                                }

                                bw.Write(tagType);
                                WriteNbtString(bw, tagName);
                                bw.Write(listElemType);
                                WriteNbtInt(bw, keptCompounds.Count);
                                foreach (var cb in keptCompounds)
                                {
                                    bw.Write(cb);
                                }
                            }
                            else
                            {
                                bw.Write(tagType);
                                WriteNbtString(bw, tagName);
                                CopyNbtTagValue(tagType, br, bw);
                            }
                        }

                        if (removedCount > 0)
                        {
                            bw.Flush();
                            byte[] outBytes = outMs.ToArray();
                            if (isGzip)
                            {
                                using (var gzMs = new MemoryStream())
                                {
                                    using (var gz = new GZipStream(gzMs, CompressionMode.Compress, true))
                                    {
                                        gz.Write(outBytes, 0, outBytes.Length);
                                    }
                                    outBytes = gzMs.ToArray();
                                }
                            }

                            try { File.WriteAllBytes(filePath + ".bak", bytes); } catch { }
                            File.WriteAllBytes(filePath, outBytes);
                            Program.Log(string.Format("AdServerBlocker: purged {0} ad servers from {1}", removedCount, filePath));
                        }
                        return removedCount;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log("AdServerBlocker CleanServersDat error (" + filePath + "): " + ex.Message);
                return 0;
            }
        }

        public static int CleanInnerStateJson(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            try
            {
                string json;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }
                if (string.IsNullOrEmpty(json)) return 0;

                var jss = new JavaScriptSerializer();
                var list = jss.Deserialize<ArrayList>(json);
                if (list == null || list.Count == 0) return 0;

                var kept = new ArrayList();
                int removed = 0;
                foreach (object item in list)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null) continue;
                    string addr = dict.ContainsKey("address") ? (string)dict["address"] : null;
                    string name = dict.ContainsKey("name") ? (string)dict["name"] : null;
                    if (IsBlocked(addr, name))
                    {
                        removed++;
                        Program.Log(string.Format("AdServerBlocker: removing InnerState ad server: {0} ({1}) from {2}", name, addr, filePath));
                    }
                    else
                    {
                        kept.Add(dict);
                    }
                }

                if (removed > 0)
                {
                    try { File.WriteAllText(filePath + ".bak", json, Encoding.UTF8); } catch { }
                    string newJson = jss.Serialize(kept);
                    File.WriteAllText(filePath, newJson, Encoding.UTF8);
                    Program.Log(string.Format("AdServerBlocker: cleaned InnerState {0}, purged {1} ad servers.", filePath, removed));
                }
                return removed;
            }
            catch (Exception ex)
            {
                Program.Log("AdServerBlocker CleanInnerStateJson error (" + filePath + "): " + ex.Message);
                return 0;
            }
        }

        public static bool TryBlockHosts()
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string hostsPath = Path.Combine(sysDir, @"drivers\etc\hosts");
                if (!File.Exists(hostsPath)) return false;

                string content = File.ReadAllText(hostsPath, Encoding.UTF8);
                string[] domains = new string[] {
                    "mc.dexland.org",
                    "mc.masedworld.net",
                    "mc.skycave.pro",
                    "mc.hypemc.pro",
                    "mc.vimemc.net",
                    "go.bigcraft.pro",
                    "mc.mineblaze.net"
                };

                var missing = new List<string>();
                foreach (var d in domains)
                {
                    if (content.IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        missing.Add("127.0.0.1 " + d);
                    }
                }

                if (missing.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine("# Civilization Launcher - Blocked TLauncher Ad Servers");
                    foreach (var m in missing) sb.AppendLine(m);
                    File.AppendAllText(hostsPath, sb.ToString(), Encoding.UTF8);
                    Program.Log("AdServerBlocker: successfully blocked ad domains in hosts file");
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static int CleanAll(string mcDir = null)
        {
            int totalRemoved = 0;
            try
            {
                if (string.IsNullOrEmpty(mcDir)) mcDir = Program.GetInstalledMcDir();
                if (string.IsNullOrEmpty(mcDir)) mcDir = Program.GetDetectedMcDir();

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    candidates.Add(Path.Combine(mcDir, @"versions\Civilization\servers.dat"));
                    candidates.Add(Path.Combine(mcDir, "servers.dat"));
                    candidates.Add(Path.Combine(mcDir, "InnerStateServer-1.3.json"));
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                candidates.Add(Path.Combine(appData, @".minecraft\versions\Civilization\servers.dat"));
                candidates.Add(Path.Combine(appData, @".minecraft\servers.dat"));
                candidates.Add(Path.Combine(appData, @".minecraft\InnerStateServer-1.3.json"));
                candidates.Add(Path.Combine(appData, @".tlauncher\InnerStateServer-1.3.json"));

                var checkedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in candidates)
                {
                    if (string.IsNullOrEmpty(p) || checkedSet.Contains(p) || !File.Exists(p)) continue;
                    checkedSet.Add(p);

                    if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        totalRemoved += CleanInnerStateJson(p);
                    }
                    else if (p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    {
                        totalRemoved += CleanServersDat(p);
                    }
                }

                TryBlockHosts();
            }
            catch (Exception ex)
            {
                Program.Log("AdServerBlocker CleanAll error: " + ex.Message);
            }
            return totalRemoved;
        }

        public static void SetupWatchers()
        {
            try
            {
                string mcDir = Program.GetInstalledMcDir() ?? Program.GetDetectedMcDir();
                var dirs = new List<string>();

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] list = new string[] {
                    mcDir,
                    !string.IsNullOrEmpty(mcDir) ? Path.Combine(mcDir, @"versions\Civilization") : null,
                    Path.Combine(appData, @".minecraft"),
                    Path.Combine(appData, @".minecraft\versions\Civilization"),
                    Path.Combine(appData, @".tlauncher")
                };

                foreach (var d in list)
                {
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && !dirs.Contains(d))
                        dirs.Add(d);
                }

                foreach (var w in watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                }
                watchers.Clear();

                foreach (var dir in dirs)
                {
                    try
                    {
                        var fsw = new FileSystemWatcher(dir);
                        fsw.IncludeSubdirectories = false;
                        fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                        FileSystemEventHandler onEv = (s, e) => {
                            string fn = Path.GetFileName(e.FullPath);
                            if (fn.Equals("servers.dat", StringComparison.OrdinalIgnoreCase) ||
                                fn.StartsWith("InnerStateServer", StringComparison.OrdinalIgnoreCase))
                            {
                                TriggerDebounce(mcDir);
                            }
                        };
                        fsw.Created += onEv;
                        fsw.Changed += onEv;
                        fsw.EnableRaisingEvents = true;
                        watchers.Add(fsw);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Program.Log("AdServerBlocker SetupWatchers error: " + ex.Message);
            }
        }

        private static void TriggerDebounce(string mcDir)
        {
            lock (cleanLock)
            {
                if (debounceTimer != null)
                {
                    try { debounceTimer.Dispose(); } catch { }
                    debounceTimer = null;
                }
                debounceTimer = new System.Threading.Timer(state => {
                    try { CleanAll(mcDir); } catch { }
                }, null, 600, System.Threading.Timeout.Infinite);
            }
        }

        private static short ReadNbtShort(BinaryReader br)
        {
            byte[] b = br.ReadBytes(2);
            Array.Reverse(b);
            return BitConverter.ToInt16(b, 0);
        }
        private static void WriteNbtShort(BinaryWriter bw, short v)
        {
            byte[] b = BitConverter.GetBytes(v);
            Array.Reverse(b);
            bw.Write(b);
        }
        private static int ReadNbtInt(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }
        private static void WriteNbtInt(BinaryWriter bw, int v)
        {
            byte[] b = BitConverter.GetBytes(v);
            Array.Reverse(b);
            bw.Write(b);
        }
        private static string ReadNbtString(BinaryReader br)
        {
            short len = ReadNbtShort(br);
            if (len <= 0) return "";
            byte[] b = br.ReadBytes(len);
            return Encoding.UTF8.GetString(b);
        }
        private static void WriteNbtString(BinaryWriter bw, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                WriteNbtShort(bw, 0);
                return;
            }
            byte[] b = Encoding.UTF8.GetBytes(s);
            WriteNbtShort(bw, (short)b.Length);
            bw.Write(b);
        }

        private static object ReadNbtTagValue(byte type, BinaryReader br)
        {
            switch (type)
            {
                case 1: return br.ReadByte();
                case 2: return ReadNbtShort(br);
                case 3: return ReadNbtInt(br);
                case 4: {
                    byte[] b = br.ReadBytes(8);
                    Array.Reverse(b);
                    return BitConverter.ToInt64(b, 0);
                }
                case 5: {
                    byte[] b = br.ReadBytes(4);
                    Array.Reverse(b);
                    return BitConverter.ToSingle(b, 0);
                }
                case 6: {
                    byte[] b = br.ReadBytes(8);
                    Array.Reverse(b);
                    return BitConverter.ToDouble(b, 0);
                }
                case 7: {
                    int len = ReadNbtInt(br);
                    return br.ReadBytes(len);
                }
                case 8: return ReadNbtString(br);
                case 9: {
                    byte subType = br.ReadByte();
                    int len = ReadNbtInt(br);
                    for (int i = 0; i < len; i++) ReadNbtTagValue(subType, br);
                    return "[List]";
                }
                case 10: {
                    while (true)
                    {
                        byte subType = br.ReadByte();
                        if (subType == 0) break;
                        ReadNbtString(br);
                        ReadNbtTagValue(subType, br);
                    }
                    return "[Compound]";
                }
                case 11: {
                    int len = ReadNbtInt(br);
                    for (int i = 0; i < len; i++) ReadNbtInt(br);
                    return "[IntArray]";
                }
                case 12: {
                    int len = ReadNbtInt(br);
                    for (int i = 0; i < len; i++) {
                        byte[] b = br.ReadBytes(8);
                    }
                    return "[LongArray]";
                }
                default: return null;
            }
        }

        private static void CopyNbtTagValue(byte type, BinaryReader br, BinaryWriter bw)
        {
            switch (type)
            {
                case 1: bw.Write(br.ReadByte()); break;
                case 2: WriteNbtShort(bw, ReadNbtShort(br)); break;
                case 3: WriteNbtInt(bw, ReadNbtInt(br)); break;
                case 4: {
                    byte[] b = br.ReadBytes(8);
                    bw.Write(b);
                    break;
                }
                case 5: {
                    byte[] b = br.ReadBytes(4);
                    bw.Write(b);
                    break;
                }
                case 6: {
                    byte[] b = br.ReadBytes(8);
                    bw.Write(b);
                    break;
                }
                case 7: {
                    int len = ReadNbtInt(br);
                    WriteNbtInt(bw, len);
                    bw.Write(br.ReadBytes(len));
                    break;
                }
                case 8: {
                    string s = ReadNbtString(br);
                    WriteNbtString(bw, s);
                    break;
                }
                case 9: {
                    byte subType = br.ReadByte();
                    int len = ReadNbtInt(br);
                    bw.Write(subType);
                    WriteNbtInt(bw, len);
                    for (int i = 0; i < len; i++) CopyNbtTagValue(subType, br, bw);
                    break;
                }
                case 10: {
                    while (true)
                    {
                        byte subType = br.ReadByte();
                        bw.Write(subType);
                        if (subType == 0) break;
                        string name = ReadNbtString(br);
                        WriteNbtString(bw, name);
                        CopyNbtTagValue(subType, br, bw);
                    }
                    break;
                }
                case 11: {
                    int len = ReadNbtInt(br);
                    WriteNbtInt(bw, len);
                    bw.Write(br.ReadBytes(len * 4));
                    break;
                }
                case 12: {
                    int len = ReadNbtInt(br);
                    WriteNbtInt(bw, len);
                    bw.Write(br.ReadBytes(len * 8));
                    break;
                }
            }
        }
    }
}
