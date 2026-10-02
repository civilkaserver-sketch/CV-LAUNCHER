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

namespace CivilizationLauncher
{
    class Program
    {
        private static int port = 0;
        private static HttpListener listener;
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
        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                    using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, Encoding.UTF8))
                    {
                        sw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg);
                    }
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("Unhandled: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "unknown"));
            appDir = AppDomain.CurrentDomain.BaseDirectory;
            Log("=== Launcher started ===");
            try
            {
                Process current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id != current.Id)
                    {
                        Log("Killing old instance PID " + p.Id);
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log("Cleanup error: " + ex.Message); }

            try { SetCurrentProcessExplicitAppUserModelID("Civilization.Launcher.CV"); } catch { }

            // Найти свободный порт для локального сервера
            for (int p = 52140; p < 52250; p++)
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", p));
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
                    if (p > 52145) break; // don't spam 100 ports if HttpListener permission denied
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
            Log("Server started");

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
        }

        static void LaunchBrowser()
        {
            string url = string.Format("http://127.0.0.1:{0}/ui.html", port);
            string tempProfile = Path.Combine(Path.GetTempPath(), "CivLauncherProfile");

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

                string lockFile = Path.Combine(tempProfile, "lockfile");
                if (File.Exists(lockFile))
                {
                    try { File.Delete(lockFile); }
                    catch
                    {
                        try
                        {
                            var psiKill = new ProcessStartInfo("cmd.exe", "/c wmic process where \"CommandLine like '%CivLauncherProfile%' and Name='msedge.exe'\" call terminate");
                            psiKill.CreateNoWindow = true;
                            psiKill.UseShellExecute = false;
                            using (var kp = Process.Start(psiKill)) kp.WaitForExit(2000);
                        }
                        catch { }
                        try { File.Delete(lockFile); } catch { }
                    }
                }
            }
            catch { }

            if (exeToUse != null)
            {
                Log("Starting browser process: " + exeToUse);
                var psi = new ProcessStartInfo();
                psi.FileName = exeToUse;
                psi.Arguments = string.Format(
                    "--app=\"{0}\" --window-size=1150,740 --user-data-dir=\"{1}\" " +
                    "--disable-features=WebAppIconInTitlebar --disable-background-mode --no-first-run --no-default-browser-check",
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
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(200);
                if (browserProcess != null && browserProcess.HasExited) break;

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

                if (styledCount > 0 && i > 15) break; // Window styled, exit polling loop
            }

            Log("StyleWindowLoop completed initial styling, waiting for browser to exit");
            if (browserProcess != null && !browserProcess.HasExited)
            {
                browserProcess.WaitForExit();
            }
            Log("Browser process exited, closing launcher");
            isRunning = false;
            }
            catch (Exception ex)
            {
                Log("StyleWindowLoop error: " + ex.ToString());
            }
        }

        static void ServerLoop()
        {
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

        static void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(path) || path == "ui.html")
                {
                    ServeFile(ctx, Path.Combine(appDir, "ui.html"), "text/html; charset=utf-8");
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
                    string pngFile = Path.Combine(appDir, "assets", "icon.png");
                    if (File.Exists(pngFile))
                    {
                        ServeBytes(ctx, File.ReadAllBytes(pngFile), "image/png");
                        return;
                    }
                    ServeBytes(ctx, TransparentIco, "image/x-icon");
                    return;
                }

                if (path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    ServeFile(ctx, Path.Combine(appDir, "manifest.json"), "application/manifest+json; charset=utf-8");
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
                    ServeFile(ctx, filePath, mime);
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

        static void ServeFile(HttpListenerContext ctx, string filePath, string contentType)
        {
            if (!File.Exists(filePath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Flush();
                ctx.Response.Close();
            }
            catch
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
        }

        static void ServeBytes(HttpListenerContext ctx, byte[] bytes, string contentType)
        {
            try
            {
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
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
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Flush();
            ctx.Response.Close();
        }

        static string ReadBody(HttpListenerContext ctx)
        {
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
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

                string installedAt = "";
                var state = LoadState();
                if (state != null && state.ContainsKey("installedAt"))
                {
                    installedAt = (string)state["installedAt"];
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
                    { "mcDir", mcDir ?? "" },
                    { "installedAt", installedAt },
                    { "javaMajor", jMaj },
                    { "javaNeed", jNeed },
                    { "javaOk", jMaj >= jNeed }
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

                SendJson(ctx, new Dictionary<string, object> { { "path", chosen ?? "" } });
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
                        if (Directory.Exists(c)) { sDir = c; break; }
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
                string mcDir = GetInstalledMcDir() ?? GetDetectedMcDir();
                var list = new List<Dictionary<string, object>>();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    string[] candidates = new string[] {
                        Path.Combine(mcDir, @"versions\Civilization\screenshots"),
                        Path.Combine(mcDir, "screenshots")
                    };
                    string sDir = null;
                    foreach (string c in candidates)
                    {
                        if (Directory.Exists(c)) { sDir = c; break; }
                    }
                    if (sDir != null && Directory.Exists(sDir))
                    {
                        var files = Directory.GetFiles(sDir, "*.png");
                        Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                        foreach (string f in files)
                        {
                            var fi = new FileInfo(f);
                            list.Add(new Dictionary<string, object> {
                                { "name", fi.Name },
                                { "date", fi.LastWriteTime.ToString("dd MMM yyyy, HH:mm") },
                                { "meta", string.Format("{0:0.#} MB", fi.Length / (1024.0 * 1024.0)) },
                                { "src", "/api/screenshot-file?path=" + Uri.EscapeDataString(fi.FullName) }
                            });
                        }
                    }
                }
                SendJson(ctx, list);
                return;
            }

            if (endpoint.StartsWith("screenshot-file"))
            {
                string rawPath = ctx.Request.QueryString["path"];
                if (!string.IsNullOrEmpty(rawPath) && File.Exists(rawPath))
                {
                    ServeFile(ctx, rawPath, "image/png");
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
                    { "pid", pid }
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

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        private static Process activeGameProcess = null;

        static bool IsGameRunning()
        {
            try
            {
                if (activeGameProcess != null && !activeGameProcess.HasExited)
                    return true;
            }
            catch { }

            try
            {
                var procs = Process.GetProcessesByName("javaw");
                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited && (p.MainWindowTitle.IndexOf("Civilization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             p.MainWindowTitle.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            activeGameProcess = p;
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
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
                    string tlExe = FindTLauncherExe(mcDir);
                    if (!string.IsNullOrEmpty(tlExe) && File.Exists(tlExe))
                    {
                        var psiTl = new ProcessStartInfo(tlExe);
                        psiTl.WorkingDirectory = Path.GetDirectoryName(tlExe);
                        psiTl.WindowStyle = ProcessWindowStyle.Minimized;
                        activeGameProcess = Process.Start(psiTl);
                        return true;
                    }
                    error = "Не найден Java 21 и TLauncher.exe для запуска игры";
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

                var psi = new ProcessStartInfo();
                psi.FileName = javaExe;
                psi.Arguments = args.ToString();
                psi.WorkingDirectory = civDir;
                psi.UseShellExecute = false;

                activeGameProcess = Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log("Launch error: " + ex.Message);
                try
                {
                    string tlExe = FindTLauncherExe(mcDir);
                    if (!string.IsNullOrEmpty(tlExe) && File.Exists(tlExe))
                    {
                        var psiTl = new ProcessStartInfo(tlExe);
                        psiTl.WorkingDirectory = Path.GetDirectoryName(tlExe);
                        psiTl.WindowStyle = ProcessWindowStyle.Minimized;
                        activeGameProcess = Process.Start(psiTl);
                        return true;
                    }
                }
                catch { }
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

        static void SaveState(string mcDir, List<string> files)
        {
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                var dict = new Dictionary<string, object> {
                    { "mcDir", mcDir },
                    { "installedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                    { "files", files }
                };
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

        static bool TestIsInstalled(string mcDir)
        {
            if (string.IsNullOrEmpty(mcDir)) return false;
            return File.Exists(Path.Combine(mcDir, @"versions\Civilization\Civilization.json"));
        }

        static string GetDetectedMcDir()
        {
            string tlProps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\tlauncher-2.0.properties");
            if (File.Exists(tlProps))
            {
                try
                {
                    string[] lines = File.ReadAllLines(tlProps, Encoding.UTF8);
                    foreach (string l in lines)
                    {
                        var m = Regex.Match(l, @"^minecraft\.gamedir=(.+)$");
                        if (m.Success)
                        {
                            string raw = m.Groups[1].Value.Replace(@"\:", ":").Replace(@"\\", @"\").Trim();
                            if (Directory.Exists(raw)) return raw;
                        }
                    }
                }
                catch { }
            }

            string[] candidates = new string[] {
                @"E:\Games\.minecraft",
                @"D:\Games\.minecraft",
                @"C:\Games\.minecraft",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
                @"D:\.minecraft",
                @"E:\.minecraft"
            };
            foreach (string c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        }

        static string GetInstalledMcDir()
        {
            var state = LoadState();
            if (state != null && state.ContainsKey("mcDir"))
            {
                string s = (string)state["mcDir"];
                if (TestIsInstalled(s)) return s;
            }
            string detected = GetDetectedMcDir();
            if (TestIsInstalled(detected)) return detected;
            return null;
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
            if (major >= need) return true;

            foreach (string jh in FindJavaHomes(mcDir))
            {
                int m = GetJavaMajor(Path.Combine(jh, @"bin\java.exe"));
                if (m >= need)
                {
                    SetProgress(92, "Копирование Java 21 в лаунчер...", true, false, null);
                    if (InstallJavaHome(jh, runtimeDir, comp, need)) return true;
                }
            }

            // Скачивание
            SetProgress(90, "Загрузка Java 21 (Eclipse Temurin)...", true, false, null);
            try
            {
                string downloaded = DownloadJava(need);
                SetProgress(97, "Установка Java 21...", true, false, null);
                return InstallJavaHome(downloaded, runtimeDir, comp, need);
            }
            catch (OperationCancelled) { throw; }
            catch (Exception ex)
            {
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
            string labelPrep = isUpdate ? "Подготовка к загрузке обновления..." : "Подготовка к загрузке сборки...";
            int initialPct = isUpdate ? 25 : 5;
            SetProgress(initialPct, labelPrep, true, false, null);
            CheckCancelled();

            // Пробуем git
            try
            {
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
                        return CacheDir;
                    }
                }
            }
            catch (OperationCancelled) { throw; }
            catch { }

            // Загрузка через zip
            CheckCancelled();
            string zipPath = Path.Combine(Path.GetTempPath(), "civilka_full.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(ExtractDir)) Directory.Delete(ExtractDir, true);

            DownloadFileCancellable(RepoZip, zipPath, (pct) =>
            {
                int p = isUpdate ? (25 + (int)(pct * 0.45)) : (10 + (int)(pct * 0.4));
                string txt = isUpdate
                    ? string.Format("Загрузка обновления ({0}%)...", pct)
                    : string.Format("Загрузка сборки ({0}%)...", pct);
                SetProgress(p, txt, true, false, null);
            });

            if (!File.Exists(zipPath)) throw new Exception("Не удалось скачать архив сборки с GitHub.");

            CheckCancelled();
            SetProgress(isUpdate ? 72 : 52, isUpdate ? "Распаковка обновления..." : "Распаковка сборки...", true, false, null);
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
                return unpacked;
            }
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
            try
            {
                if (!Directory.Exists(mcDir)) Directory.CreateDirectory(mcDir);

                string sourceDir = DownloadBuild(false);
                CheckCancelled();
                var files = InstallFiles(sourceDir, mcDir);

                CheckCancelled();
                SetProgress(82, "Регистрация профиля Civilization...", true, false, null);
                RegisterProfile(mcDir, Path.Combine(mcDir, @"versions\Civilization"));
                SaveState(mcDir, files);

                CheckCancelled();
                EnsureJava(mcDir, sourceDir);

                SetProgress(100, "Установка успешно завершена!", false, true, null);
            }
            catch (OperationCancelled)
            {
                cancelRequested = false;
                SetCancelledProgress("Установка остановлена");
            }
            catch (Exception ex)
            {
                cancelRequested = false;
                SetProgress(0, "Ошибка установки: " + ex.Message, false, false, ex.Message);
            }
        }

        static void DoUpdate(string mcDir)
        {
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

                var files = InstallFiles(sourceDir, mcDir);

                SetProgress(88, "Очистка устаревших файлов...", true, false, null);
                RemoveStale(mcDir, oldFiles, files);

                SetProgress(94, "Обновление профиля игры...", true, false, null);
                RegisterProfile(mcDir, Path.Combine(mcDir, @"versions\Civilization"));
                SaveState(mcDir, files);

                EnsureJava(mcDir, sourceDir);

                SetProgress(100, "Обновление успешно завершено!", false, true, null);
            }
            catch (OperationCancelled)
            {
                cancelRequested = false;
                SetCancelledProgress("Обновление остановлено");
            }
            catch (Exception ex)
            {
                cancelRequested = false;
                SetProgress(0, "Ошибка обновления: " + ex.Message, false, false, ex.Message);
            }
        }

        static void DoUninstall(string mcDir)
        {
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
                foreach (string rel in files)
                {
                    string p = Path.Combine(mcDir, rel);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); } catch { }
                    }
                }

                string civDst = Path.Combine(mcDir, @"versions\Civilization");
                if (Directory.Exists(civDst))
                {
                    try { Directory.Delete(civDst, true); } catch { }
                }

                string libRoot = Path.Combine(mcDir, "libraries");
                if (Directory.Exists(libRoot))
                {
                    CleanEmptyDirs(libRoot);
                }

                ClearState();
                SetProgress(100, "Сборка успешно удалена.", false, true, null);
            }
            catch (Exception ex)
            {
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
}
