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

    public partial class Program

    {

        private static int port = 0;

        private static HttpListener listener;

        private static ManualResetEvent serverReady = new ManualResetEvent(false);

        private static bool isRunning = true;

        private static Process browserProcess;

        public static IntPtr MainBrowserHwnd = IntPtr.Zero;

        private static string appDir;

        public static bool IsDevEnvironment
        {
            get
            {
                try
                {
                    string baseDir = appDir ?? AppDomain.CurrentDomain.BaseDirectory;
                    return File.Exists(Path.Combine(baseDir, "Launcher.cs"));
                }
                catch { return false; }
            }
        }

        public static string GetLauncherDataDir()
        {
            if (IsDevEnvironment) return appDir ?? AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CivilizationLauncher");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static string GetLauncherSkinsDir()
        {
            string baseDir = GetLauncherDataDir();
            string dir = Path.Combine(baseDir, "skins");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }



        private static string RepoZip = "https://github.com/civilkaserver-sketch/Civilka-version/archive/refs/heads/26.3.zip";

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



        // 0 = обновление не идёт, 1 = идёт (защита от параллельных запусков DoUpdate).
        private static int updateInProgress = 0;



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



        public static double GetTotalPhysicalRamGb()

        {

            try

            {

                var mem = new MEMORYSTATUSEX();

                if (GlobalMemoryStatusEx(mem))

                {

                    return mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);

                }

            }

            catch { }

            return 0.0;

        }



        public static int CalculateOptimalRamMb()

        {

            try

            {

                double totalGb = GetTotalPhysicalRamGb();

                if (totalGb > 0.1)

                {

                    if (totalGb <= 4.5) return 2560;       // <= 4 GB RAM -> 2.5 GB

                    if (totalGb <= 6.5) return 3584;       // 6 GB RAM -> 3.5 GB

                    if (totalGb <= 8.5) return 4096;       // 8 GB RAM -> 4 GB

                    if (totalGb <= 12.5) return 5120;      // 12 GB RAM -> 5 GB

                    if (totalGb <= 16.5) return 6144;      // 16 GB RAM -> 6 GB

                    if (totalGb <= 24.5) return 7168;      // 24 GB RAM -> 7 GB

                    return 6144;                           // 32+ GB RAM -> 6 GB (резервируем native для Sodium/Voxy/LWJGL)

                }

            }

            catch { }

            return 4096; // Безопасный дефолт

        }

        public static string SanitizeMinecraftUsername(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Player_" + new Random().Next(100, 999);

            var translit = new Dictionary<char, string> {
                {'а',"a"}, {'б',"b"}, {'в',"v"}, {'г',"g"}, {'д',"d"}, {'е',"e"}, {'ё',"e"},
                {'ж',"zh"}, {'з',"z"}, {'и',"i"}, {'й',"y"}, {'к',"k"}, {'л',"l"}, {'м',"m"},
                {'н',"n"}, {'о',"o"}, {'п',"p"}, {'р',"r"}, {'с',"s"}, {'т',"t"}, {'у',"u"},
                {'ф',"f"}, {'х',"kh"}, {'ц',"ts"}, {'ч',"ch"}, {'ш',"sh"}, {'щ',"shch"},
                {'ъ',""}, {'ы',"y"}, {'ь',""}, {'э',"e"}, {'ю',"yu"}, {'я',"ya"},
                {'А',"A"}, {'Б',"B"}, {'В',"V"}, {'Г',"G"}, {'Д',"D"}, {'Е',"E"}, {'Ё',"E"},
                {'Ж',"Zh"}, {'З',"Z"}, {'И',"I"}, {'Й',"Y"}, {'К',"K"}, {'Л',"L"}, {'М',"M"},
                {'Н',"N"}, {'О',"O"}, {'П',"P"}, {'Р',"R"}, {'С',"S"}, {'Т',"T"}, {'У',"U"},
                {'Ф',"F"}, {'Х',"Kh"}, {'Ц',"Ts"}, {'Ч',"Ch"}, {'Ш',"Sh"}, {'Щ',"Shch"},
                {'Ъ',""}, {'Ы',"Y"}, {'Ь',""}, {'Э',"E"}, {'Ю',"Yu"}, {'Я',"Ya"}
            };

            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (translit.ContainsKey(c)) sb.Append(translit[c]);
                else if (char.IsWhiteSpace(c) || c == '-' || c == '.') sb.Append('_');
                else sb.Append(c);
            }

            string clean = Regex.Replace(sb.ToString(), @"[^a-zA-Z0-9_]", "");
            clean = clean.Trim('_');

            if (clean.Length > 16) clean = clean.Substring(0, 16).TrimEnd('_');

            if (clean.Length < 3)
            {
                clean = (clean + "_Player").PadRight(3, '1');
                if (clean.Length > 16) clean = clean.Substring(0, 16);
            }

            return clean;
        }

        public static string GenerateOfflineUuid(string username)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
                    hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // version 3
                    hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variant IETF
                    var sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }



        public static bool IsPerformanceModeEnabled()

        {

            try

            {

                var state = LoadState();

                if (state != null && state.ContainsKey("performanceMode"))

                {

                    object val = state["performanceMode"];

                    if (val is bool) return (bool)val;

                    if (val != null)

                    {

                        bool b;

                        if (bool.TryParse(val.ToString(), out b)) return b;

                    }

                }

            }

            catch { }

            return false;

        }



        public static void SetPerformanceModeEnabled(bool enabled)

        {

            try

            {

                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);

                var state = LoadState() ?? new Dictionary<string, object>();

                state["performanceMode"] = enabled;

                string json = new JavaScriptSerializer().Serialize(state);

                File.WriteAllText(StateFile, json, Encoding.UTF8);

            }

            catch { }

        }



        public static void ApplyPerformanceModeConfig(string mcDir, bool enabled)

        {

            if (string.IsNullOrEmpty(mcDir)) return;

            try

            {

                string civDir = Path.Combine(mcDir, @"versions\Civilization");



                // 1. Точечно меняем sodium-options.json

                string sodiumFile = Path.Combine(civDir, @"config\sodium-options.json");

                if (!File.Exists(sodiumFile))

                {

                    sodiumFile = Path.Combine(mcDir, @"config\sodium-options.json");

                }



                if (File.Exists(sodiumFile))

                {

                    try

                    {

                        string text = File.ReadAllText(sodiumFile, new UTF8Encoding(false));



                        string targetWeather = enabled ? "\"weather_quality\": \"FAST\"" : "\"weather_quality\": \"DEFAULT\"";

                        string targetLeaves = enabled ? "\"leaves_quality\": \"FAST\"" : "\"leaves_quality\": \"DEFAULT\"";

                        string targetVignette = enabled ? "\"enable_vignette\": false" : "\"enable_vignette\": true";



                        text = Regex.Replace(text, @"""weather_quality""\s*:\s*""[^""]+""", targetWeather);

                        text = Regex.Replace(text, @"""leaves_quality""\s*:\s*""[^""]+""", targetLeaves);

                        text = Regex.Replace(text, @"""enable_vignette""\s*:\s*(true|false)", targetVignette);



                        if (enabled)

                        {

                            text = Regex.Replace(text, @"""animate_only_visible_textures""\s*:\s*(true|false)", "\"animate_only_visible_textures\": true");

                            text = Regex.Replace(text, @"""use_entity_culling""\s*:\s*(true|false)", "\"use_entity_culling\": true");

                            text = Regex.Replace(text, @"""use_fog_occlusion""\s*:\s*(true|false)", "\"use_fog_occlusion\": true");

                            text = Regex.Replace(text, @"""use_block_face_culling""\s*:\s*(true|false)", "\"use_block_face_culling\": true");

                            text = Regex.Replace(text, @"""use_no_error_g_l_context""\s*:\s*(true|false)", "\"use_no_error_g_l_context\": true");

                        }



                        File.WriteAllText(sodiumFile, text, new UTF8Encoding(false));

                        Log(string.Format("[PerformanceMode] Точечно обновлен sodium-options.json: enabled={0}", enabled));

                    }

                    catch (Exception ex)

                    {

                        Log(string.Format("[PerformanceMode] Ошибка обновления sodium-options.json: {0}", ex.Message));

                    }

                }



                // 2. Точечно меняем Графика: Быстро (0) и Облака: Отключить ("false") в options.txt

                string[] optPaths = new string[] {

                    Path.Combine(civDir, "options.txt"),

                    Path.Combine(mcDir, "options.txt")

                };



                foreach (var optFile in optPaths)

                {

                    if (File.Exists(optFile))

                    {

                        try

                        {

                            // Читаем СТРОГО в UTF-8 без BOM

                            string optText = File.ReadAllText(optFile, new UTF8Encoding(false));



                            // graphicsMode: 0 = Быстро (Fast), 1 = Детально (Fancy)

                            string targetGraphics = enabled ? "graphicsMode:0" : "graphicsMode:1";

                            // renderClouds: "false" = Отключить (Off), "true" = Включить (Fancy)

                            string targetClouds = enabled ? "renderClouds:\"false\"" : "renderClouds:\"true\"";



                            if (Regex.IsMatch(optText, @"graphicsMode:\d+"))

                            {

                                optText = Regex.Replace(optText, @"graphicsMode:\d+", targetGraphics);

                            }

                            else

                            {

                                optText += "\r\n" + targetGraphics;

                            }



                            if (Regex.IsMatch(optText, @"renderClouds:""[^""]+"""))

                            {

                                optText = Regex.Replace(optText, @"renderClouds:""[^""]+""", targetClouds);

                            }

                            else

                            {

                                optText += "\r\n" + targetClouds;

                            }



                            // Сохраняем СТРОГО в UTF-8 БЕЗ BOM, чтобы игра не сбрасывала настройки

                            File.WriteAllText(optFile, optText, new UTF8Encoding(false));

                            Log(string.Format("[PerformanceMode] Точечно обновлен {0}: graphicsMode={1}, renderClouds={2}", optFile, enabled ? "0 (Fast)" : "1 (Fancy)", enabled ? "false (Off)" : "true (On)"));

                        }

                        catch (Exception ex)

                        {

                            Log(string.Format("[PerformanceMode] Ошибка обновления options.txt: {0}", ex.Message));

                        }

                    }

                }



                // 3. Точечно меняем частицы "Блок сломан" (block_break) и "Стук по блоку" (block_breaking) в sodium-extra-options.json

                string[] extraPaths = new string[] {

                    Path.Combine(civDir, @"config\sodium-extra-options.json"),

                    Path.Combine(mcDir, @"config\sodium-extra-options.json")

                };



                foreach (var extraFile in extraPaths)

                {

                    if (File.Exists(extraFile))

                    {

                        try

                        {

                            string extraText = File.ReadAllText(extraFile, new UTF8Encoding(false));



                            string targetBreak = enabled ? "\"block_break\": false" : "\"block_break\": true";

                            string targetBreaking = enabled ? "\"block_breaking\": false" : "\"block_breaking\": true";



                            if (Regex.IsMatch(extraText, @"""block_break""\s*:\s*(true|false)"))

                            {

                                extraText = Regex.Replace(extraText, @"""block_break""\s*:\s*(true|false)", targetBreak);

                            }

                            if (Regex.IsMatch(extraText, @"""block_breaking""\s*:\s*(true|false)"))

                            {

                                extraText = Regex.Replace(extraText, @"""block_breaking""\s*:\s*(true|false)", targetBreaking);

                            }



                            File.WriteAllText(extraFile, extraText, new UTF8Encoding(false));

                            Log(string.Format("[PerformanceMode] Точечно обновлен {0}: block_break={1}, block_breaking={2}", extraFile, !enabled, !enabled));

                        }

                        catch (Exception ex)

                        {

                            Log(string.Format("[PerformanceMode] Ошибка обновления sodium-extra-options.json: {0}", ex.Message));

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                Log(string.Format("[PerformanceMode] Ошибка точечного обновления: {0}", ex.Message));

            }

        }



        public static string GetLogPath()
        {
            if (activeLogPath != null) return activeLogPath;

            try
            {
                if (IsDevEnvironment)
                {
                    string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                    using (var fs = new FileStream(local, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    {
                    }
                    activeLogPath = local;
                    return activeLogPath;
                }
            }
            catch { }

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CivilizationLauncher");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string local = Path.Combine(dir, "launcher.log");
                using (var fs = new FileStream(local, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                }
                activeLogPath = local;
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



        public class NetworkDiagResult
        {
            public bool localRadminFound;
            public string localRadminIp;
            public string targetHost;
            public int targetPort;
            public bool isHostSelf;
            public bool pingOk;
            public long pingTimeMs;
            public bool tcp25565Ok;
            public long tcp25565TimeMs;
            public string tcp25565Error;
            public bool tcp25566Ok;
            public long tcp25566TimeMs;
            public string tcp25566Error;
            public int playersOnline = -1;
            public int playersMax = 0;
            public List<string> logLines = new List<string>();
        }

        public class ServerStatusInfo
        {
            public bool online;
            public long pingMs;
            public int playersOnline = -1;
            public int playersMax = 0;
            public string host = "26.194.58.43";
            public int port = 25565;
        }

        private static ServerStatusInfo lastCachedServerStatus = null;
        private static DateTime lastServerStatusCheck = DateTime.MinValue;
        private static readonly object serverStatusLock = new object();

        private static void WriteVarInt(Stream stream, int value)
        {
            while ((value & -128) != 0)
            {
                stream.WriteByte((byte)((value & 0x7F) | 0x80));
                value = (int)((uint)value >> 7);
            }
            stream.WriteByte((byte)value);
        }

        private static int ReadVarInt(Stream stream)
        {
            int numRead = 0;
            int result = 0;
            byte read;
            do
            {
                int val = stream.ReadByte();
                if (val == -1) throw new EndOfStreamException();
                read = (byte)val;
                int value = (read & 0x7F);
                result |= (value << (7 * numRead));
                numRead++;
                if (numRead > 5) throw new InvalidOperationException("VarInt is too big");
            } while ((read & 0x80) != 0);
            return result;
        }

        public static int QueryMinecraftOnlinePlayers(string host, int port, out int maxPlayers)
        {
            maxPlayers = 0;
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2000) || !client.Connected) return -1;
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 2500;
                        stream.WriteTimeout = 2500;
                        using (var ms = new MemoryStream())
                        {
                            WriteVarInt(ms, 0x00);
                            WriteVarInt(ms, 772);
                            byte[] hostBytes = Encoding.UTF8.GetBytes(host);
                            WriteVarInt(ms, hostBytes.Length);
                            ms.Write(hostBytes, 0, hostBytes.Length);
                            ms.WriteByte((byte)(port >> 8));
                            ms.WriteByte((byte)(port & 0xFF));
                            WriteVarInt(ms, 1);

                            byte[] handshakeBytes = ms.ToArray();
                            WriteVarInt(stream, handshakeBytes.Length);
                            stream.Write(handshakeBytes, 0, handshakeBytes.Length);
                        }

                        WriteVarInt(stream, 1);
                        stream.WriteByte(0x00);

                        int totalLen = ReadVarInt(stream);
                        int packetId = ReadVarInt(stream);
                        int strLen = ReadVarInt(stream);
                        byte[] strBytes = new byte[strLen];
                        int totalRead = 0;
                        while (totalRead < strLen)
                        {
                            int r = stream.Read(strBytes, totalRead, strLen - totalRead);
                            if (r <= 0) break;
                            totalRead += r;
                        }
                        string json = Encoding.UTF8.GetString(strBytes, 0, totalRead);
                        var mOnline = Regex.Match(json, @"""online""\s*:\s*(\d+)");
                        var mMax = Regex.Match(json, @"""max""\s*:\s*(\d+)");
                        if (mMax.Success) int.TryParse(mMax.Groups[1].Value, out maxPlayers);
                        if (mOnline.Success)
                        {
                            int online;
                            if (int.TryParse(mOnline.Groups[1].Value, out online)) return online;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        public static ServerStatusInfo GetCachedServerStatus(bool force = false)
        {
            lock (serverStatusLock)
            {
                if (!force && lastCachedServerStatus != null && (DateTime.UtcNow - lastServerStatusCheck).TotalSeconds < 7)
                {
                    return lastCachedServerStatus;
                }

                var status = new ServerStatusInfo();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var tcp = new System.Net.Sockets.TcpClient())
                    {
                        var ar = tcp.BeginConnect(status.host, status.port, null, null);
                        bool ok = ar.AsyncWaitHandle.WaitOne(2500);
                        sw.Stop();
                        if (ok && tcp.Connected)
                        {
                            status.online = true;
                            status.pingMs = sw.ElapsedMilliseconds;

                            int maxP = 0;
                            int onP = QueryMinecraftOnlinePlayers(status.host, status.port, out maxP);
                            if (onP >= 0)
                            {
                                status.playersOnline = onP;
                                status.playersMax = maxP;
                            }
                        }
                        else
                        {
                            status.online = false;
                        }
                    }
                }
                catch
                {
                    status.online = false;
                }

                lastCachedServerStatus = status;
                lastServerStatusCheck = DateTime.UtcNow;
                return status;
            }
        }

        static NetworkDiagResult RunNetworkDiagnostics(bool writeToLog)
        {
            var res = new NetworkDiagResult();
            res.targetHost = "26.194.58.43";
            res.targetPort = 25565;

            Action<string> emit = (msg) =>
            {
                res.logLines.Add(msg);
                if (writeToLog) Log(msg);
            };

            emit("================================================================================");
            emit("[СЕТЕВАЯ ДИАГНОСТИКА СЕРВЕРА CIVILIZATION]");
            emit(string.Format("Целевой сервер: {0}:{1}", res.targetHost, res.targetPort));

            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    string name = ni.Name ?? "";
                    string desc = ni.Description ?? "";
                    if (name.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var ipProps = ni.GetIPProperties();
                        foreach (var u in ipProps.UnicastAddresses)
                        {
                            if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                res.localRadminFound = true;
                                res.localRadminIp = u.Address.ToString();
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                emit("[Сеть] Ошибка сканирования сетевых карт: " + ex.Message);
            }

            if (res.localRadminFound)
            {
                emit(string.Format("[Сеть] Локальный IP в Radmin VPN: {0}", res.localRadminIp));
                if (res.localRadminIp == res.targetHost)
                {
                    res.isHostSelf = true;
                    emit("[Сеть] Этот компьютер является ХОСТОМ сервера (IP совпадает с целевым)");
                }
            }
            else
            {
                emit("[Сеть/ВНИМАНИЕ] Сетевой адаптер Radmin VPN не найден или отключён!");
            }

            // Ping
            try
            {
                using (var p = new System.Net.NetworkInformation.Ping())
                {
                    var reply = p.Send(res.targetHost, 2500);
                    if (reply != null && reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        res.pingOk = true;
                        res.pingTimeMs = reply.RoundtripTime;
                        emit(string.Format("[Сеть] ICMP Ping до {0}: УСПЕШНО ({1} ms)", res.targetHost, res.pingTimeMs));
                    }
                    else
                    {
                        res.pingOk = false;
                        string status = reply != null ? reply.Status.ToString() : "Таймаут";
                        emit(string.Format("[Сеть] ICMP Ping до {0}: НЕТ ОТВЕТА ({1})", res.targetHost, status));
                    }
                }
            }
            catch (Exception ex)
            {
                emit(string.Format("[Сеть] Ошибка Ping до {0}: {1}", res.targetHost, ex.Message));
            }

            // TCP 25565
            var sw25565 = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var ar = tcp.BeginConnect(res.targetHost, 25565, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(3500);
                    sw25565.Stop();
                    if (ok && tcp.Connected)
                    {
                        res.tcp25565Ok = true;
                        res.tcp25565TimeMs = sw25565.ElapsedMilliseconds;
                        emit(string.Format("[Сеть] Порт 25565 (Minecraft Сервер): ДОСТУПЕН! Ответ за {0} ms", res.tcp25565TimeMs));
                        try
                        {
                            int maxP = 0;
                            int onP = QueryMinecraftOnlinePlayers(res.targetHost, 25565, out maxP);
                            if (onP >= 0)
                            {
                                res.playersOnline = onP;
                                res.playersMax = maxP;
                                emit(string.Format("[Сеть] Онлайн игроков на сервере: {0} / {1}", onP, maxP));
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        res.tcp25565Ok = false;
                        res.tcp25565Error = "Превышено время ожидания ответа (Connection timed out, 3.5s)";
                        emit("[Сеть/ОШИБКА] Порт 25565 (Minecraft Сервер): " + res.tcp25565Error);
                    }
                }
            }
            catch (Exception ex)
            {
                sw25565.Stop();
                res.tcp25565Ok = false;
                res.tcp25565Error = ex.Message;
                emit("[Сеть/ОШИБКА] Порт 25565 (Minecraft Сервер): " + ex.Message);
            }

            // TCP 25566 (Mod Update Server)
            var sw25566 = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var ar = tcp.BeginConnect(res.targetHost, 25566, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(3500);
                    sw25566.Stop();
                    if (ok && tcp.Connected)
                    {
                        res.tcp25566Ok = true;
                        res.tcp25566TimeMs = sw25566.ElapsedMilliseconds;
                        emit(string.Format("[Сеть] Порт 25566 (Сервер обновлений мода): ДОСТУПЕН! Ответ за {0} ms", res.tcp25566TimeMs));
                    }
                    else
                    {
                        res.tcp25566Ok = false;
                        res.tcp25566Error = "Превышено время ожидания (3.5s)";
                        emit("[Сеть/ОШИБКА] Порт 25566 (Сервер обновлений): " + res.tcp25566Error);
                    }
                }
            }
            catch (Exception ex)
            {
                sw25566.Stop();
                res.tcp25566Ok = false;
                res.tcp25566Error = ex.Message;
                emit("[Сеть/ОШИБКА] Порт 25566 (Сервер обновлений): " + ex.Message);
            }

            if (!res.tcp25565Ok)
            {
                emit("--------------------------------------------------------------------------------");
                emit("[ПРИЧИНА И РЕШЕНИЕ]:");
                if (!res.localRadminFound)
                {
                    emit("1. Radmin VPN не запущен на этом ПК. Запустите Radmin VPN.");
                }
                if (!res.pingOk)
                {
                    emit("2. Нет связи по Radmin VPN с 26.194.58.43. Убедитесь, что вы в одной комнате с хостом,");
                    emit("   и в Radmin VPN горит зелёный кружок напротив ников игроков.");
                }
                emit("3. Если вы подключаетесь к другу-хосту: хосту необходимо запустить на своём Рабочем столе");
                emit("   файл 'ОТКРЫТЬ_ПОРТЫ_РАДМИН.bat' от имени Администратора (чтобы открыть порт 25565 в Firewall).");
                emit("--------------------------------------------------------------------------------");
            }
            else
            {
                emit("[Сеть] ВСЁ В ПОРЯДКЕ: Соединение с сервером установлено, можно заходить в игру!");
            }
            emit("================================================================================");

            return res;
        }

        static void LogServerReachability()
        {
            ThreadPool.QueueUserWorkItem(_ => RunNetworkDiagnostics(true));
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
                            if (latestCrash == null || f.LastWriteTime > latestCrash.LastWriteTime)
                                latestCrash = f;
                        }
                    }
                }

                if (latestCrash != null)
                {
                    Log("Found recent Minecraft crash report: " + latestCrash.FullName);
                    try
                    {
                        string[] lines = File.ReadAllLines(latestCrash.FullName, Encoding.UTF8);
                        int count = Math.Min(80, lines.Length);
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
                        if (fi.Length > 0)
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
                                        if (q.Count > 80) q.Dequeue();
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



        static void RunSilentBackgroundUpdate()
        {
            try
            {
                // 1. Silent launcher update check:
                if (!IsDevWorkspace())
                {
                    string curLauncherSha = GetCurrentLauncherSha();
                    string remoteLauncherSha = FetchLauncherRemoteShaSynchronous();
                    if (!string.IsNullOrEmpty(remoteLauncherSha) && !remoteLauncherSha.StartsWith(curLauncherSha, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[SilentUpdater] Newer launcher version detected: " + remoteLauncherSha);
                        string tempExe = Path.Combine(Path.GetTempPath(), "CV_Launcher_new.exe");
                        if (DownloadFileFromRepo("CV Launcher.exe", tempExe, null))
                        {
                            ExecuteLauncherUpdate(tempExe, null, remoteLauncherSha);
                            return;
                        }
                    }
                }

                // 2. Silent modpack update check:
                string mcDir = GetActiveMcDir();
                string activeVer = GetSelectedBuildVersion();
                bool legNeed = false;
                if (!string.IsNullOrEmpty(mcDir) && TestIsVersionInstalled(mcDir, activeVer, out legNeed) && !legNeed)
                {
                    string remoteSha = FetchRemoteShaSynchronous(activeVer);
                    string localSha = "";
                    var state = LoadState(activeVer);
                    if (state != null && state.ContainsKey("commitSha")) localSha = (string)state["commitSha"];
                    if (!string.IsNullOrEmpty(remoteSha) && !string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[SilentUpdater] Newer modpack detected (" + activeVer + ", remote=" + remoteSha + ", local=" + localSha + "). Starting quiet update...");
                        DoUpdate(mcDir, false, activeVer);
                        Log("[SilentUpdater] Modpack updated cleanly to SHA: " + remoteSha);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[SilentUpdater] Error: " + ex.Message);
            }
        }

        [STAThread]

        static void Main(string[] args)

        {

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("Unhandled: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "unknown"));

            bool isSilentMode = false;
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase))
                    {
                        isSilentMode = true;
                        break;
                    }
                }
            }

            if (isSilentMode)
            {
                appDir = AppDomain.CurrentDomain.BaseDirectory;
                Log("[SilentMode] Running in quiet background mode without UI...");
                RunSilentBackgroundUpdate();
                Log("[SilentMode] Silent background update finished. Exiting process.");
                return;
            }

            appDir = AppDomain.CurrentDomain.BaseDirectory;

            LogSystemDiagnostics();

            ThreadPool.QueueUserWorkItem((s) => FetchRemoteShaInBackground(GetSelectedBuildVersion()));

            ThreadPool.QueueUserWorkItem((s) => FetchLauncherRemoteShaInBackground());

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



            try { SetCurrentProcessExplicitAppUserModelID("Civilization.Launcher.CV"); } catch { }



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

            try
            {
                Thread friendsThread = new Thread(FriendsSyncLoop);
                friendsThread.IsBackground = true;
                friendsThread.Start();
            }
            catch { }



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

                    "--app=\"{0}\" --window-size=1150,740 --user-data-dir=\"{1}\" " +

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

        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);



        [DllImport("user32.dll", SetLastError = true)]

        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);



        [DllImport("user32.dll")]

        public static extern bool IsWindowVisible(IntPtr hWnd);



        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]

        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);



        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]

        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);



        [DllImport("user32.dll")]

        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);



        [DllImport("user32.dll")]

        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);



        [DllImport("user32.dll")]

        public static extern bool IsWindow(IntPtr hWnd);



        [DllImport("user32.dll")]

        public static extern bool SetForegroundWindow(IntPtr hWnd);



        [DllImport("user32.dll")]

        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);



        [DllImport("user32.dll")]

        public static extern bool AllowSetForegroundWindow(int dwProcessId);



        [DllImport("user32.dll")]

        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);



        [DllImport("user32.dll")]

        public static extern bool BringWindowToTop(IntPtr hWnd);



        [DllImport("user32.dll")]

        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);



        [DllImport("user32.dll")]

        public static extern IntPtr GetForegroundWindow();



        [DllImport("kernel32.dll")]

        public static extern uint GetCurrentThreadId();



        [DllImport("user32.dll")]

        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);



        [DllImport("user32.dll")]

        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public const uint GW_OWNER = 4;

        public const int ASFW_ANY = -1;



        [DllImport("shell32.dll", ExactSpelling = true)]

        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);



        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]

        private static extern IntPtr ILCreateFromPathW(string pszPath);



        [DllImport("shell32.dll")]

        private static extern void ILFree(IntPtr pidl);



        [DllImport("user32.dll")]

        public static extern bool IsIconic(IntPtr hWnd);



        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);



        public static void ForceForeground(IntPtr targetHwnd)

        {

            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd)) return;

            try

            {

                if (IsIconic(targetHwnd))

                {

                    ShowWindow(targetHwnd, 9); // SW_RESTORE only when iconic/minimized

                }

                else

                {

                    ShowWindow(targetHwnd, 5); // SW_SHOW without un-maximizing

                }



                // 1. Устанавливаем окно на самый верх (TOPMOST), чтобы оно физически всплыло над развёрнутым браузером

                SetWindowPos(targetHwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW

                BringWindowToTop(targetHwnd);

                SetForegroundWindow(targetHwnd);

                SwitchToThisWindow(targetHwnd, true);



                // 2. Оставляем TOPMOST на 1 секунду, чтобы окно гарантированно отрисовалось перед глазами пользователя,

                // после чего аккуратно возвращаем в обычный Z-порядок (HWND_NOTOPMOST) без потери фокуса

                ThreadPool.QueueUserWorkItem(delegate

                {

                    Thread.Sleep(1000);

                    try

                    {

                        if (IsWindow(targetHwnd))

                        {

                            SetWindowPos(targetHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // SWP_NOACTIVATE

                        }

                    }

                    catch { }

                });

            }

            catch (Exception ex)

            {

                Log("ForceForeground error: " + ex.Message);

            }

        }



        public static void OpenPathInExplorer(string fullPath, bool selectFile)

        {

            if (string.IsNullOrEmpty(fullPath)) return;



            ThreadPool.QueueUserWorkItem(delegate(object state)

            {

                try

                {

                    var existingHwnds = new HashSet<IntPtr>();

                    IntPtr lastActiveExplorer = IntPtr.Zero;

                    EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)

                    {

                        if (IsWindow(hWnd) && IsWindowVisible(hWnd))

                        {

                            StringBuilder sb = new StringBuilder(64);

                            GetClassName(hWnd, sb, 64);

                            string cls = sb.ToString();

                            if (cls == "CabinetWClass" || cls == "ExploreWClass")

                            {

                                existingHwnds.Add(hWnd);

                                if (lastActiveExplorer == IntPtr.Zero) lastActiveExplorer = hWnd;

                            }

                        }

                        return true;

                    }, IntPtr.Zero);



                    AllowSetForegroundWindow(ASFW_ANY);



                    if (selectFile && File.Exists(fullPath))

                    {

                        var psi = new ProcessStartInfo("explorer.exe", string.Format("/select,\"{0}\"", fullPath));

                        psi.UseShellExecute = true;

                        Process.Start(psi);

                    }

                    else

                    {

                        string target = Directory.Exists(fullPath) ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);

                        if (!string.IsNullOrEmpty(target) && !Directory.Exists(target))

                        {

                            try { Directory.CreateDirectory(target); } catch { }

                        }

                        var psi = new ProcessStartInfo("explorer.exe", string.Format("\"{0}\"", target));

                        psi.UseShellExecute = true;

                        Process.Start(psi);

                    }



                    IntPtr foundHwnd = IntPtr.Zero;

                    for (int i = 0; i < 60; i++)

                    {

                        Thread.Sleep(70);

                        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)

                        {

                            if (foundHwnd != IntPtr.Zero) return false;

                            if (IsWindow(hWnd) && IsWindowVisible(hWnd) && !existingHwnds.Contains(hWnd))

                            {

                                StringBuilder sb = new StringBuilder(64);

                                GetClassName(hWnd, sb, 64);

                                string cls = sb.ToString();

                                if (cls == "CabinetWClass" || cls == "ExploreWClass")

                                {

                                    foundHwnd = hWnd;

                                    return false;

                                }

                            }

                            return true;

                        }, IntPtr.Zero);



                        if (foundHwnd != IntPtr.Zero)

                        {

                            Log("OpenPathInExplorer: Found new Explorer window " + foundHwnd);

                            ForceForeground(foundHwnd);

                            break;

                        }

                    }



                    if (foundHwnd == IntPtr.Zero)

                    {

                        string targetName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));

                        if (!string.IsNullOrEmpty(targetName))

                        {

                            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)

                            {

                                if (foundHwnd != IntPtr.Zero) return false;

                                if (IsWindow(hWnd) && IsWindowVisible(hWnd))

                                {

                                    StringBuilder sb = new StringBuilder(64);

                                    GetClassName(hWnd, sb, 64);

                                    string cls = sb.ToString();

                                    if (cls == "CabinetWClass" || cls == "ExploreWClass")

                                    {

                                        StringBuilder titleSb = new StringBuilder(256);

                                        GetWindowText(hWnd, titleSb, 256);

                                        if (titleSb.ToString().IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)

                                        {

                                            foundHwnd = hWnd;

                                            return false;

                                        }

                                    }

                                }

                                return true;

                            }, IntPtr.Zero);

                        }



                        if (foundHwnd == IntPtr.Zero && lastActiveExplorer != IntPtr.Zero && IsWindow(lastActiveExplorer))

                        {

                            foundHwnd = lastActiveExplorer;

                        }



                        if (foundHwnd != IntPtr.Zero)

                        {

                            Log("OpenPathInExplorer: Activating existing Explorer window " + foundHwnd);

                            ForceForeground(foundHwnd);

                        }

                    }

                }

                catch (Exception ex)

                {

                    Log("OpenPathInExplorer error: " + ex.Message);

                }

            });

        }



        public static void OpenFileInNotepad(string filePath)

        {

            if (string.IsNullOrEmpty(filePath)) return;



            ThreadPool.QueueUserWorkItem(delegate(object state)

            {

                try

                {

                    AllowSetForegroundWindow(ASFW_ANY);

                    Process p = Process.Start("notepad.exe", string.Format("\"{0}\"", filePath));

                    if (p != null)

                    {

                        for (int i = 0; i < 30; i++)

                        {

                            Thread.Sleep(70);

                            p.Refresh();

                            if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))

                            {

                                ForceForeground(p.MainWindowHandle);

                                break;

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    Log("OpenFileInNotepad error: " + ex.Message);

                }

            });

        }



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

                                        MainBrowserHwnd = hWnd;

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

                string shortcutPath = Path.Combine(desktop, "CV Launcher.lnk");

                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;



                Type shellType = Type.GetTypeFromProgID("WScript.Shell");

                if (shellType != null)

                {

                    dynamic shell = Activator.CreateInstance(shellType);

                    dynamic shortcut = shell.CreateShortcut(shortcutPath);

                    shortcut.TargetPath = exePath;

                    shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);

                    shortcut.Description = "Civilization Reborn Launcher";

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

            // Не распаковывать и не засорять файлы (Desktop/Downloads), если запущен собранный exe вне исходного репозитория разработки
            if (!IsDevEnvironment) return;

            try

            {

                string uiPath = Path.Combine(appDir, "ui.html");

                if (!File.Exists(uiPath))

                {

                    byte[] ui = GetEmbeddedResource("ui.html");

                    if (ui != null && ui.Length > 0)

                    {

                        File.WriteAllBytes(uiPath, ui);

                        Log("Extracted embedded ui.html to disk.");

                    }

                }



                string assetsDir = Path.Combine(appDir, "assets");

                if (!Directory.Exists(assetsDir))

                {

                    try { Directory.CreateDirectory(assetsDir); } catch { }

                }



                string[] essentialAssets = new string[] {

                    "assets/icon.png",

                    "assets/minecraft-rus.ttf",

                    "assets/news_banner_1.svg",

                    "assets/news_banner_1.png",

                    "assets/news_banner_patch.svg",

                    "assets/news_banner_patch.png",

                    "assets/news_banner_patch_green.svg"

                };



                foreach (string asset in essentialAssets)

                {

                    string localPath = Path.Combine(appDir, asset.Replace('/', '\\'));

                    if (!File.Exists(localPath))

                    {

                        byte[] data = GetEmbeddedResource(asset);

                        if (data != null && data.Length > 0)

                        {

                            string dir = Path.GetDirectoryName(localPath);

                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                            File.WriteAllBytes(localPath, data);

                            Log("Extracted embedded asset: " + asset);

                        }

                    }

                }



                string manifestPath = Path.Combine(appDir, "manifest.json");

                if (!File.Exists(manifestPath))

                {

                    byte[] mf = GetEmbeddedResource("manifest.json");

                    if (mf != null && mf.Length > 0) File.WriteAllBytes(manifestPath, mf);

                }



                string icoPath = Path.Combine(appDir, "icon.ico");

                if (!File.Exists(icoPath))

                {

                    byte[] ico = GetEmbeddedResource("icon.ico");

                    if (ico != null && ico.Length > 0) File.WriteAllBytes(icoPath, ico);

                }



                string newsPath = Path.Combine(appDir, "news.json");

                if (!File.Exists(newsPath))

                {

                    byte[] nj = GetEmbeddedResource("news.json");

                    if (nj != null && nj.Length > 0) File.WriteAllBytes(newsPath, nj);

                }



                try

                {

                    if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);

                    var state = LoadState() ?? new Dictionary<string, object>();

                    if (!state.ContainsKey("desktopShortcutCreated"))

                    {

                        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CV Launcher.lnk");

                        if (!File.Exists(desktopLnk))

                        {

                            CreateDesktopShortcut();

                        }

                        state["desktopShortcutCreated"] = true;

                        string json = new JavaScriptSerializer().Serialize(state);

                        File.WriteAllText(StateFile, json, Encoding.UTF8);

                    }

                }

                catch { }



                try

                {

                    string activeMc = GetActiveMcDir();

                    if (!string.IsNullOrEmpty(activeMc))

                    {

                        SkinManager.SyncSkinToGame(appDir, activeMc);

                    }

                }

                catch { }

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

                string path = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).TrimStart('/');

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

                    ServeFile(ctx, Path.Combine(appDir, "news.json"), "application/json; charset=utf-8", "news.json");

                    return;

                }



                if (path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))

                {

                    string filePath = Path.Combine(appDir, path.Replace('/', '\\'));

                    string mime = "application/octet-stream";

                    if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) mime = "image/png";

                    else if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) mime = "image/webp";

                    else if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) mime = "image/jpeg";

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



        private static readonly object assetDownloadLock = new object();
        private static volatile bool githubPagesUnavailable = false;

        static byte[] TryDownloadUrl(string url, out HttpStatusCode? statusCode)
        {
            statusCode = null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "CivilizationLauncher";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 30000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    statusCode = resp.StatusCode;
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        using (Stream stream = resp.GetResponseStream())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            byte[] data = ms.ToArray();
                            if (data.Length > 0) return data;
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    statusCode = errResp.StatusCode;
                    try { errResp.Close(); } catch { }
                }
                Log("Asset download error (" + url + "): " + wex.Message);
            }
            catch (Exception ex)
            {
                Log("Asset download error (" + url + "): " + ex.Message);
            }
            return null;
        }

        static byte[] DownloadAssetFromGitHub(string assetRelPath)
        {
            if (string.IsNullOrEmpty(assetRelPath)) return null;
            string cleanRel = Uri.UnescapeDataString(assetRelPath).Replace('\\', '/').TrimStart('/');
            if (!cleanRel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || cleanRel.Contains(".."))
            {
                return null;
            }

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                if (ServicePointManager.DefaultConnectionLimit < 16)
                {
                    ServicePointManager.DefaultConnectionLimit = 16;
                }

                byte[] downloaded = null;
                HttpStatusCode? status = null;

                if (!githubPagesUnavailable)
                {
                    string pagesUrl = "https://civilkaserver-sketch.github.io/CV-LAUNCHER/" + cleanRel;
                    downloaded = TryDownloadUrl(pagesUrl, out status);
                    if (downloaded == null && status == HttpStatusCode.NotFound)
                    {
                        githubPagesUnavailable = true;
                    }
                }

                if (downloaded == null)
                {
                    string rawUrl = "https://raw.githubusercontent.com/civilkaserver-sketch/CV-LAUNCHER/main/" + cleanRel;
                    downloaded = TryDownloadUrl(rawUrl, out status);
                }

                if (downloaded != null && downloaded.Length > 0)
                {
                    try
                    {
                        string cachePath = Path.Combine(GetLauncherDataDir(), cleanRel.Replace('/', '\\'));
                        string dir = Path.GetDirectoryName(cachePath);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        lock (assetDownloadLock)
                        {
                            if (!File.Exists(cachePath))
                            {
                                File.WriteAllBytes(cachePath, downloaded);
                            }
                        }
                    }
                    catch { }
                    return downloaded;
                }
            }
            catch (Exception ex)
            {
                Log("Asset download error (" + cleanRel + "): " + ex.Message);
            }
            return null;
        }

        private static readonly object thumbGenLock = new object();

        static System.Drawing.Imaging.ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders())
            {
                if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) return codec;
            }
            return null;
        }

        static byte[] GetOrCreateScreenshotThumb(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return null;

                string keyStr = fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                string hashHex;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(keyStr));
                    hashHex = BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
                }

                string thumbDir = Path.Combine(GetLauncherDataDir(), "thumbs");
                string thumbPath = Path.Combine(thumbDir, hashHex + ".jpg");
                if (File.Exists(thumbPath))
                {
                    return File.ReadAllBytes(thumbPath);
                }

                lock (thumbGenLock)
                {
                    if (File.Exists(thumbPath))
                    {
                        return File.ReadAllBytes(thumbPath);
                    }
                    if (!Directory.Exists(thumbDir)) Directory.CreateDirectory(thumbDir);

                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var srcImg = System.Drawing.Image.FromStream(fs, false, false))
                    {
                        int maxW = 480;
                        int w = srcImg.Width > 0 ? srcImg.Width : maxW;
                        int h = srcImg.Height > 0 ? srcImg.Height : 270;
                        if (w > maxW)
                        {
                            h = Math.Max(1, (int)Math.Round(h * (maxW / (double)w)));
                            w = maxW;
                        }

                        using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                            g.DrawImage(srcImg, 0, 0, w, h);

                            using (var ms = new MemoryStream())
                            {
                                var jpegCodec = GetJpegCodec();
                                if (jpegCodec != null)
                                {
                                    using (var encParams = new System.Drawing.Imaging.EncoderParameters(1))
                                    {
                                        encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                                        bmp.Save(ms, jpegCodec, encParams);
                                    }
                                }
                                else
                                {
                                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                                }
                                byte[] jpgBytes = ms.ToArray();
                                try
                                {
                                    string tmpPath = thumbPath + ".tmp";
                                    File.WriteAllBytes(tmpPath, jpgBytes);
                                    if (File.Exists(thumbPath)) File.Delete(thumbPath);
                                    File.Move(tmpPath, thumbPath);
                                }
                                catch { }
                                return jpgBytes;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static void ServeScreenshotFile(HttpListenerContext ctx, string filePath, bool isThumb)
        {
            byte[] bytes = null;
            string contentType = "image/png";

            if (isThumb)
            {
                bytes = GetOrCreateScreenshotThumb(filePath);
                if (bytes != null) contentType = "image/jpeg";
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
                ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Flush();
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        static void ServeFile(HttpListenerContext ctx, string filePath, string contentType, string resName = null)

        {

            byte[] bytes = null;

            if (File.Exists(filePath))

            {

                try { bytes = File.ReadAllBytes(filePath); } catch { }

            }



            if (bytes == null && !string.IsNullOrEmpty(resName))

            {

                try

                {

                    string cachePath = Path.Combine(GetLauncherDataDir(), resName.Replace('/', '\\'));

                    if (File.Exists(cachePath))

                    {

                        bytes = File.ReadAllBytes(cachePath);

                    }

                }

                catch { }

            }



            if (bytes == null && !string.IsNullOrEmpty(resName))

            {

                bytes = GetEmbeddedResource(resName);

            }



            if (bytes == null && !string.IsNullOrEmpty(resName) && resName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))

            {

                bytes = DownloadAssetFromGitHub(resName);

            }



            if (bytes == null)

            {

                ctx.Response.StatusCode = 404;

                ctx.Response.Close();

                return;

            }



            try

            {

                if (!string.IsNullOrEmpty(resName) && resName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
                }
                else
                {
                    ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                    ctx.Response.AddHeader("Pragma", "no-cache");
                    ctx.Response.AddHeader("Expires", "0");
                }

                ctx.Response.ContentType = contentType;

                ctx.Response.ContentLength64 = bytes.Length;

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
            ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            ctx.Response.AddHeader("Pragma", "no-cache");

            ctx.Response.ContentLength64 = bytes.Length;

            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

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



        // Сетевая/временная ошибка обновления. Такую ошибку НЕЛЬЗЯ лечить полной
        // перезаливкой 300 МБ: файлы уже частично обновлены, повтор должен быть дешёвым.
        class TransientUpdateException : Exception

        {

            public TransientUpdateException(string msg) : base(msg) { }

        }



        static void CheckCancelled()

        {

            if (cancelRequested) throw new OperationCancelled();

        }



        static void HandleApi(HttpListenerContext ctx, string endpoint)

        {

            var jss = new JavaScriptSerializer();



            if (endpoint == "set-version")
            {
                try
                {
                    string body = ReadBody(ctx);
                    var data = jss.Deserialize<Dictionary<string, object>>(body);
                    string reqVer = data != null && data.ContainsKey("version") && data["version"] != null ? data["version"].ToString() : "26.3";
                    string ver = NormalizeBuildVersion(reqVer);
                    SetSelectedBuildVersion(ver);
                    ThreadPool.QueueUserWorkItem((s) => FetchRemoteShaInBackground(ver));
                    SendJson(ctx, new Dictionary<string, object> {
                        { "ok", true },
                        { "selectedVersion", ver }
                    });
                }
                catch (Exception ex)
                {
                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
                }
                return;
            }

            if (endpoint == "status")
            {
                string mcDir = GetActiveMcDir();
                string reqVer = ctx.Request.QueryString["version"];
                string selVer = !string.IsNullOrEmpty(reqVer) ? NormalizeBuildVersion(reqVer) : GetSelectedBuildVersion();

                bool legacyInstalled = false;
                bool installed = !string.IsNullOrEmpty(mcDir) && TestIsVersionInstalled(mcDir, selVer, out legacyInstalled);

                ThreadPool.QueueUserWorkItem((s) => { try { AdServerBlocker.CleanAll(mcDir); } catch { } });

                string installedAt = "";
                string localSha = "";
                var state = LoadState(selVer);
                if (state != null)
                {
                    if (state.ContainsKey("installedAt")) installedAt = (string)state["installedAt"];
                    if (state.ContainsKey("commitSha")) localSha = (string)state["commitSha"];
                }

                string remoteSha = installed ? GetCachedOrFetchRemoteSha(selVer) : null;
                bool updateAvailable = false;
                if (installed && !string.IsNullOrEmpty(remoteSha))
                {
                    if (string.IsNullOrEmpty(localSha) || !localSha.Equals(remoteSha, StringComparison.OrdinalIgnoreCase))
                    {
                        updateAvailable = true;
                    }
                }

                int jMaj = 0;
                int jNeed = selVer == "1.21.8" ? 21 : 25;
                if (installed)
                {
                    string verCivDir = ResolveCivDirForVersion(mcDir, selVer, false);
                    var jinfo = GetRequiredJavaInfo(mcDir, null, verCivDir);
                    jNeed = jinfo.Major;
                    string jExe = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}\bin\java.exe", jinfo.Component));
                    jMaj = GetJavaMajor(jExe);
                }

                bool leg263 = false;
                bool inst263 = !string.IsNullOrEmpty(mcDir) && TestIsVersionInstalled(mcDir, "26.3", out leg263);
                bool leg1218 = false;
                bool inst1218 = !string.IsNullOrEmpty(mcDir) && TestIsVersionInstalled(mcDir, "1.21.8", out leg1218);
                var versionsStatus = new Dictionary<string, object> {
                    { "26.3", new Dictionary<string, object> {
                        { "installed", inst263 },
                        { "legacyInstalled", leg263 }
                    } },
                    { "1.21.8", new Dictionary<string, object> {
                        { "installed", inst1218 },
                        { "legacyInstalled", false }
                    } }
                };

                string curLauncherSha = GetCurrentLauncherSha();

                string remoteLauncherSha = IsDevWorkspace() ? curLauncherSha : GetCachedOrFetchLauncherRemoteSha();

                bool launcherUpdateAvailable = !IsDevWorkspace() && !string.IsNullOrEmpty(remoteLauncherSha) && !remoteLauncherSha.StartsWith(curLauncherSha, StringComparison.OrdinalIgnoreCase);



                SendJson(ctx, new Dictionary<string, object> {
                    { "selectedVersion", selVer },
                    { "installed", installed },
                    { "legacyInstalled", legacyInstalled },
                    { "versions", versionsStatus },
                    { "updateAvailable", updateAvailable },
                    { "localSha", localSha ?? "" },
                    { "remoteSha", remoteSha ?? "" },
                    { "launcherUpdateAvailable", launcherUpdateAvailable },
                    { "launcherUpdateState", launcherUpdateState },
                    { "launcherUpdatePct", launcherUpdatePct },
                    { "launcherUpdateMsg", launcherUpdateMsg },
                    { "currentLauncherSha", curLauncherSha ?? "" },
                    { "remoteLauncherSha", remoteLauncherSha ?? "" },
                    { "mcDir", mcDir ?? "" },
                    { "installedAt", installedAt },
                    { "javaMajor", jMaj },
                    { "javaNeed", jNeed },
                    { "javaOk", jMaj >= jNeed },
                    { "smartRamMb", CalculateOptimalRamMb() },
                    { "totalRamGb", Math.Round(GetTotalPhysicalRamGb(), 1) },
                    { "performanceMode", IsPerformanceModeEnabled() }
                });

                return;

            }



            if (endpoint == "check-update")
            {
                string mcDir = GetActiveMcDir();
                string reqVer = ctx.Request.QueryString["version"];
                string selVer = !string.IsNullOrEmpty(reqVer) ? NormalizeBuildVersion(reqVer) : GetSelectedBuildVersion();

                bool legacyInstalled = false;
                bool installed = !string.IsNullOrEmpty(mcDir) && TestIsVersionInstalled(mcDir, selVer, out legacyInstalled);
                string remoteSha = FetchRemoteShaSynchronous(selVer);
                string localSha = "";
                var state = LoadState(selVer);
                if (state != null && state.ContainsKey("commitSha")) localSha = (string)state["commitSha"];
                bool updateAvailable = installed && !string.IsNullOrEmpty(remoteSha) && (string.IsNullOrEmpty(localSha) || !localSha.Equals(remoteSha, StringComparison.OrdinalIgnoreCase));

                SendJson(ctx, new Dictionary<string, object> {

                    { "installed", installed },
                    { "legacyInstalled", legacyInstalled },

                    { "updateAvailable", updateAvailable },

                    { "localSha", localSha ?? "" },

                    { "remoteSha", remoteSha ?? "" }

                });

                return;

            }



            if (endpoint == "check-launcher-update")

            {

                if (IsDevWorkspace())

                {

                    string sha = GetCurrentLauncherSha();

                    SendJson(ctx, new Dictionary<string, object> {

                        { "updateAvailable", false },

                        { "currentSha", sha ?? "" },

                        { "remoteSha", sha ?? "" }

                    });

                    return;

                }

                string curSha = GetCurrentLauncherSha();

                lock (launcherCheckLock) { lastLauncherCheck = DateTime.MinValue; }

                string remoteSha = FetchLauncherRemoteShaSynchronous();

                bool updateAvail = !string.IsNullOrEmpty(remoteSha) && !remoteSha.StartsWith(curSha, StringComparison.OrdinalIgnoreCase);

                SendJson(ctx, new Dictionary<string, object> {

                    { "updateAvailable", updateAvail },

                    { "currentSha", curSha ?? "" },

                    { "remoteSha", remoteSha ?? "" }

                });

                return;

            }



            if (endpoint == "update-launcher")
            {
                if (IsDevWorkspace())
                {
                    Log("update-launcher rejected: .git workspace detected. Local changes will not be overwritten.");
                    SendJson(ctx, new Dictionary<string, object> {
                        { "ok", false },
                        { "message", "Обновление отключено: обнаружен репозиторий разработки (.git)." }
                    });
                    return;
                }

                launcherUpdateState = "downloading";
                launcherUpdatePct = 0;
                launcherUpdateMsg = "Подключение к GitHub...";

                ThreadPool.QueueUserWorkItem((s) => {
                    try
                    {
                        Log("=== Starting self-update for launcher ===");
                        string tempDir = Path.GetTempPath();
                        string newExePath = Path.Combine(tempDir, "CV_Launcher_new.exe");
                        string newUiPath = Path.Combine(tempDir, "ui_new.html");

                        string remoteSha = FetchLauncherRemoteShaSynchronous();
                        Log("Downloading new launcher binary from GitHub with progress...");
                        bool okExe = DownloadFileFromRepo("CV Launcher.exe", newExePath, (pct, msg) => {
                            launcherUpdatePct = pct;
                            launcherUpdateMsg = msg;
                        });

                        if (!okExe)
                        {
                            launcherUpdateState = "error";
                            launcherUpdateMsg = "Не удалось скачать CV Launcher.exe с GitHub";
                            Log("Failed to download CV Launcher.exe");
                            return;
                        }

                        Log("Launcher binary downloaded successfully, triggering updater batch...");
                        ExecuteLauncherUpdate(newExePath, newUiPath, remoteSha);
                    }
                    catch (Exception ex)
                    {
                        launcherUpdateState = "error";
                        launcherUpdateMsg = ex.Message;
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

                string initial = GetActiveMcDir();

                IntPtr ownerHwnd = MainBrowserHwnd;

                var t = new Thread(() => {

                    chosen = ModernFolderBrowser.ShowDialog(ownerHwnd, "Выберите папку .minecraft", initial);

                });

                t.SetApartmentState(ApartmentState.STA);

                t.Start();

                t.Join();



                bool isInst = false;

                if (!string.IsNullOrEmpty(chosen))

                {

                    chosen = chosen.Trim().TrimEnd('\\', '/');

                    isInst = TestIsInstalled(chosen);

                    SaveCustomMcDir(chosen);

                    Log(string.Format("User selected custom Minecraft folder: '{0}' (Civilization detected: {1})", chosen, isInst));

                }



                string activeDir = GetActiveMcDir();

                SendJson(ctx, new Dictionary<string, object> { 

                    { "path", activeDir },

                    { "installed", TestIsVersionInstalledForSelected(activeDir) },
                    { "legacyInstalled", IsLegacyVersionInstalled(activeDir) && GetSelectedBuildVersion() == "26.3" }

                });

                return;

            }



            if (endpoint == "set-mc-dir")

            {

                string body = ReadBody(ctx);

                Dictionary<string, object> data = null;

                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }

                string chosen = null;

                if (data != null && data.ContainsKey("path")) chosen = data["path"] as string;

                if (!string.IsNullOrEmpty(chosen))

                {

                    chosen = chosen.Trim().TrimEnd('\\', '/');

                    SaveCustomMcDir(chosen);

                    Log(string.Format("User manually set custom Minecraft folder: '{0}'", chosen));

                    ThreadPool.QueueUserWorkItem((s) => { try { AdServerBlocker.CleanAll(chosen); AdServerBlocker.SetupWatchers(); } catch { } });

                }

                string activeDir = GetActiveMcDir();

                SendJson(ctx, new Dictionary<string, object> {

                    { "ok", true },

                    { "path", activeDir },

                    { "installed", TestIsVersionInstalledForSelected(activeDir) },
                    { "legacyInstalled", IsLegacyVersionInstalled(activeDir) && GetSelectedBuildVersion() == "26.3" }

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
                string ver = (data != null && data.ContainsKey("version") && data["version"] != null) ? NormalizeBuildVersion(data["version"].ToString()) : GetSelectedBuildVersion();
                SetSelectedBuildVersion(ver);

                cancelRequested = false;
                SetProgress(5, "Подготовка к установке...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoInstall(targetDir, ver));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "update")
            {
                if (Volatile.Read(ref updateInProgress) != 0)
                {
                    Log("update endpoint: update already running, request ignored.");
                    SendJson(ctx, new Dictionary<string, object> { { "status", "already-running" } });
                    return;
                }

                string body = ReadBody(ctx);
                Dictionary<string, object> data = null;
                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }
                string targetDir = null;
                if (data != null && data.ContainsKey("mcDir")) targetDir = (string)data["mcDir"];
                if (string.IsNullOrEmpty(targetDir)) targetDir = GetTargetMcDir();
                string ver = (data != null && data.ContainsKey("version") && data["version"] != null) ? NormalizeBuildVersion(data["version"].ToString()) : GetSelectedBuildVersion();
                SetSelectedBuildVersion(ver);

                cancelRequested = false;
                SetProgress(10, "Проверяем наличие обновлений...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoUpdate(targetDir, true, ver));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "uninstall")
            {
                string body = ReadBody(ctx);
                Dictionary<string, object> data = null;
                try { data = jss.Deserialize<Dictionary<string, object>>(body); } catch { }
                string ver = (data != null && data.ContainsKey("version") && data["version"] != null) ? NormalizeBuildVersion(data["version"].ToString()) : GetSelectedBuildVersion();
                string mcDir = GetTargetMcDir();
                bool uninstLegacy = false;
                bool uninstHasVer = !string.IsNullOrEmpty(mcDir) && (TestIsVersionInstalled(mcDir, ver, out uninstLegacy) || uninstLegacy);
                if (string.IsNullOrEmpty(mcDir) || !uninstHasVer)
                {
                    SendJson(ctx, new Dictionary<string, object> { { "error", "Civilization (" + ver + ") не установлена в текущей папке" } });
                    return;
                }
                cancelRequested = false;
                SetProgress(5, "Подготовка к удалению...", true, false, null);
                ThreadPool.QueueUserWorkItem((s) => DoUninstall(mcDir, ver));
                SendJson(ctx, new Dictionary<string, object> { { "status", "started" } });
                return;
            }

            if (endpoint == "open-folder")

            {

                string explicitPath = null;

                string subfolder = null;

                try

                {

                    if (ctx.Request.HasEntityBody)

                    {

                        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))

                        {

                            string body = reader.ReadToEnd();

                            if (!string.IsNullOrEmpty(body))

                            {

                                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);

                                if (json != null && json.ContainsKey("path"))

                                {

                                    string p = json["path"] as string;

                                    if (!string.IsNullOrEmpty(p)) explicitPath = p.Trim();

                                }

                                if (json != null && json.ContainsKey("subfolder"))

                                {

                                    string sf = json["subfolder"] as string;

                                    if (!string.IsNullOrEmpty(sf)) subfolder = sf.Trim();

                                }

                            }

                        }

                    }

                }

                catch { }



                string mcDir = (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath))

                    ? explicitPath

                    : GetActiveMcDir();



                string targetPath = null;

                if (!string.IsNullOrEmpty(mcDir))

                {

                    // Папка установленной версии игры: .minecraft/versions/Civilization

                    string civDir = Path.Combine(mcDir, @"versions\Civilization");

                    if (Directory.Exists(civDir))

                    {

                        targetPath = civDir;

                    }

                    else

                    {

                        try

                        {

                            Directory.CreateDirectory(civDir);

                            targetPath = civDir;

                        }

                        catch

                        {

                            targetPath = Path.Combine(mcDir, "versions");

                            if (!Directory.Exists(targetPath)) targetPath = mcDir;

                        }

                    }

                }

                else if (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath))

                {

                    targetPath = explicitPath;

                }



                if (!string.IsNullOrEmpty(targetPath) && !string.IsNullOrEmpty(subfolder))

                {

                    string sub = Path.Combine(targetPath, subfolder);

                    if (!Directory.Exists(sub)) { try { Directory.CreateDirectory(sub); } catch { } }

                    if (Directory.Exists(sub)) targetPath = sub;

                }

                if (!string.IsNullOrEmpty(targetPath))

                {

                    Log("Opening game folder in Explorer: " + targetPath);

                    OpenPathInExplorer(targetPath, false);

                }

                SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", targetPath } });

                return;

            }



            if (endpoint == "mods")
            {
                string mcDir = GetActiveMcDir();
                string reqVer = ctx.Request.QueryString["version"];
                string selVer = !string.IsNullOrEmpty(reqVer) ? NormalizeBuildVersion(reqVer) : GetSelectedBuildVersion();
                string modsDir = Path.Combine(ResolveCivDirForVersion(mcDir, selVer, false), "mods");
                var list = new System.Collections.Generic.List<Dictionary<string, object>>();

                if (Directory.Exists(modsDir))

                {

                    foreach (string f in Directory.GetFiles(modsDir, "*.jar"))

                    {

                        var fi = new FileInfo(f);

                        list.Add(new Dictionary<string, object> {

                            { "name", Path.GetFileNameWithoutExtension(f) },

                            { "filename", fi.Name },

                            { "size", fi.Length }

                        });

                    }

                }

                SendJson(ctx, new Dictionary<string, object> {

                    { "ok", true },

                    { "count", list.Count },

                    { "mods", list }

                });

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

                    OpenFileInNotepad(logPath);

                    SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", logPath } });

                }

                catch (Exception ex)

                {

                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });

                }

                return;

            }



            if (endpoint == "open-log-folder")

            {

                string logPath = GetLogPath();

                try

                {

                    string dir = Path.GetDirectoryName(logPath);

                    if (File.Exists(logPath))

                    {

                        OpenPathInExplorer(logPath, true);

                    }

                    else if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))

                    {

                        OpenPathInExplorer(dir, false);

                    }

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
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine("           СИСТЕМНЫЕ ЛОГИ CIVILIZATION (ЛАУНЧЕР + MINECRAFT)");
                sb.AppendLine("           Дата: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("           ОС: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)") + " | RAM: " + Math.Round(GetTotalPhysicalRamGb(), 1) + " GB");
                sb.AppendLine("           Пользователь: " + Environment.UserName + " | ПК: " + Environment.MachineName);
                sb.AppendLine("================================================================================");
                sb.AppendLine();

                // 1. ЛОГ ЛАУНЧЕРА
                string logPath = GetLogPath();
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(" [1/3] ЛОГ ЛАУНЧЕРА (launcher.log - последние 500 строк)");
                sb.AppendLine(" Файл: " + (logPath ?? "не найден"));
                sb.AppendLine("--------------------------------------------------------------------------------");
                try
                {
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            var q = new Queue<string>();
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                q.Enqueue(line);
                                if (q.Count > 500) q.Dequeue();
                            }
                            sb.AppendLine(string.Join("\r\n", q.ToArray()));
                        }
                    }
                    else
                    {
                        sb.AppendLine("(Файл launcher.log пока пуст или не найден)");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Ошибка чтения launcher.log: " + ex.Message);
                }

                sb.AppendLine();

                // 2. ЛОГ MINECRAFT (latest.log)
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(" [2/3] ЛОГ КЛИЕНТА MINECRAFT (latest.log - последние 600 строк)");
                string mcDir = GetTargetMcDir();
                var candidateLogs = new List<string>();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    candidateLogs.Add(Path.Combine(mcDir, @"versions\Civilization\logs\latest.log"));
                    candidateLogs.Add(Path.Combine(mcDir, @"logs\latest.log"));
                }
                string appDataMc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
                candidateLogs.Add(Path.Combine(appDataMc, @"versions\Civilization\logs\latest.log"));
                candidateLogs.Add(Path.Combine(appDataMc, @"logs\latest.log"));

                string foundMcLog = null;
                foreach (var lp in candidateLogs)
                {
                    if (File.Exists(lp))
                    {
                        foundMcLog = lp;
                        break;
                    }
                }

                sb.AppendLine(" Файл: " + (foundMcLog ?? "latest.log не найден"));
                sb.AppendLine("--------------------------------------------------------------------------------");
                try
                {
                    if (foundMcLog != null && File.Exists(foundMcLog))
                    {
                        using (var fs = new FileStream(foundMcLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            var q = new Queue<string>();
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                q.Enqueue(line);
                                if (q.Count > 600) q.Dequeue();
                            }
                            sb.AppendLine(string.Join("\r\n", q.ToArray()));
                        }
                    }
                    else
                    {
                        sb.AppendLine("(Файл latest.log не найден — возможно, игра ещё не запускалась)");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Ошибка чтения latest.log: " + ex.Message);
                }

                sb.AppendLine();

                // 3. КРАШ-РЕПОРТ MINECRAFT (если был вылет)
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(" [3/3] СВЕЖИЙ КРАШ-РЕПОРТ MINECRAFT (crash-reports)");
                var candidateCrashDirs = new List<string>();
                if (!string.IsNullOrEmpty(mcDir))
                {
                    candidateCrashDirs.Add(Path.Combine(mcDir, @"versions\Civilization\crash-reports"));
                    candidateCrashDirs.Add(Path.Combine(mcDir, @"crash-reports"));
                }
                candidateCrashDirs.Add(Path.Combine(appDataMc, @"versions\Civilization\crash-reports"));
                candidateCrashDirs.Add(Path.Combine(appDataMc, @"crash-reports"));

                FileInfo latestCrash = null;
                foreach (var cDir in candidateCrashDirs)
                {
                    if (Directory.Exists(cDir))
                    {
                        try
                        {
                            var di = new DirectoryInfo(cDir);
                            foreach (var f in di.GetFiles("crash-*.txt"))
                            {
                                if (latestCrash == null || f.LastWriteTime > latestCrash.LastWriteTime)
                                {
                                    latestCrash = f;
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (latestCrash != null && (DateTime.Now - latestCrash.LastWriteTime).TotalHours < 72)
                {
                    sb.AppendLine(" Файл: " + latestCrash.FullName + " (от " + latestCrash.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                    sb.AppendLine("--------------------------------------------------------------------------------");
                    try
                    {
                        string crashText = File.ReadAllText(latestCrash.FullName, Encoding.UTF8);
                        if (crashText.Length > 15000) crashText = crashText.Substring(0, 15000) + "\r\n... [обрезано]";
                        sb.AppendLine(crashText);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("Ошибка чтения краш-репорта: " + ex.Message);
                    }
                }
                else
                {
                    sb.AppendLine(" (Свежих краш-репортов не обнаружено — вылетов за последние 72ч не зафиксировано)");
                }

                sb.AppendLine("================================================================================");

                SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", logPath }, { "content", sb.ToString() } });
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

            if (endpoint == "server-status")
            {
                ThreadPool.QueueUserWorkItem((s) => {
                    try
                    {
                        var status = GetCachedServerStatus();
                        SendJson(ctx, new Dictionary<string, object> {
                            { "ok", true },
                            { "online", status.online },
                            { "ping", status.pingMs },
                            { "players", status.playersOnline >= 0 ? status.playersOnline : 0 },
                            { "maxPlayers", status.playersMax },
                            { "host", status.host },
                            { "port", status.port }
                        });
                    }
                    catch (Exception ex)
                    {
                        SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "online", false }, { "error", ex.Message } });
                    }
                });
                return;
            }

            if (endpoint == "network-diag")
            {
                ThreadPool.QueueUserWorkItem((s) => {
                    try
                    {
                        var diag = RunNetworkDiagnostics(true);
                        SendJson(ctx, new Dictionary<string, object> {
                            { "ok", true },
                            { "targetHost", diag.targetHost },
                            { "targetPort", diag.targetPort },
                            { "tcp25565Ok", diag.tcp25565Ok },
                            { "tcp25565TimeMs", diag.tcp25565TimeMs },
                            { "tcp25565Error", diag.tcp25565Error ?? "" },
                            { "tcp25566Ok", diag.tcp25566Ok },
                            { "tcp25566TimeMs", diag.tcp25566TimeMs },
                            { "tcp25566Error", diag.tcp25566Error ?? "" },
                            { "pingOk", diag.pingOk },
                            { "pingTimeMs", diag.pingTimeMs },
                            { "localRadminFound", diag.localRadminFound },
                            { "localRadminIp", diag.localRadminIp ?? "" },
                            { "isHostSelf", diag.isHostSelf },
                            { "playersOnline", diag.playersOnline },
                            { "playersMax", diag.playersMax },
                            { "logs", diag.logLines }
                        });
                    }
                    catch (Exception ex)
                    {
                        SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
                    }
                });
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

                string body = ReadBody(ctx);

                string name = null;

                if (!string.IsNullOrEmpty(body))

                {

                    try

                    {

                        var data = jss.Deserialize<Dictionary<string, object>>(body);

                        if (data != null && data.ContainsKey("name")) name = data["name"].ToString();

                    }

                    catch { }

                }

                if (string.IsNullOrEmpty(name))

                {

                    name = ctx.Request.QueryString["name"];

                }



                if (!string.IsNullOrEmpty(name))

                {

                    string target = FindScreenshotByName(name);

                    if (target != null && File.Exists(target))

                    {

                        OpenPathInExplorer(target, true);

                        SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "path", target } });

                        return;

                    }

                }



                string mcDir = GetActiveMcDir();

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

                OpenPathInExplorer(sDir, false);

                SendJson(ctx, new Dictionary<string, object> { { "ok", true } });

                return;

            }



            if (endpoint == "screenshots")

            {

                SetupScreenshotWatchers();

                string mcDir = GetActiveMcDir();

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

                            { "src", "/api/screenshot-file?b64=" + Uri.EscapeDataString(b64) + "&name=" + Uri.EscapeDataString(fi.Name) + "&v=" + fi.LastWriteTimeUtc.Ticks },

                            { "thumbSrc", "/api/screenshot-file?b64=" + Uri.EscapeDataString(b64) + "&name=" + Uri.EscapeDataString(fi.Name) + "&v=" + fi.LastWriteTimeUtc.Ticks + "&thumb=1" }

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

                    ServeScreenshotFile(ctx, targetFile, ctx.Request.QueryString["thumb"] == "1");

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
                string reqVer = ctx.Request.QueryString["version"];
                if (string.IsNullOrEmpty(reqVer))
                {
                    try
                    {
                        string rawSg = ReadBody(ctx);
                        if (!string.IsNullOrEmpty(rawSg))
                        {
                            var sgData = jss.Deserialize<Dictionary<string, object>>(rawSg);
                            if (sgData != null && sgData.ContainsKey("version") && sgData["version"] != null) reqVer = sgData["version"].ToString();
                        }
                    }
                    catch { }
                }
                string selVer = !string.IsNullOrEmpty(reqVer) ? NormalizeBuildVersion(reqVer) : GetSelectedBuildVersion();
                SetSelectedBuildVersion(selVer);
                string mcDir = GetTargetMcDir();
                bool startLegNeed;
                if (!TestIsVersionInstalled(mcDir, selVer, out startLegNeed))
                {
                    SendJson(ctx, new Dictionary<string, object> {
                        { "ok", false },
                        { "msg", startLegNeed ? "Установлена старая версия 1.21.8. Нажмите «Переустановить» или выберите 1.21.8." : "Сборка " + selVer + " ещё не установлена" }
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

                bool started = LaunchCivilizationGame(mcDir, selVer, out err);

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

                string newsPath = Path.Combine(appDir, "news.json");

                if (File.Exists(newsPath))

                {

                    ServeFile(ctx, newsPath, "application/json; charset=utf-8", "news.json");

                    return;

                }

                byte[] emb = GetEmbeddedResource("news.json");

                if (emb != null)

                {

                    ServeBytes(ctx, emb, "application/json; charset=utf-8");

                    return;

                }

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

                int count = AdServerBlocker.CleanAll(mcDir, true);

                SendJson(ctx, new Dictionary<string, object> {

                    { "ok", true },

                    { "removed", count },

                    { "msg", count > 0 ? string.Format("Удалено рекламных серверов: {0}", count) : "Список серверов чист (реклама не найдена)" }

                });

                return;

            }



            if (endpoint == "save-skin")

            {

                try

                {

                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))

                    {

                        string body = reader.ReadToEnd();

                        var data = jss.Deserialize<Dictionary<string, object>>(body);

                        if (data != null)

                        {

                            string b64 = data.ContainsKey("skin") && data["skin"] != null ? data["skin"].ToString() : null;

                            string model = data.ContainsKey("model") && data["model"] != null ? data["model"].ToString().Trim().ToLower() : "classic";

                            if (model != "slim") model = "classic";

                            string name = data.ContainsKey("name") && data["name"] != null ? data["name"].ToString() : "";



                            string mcDir = GetTargetMcDir();

                            string civDir = !string.IsNullOrEmpty(mcDir) ? Path.Combine(mcDir, @"versions\Civilization") : null;

                            string localSkinDir = GetLauncherSkinsDir();



                            if (string.IsNullOrEmpty(b64))

                            {

                                SkinManager.RemoveSkinFiles(localSkinDir);

                                if (!string.IsNullOrEmpty(civDir)) SkinManager.RemoveSkinFiles(civDir);

                                if (!string.IsNullOrEmpty(mcDir)) SkinManager.RemoveSkinFiles(mcDir);

                                Log("[SkinManager] Скин очищен пользователем");

                                NotifyFriendsChanged();
                            SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "cleared", true } });

                                return;

                            }



                            if (b64.Contains(",")) b64 = b64.Substring(b64.IndexOf(",") + 1);

                            byte[] bytes = Convert.FromBase64String(b64);



                            int width, height;

                            string valError;

                            if (!SkinManager.ValidatePng(bytes, out width, out height, out valError))

                            {

                                Log(string.Format("[SkinManager] Ошибка валидации скина: {0}", valError));

                                SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", valError } });

                                return;

                            }



                            // 1. Сохраняем в локальное хранилище лаунчера appDir\skins

                            SkinManager.SaveSkinFiles(localSkinDir, bytes, model);

                            if (!string.IsNullOrEmpty(name))

                            {

                                try { File.WriteAllText(Path.Combine(localSkinDir, "name.txt"), name); } catch { }

                            }



                            // 2. Сохраняем в рабочую папку версии игры (<mcDir>\versions\Civilization)

                            if (!string.IsNullOrEmpty(civDir))

                            {

                                SkinManager.SaveSkinFiles(civDir, bytes, model);

                            }



                            // 3. Сохраняем в корневую папку Minecraft (<mcDir>)

                            if (!string.IsNullOrEmpty(mcDir))

                            {

                                SkinManager.SaveSkinFiles(mcDir, bytes, model);

                            }



                            Log(string.Format("[SkinManager] Скин сохранен ({0}x{1}, модель: {2}) в {3}", width, height, model, civDir ?? mcDir));

                            NotifyFriendsChanged();
                            SendJson(ctx, new Dictionary<string, object> {

                                { "ok", true },

                                { "width", width },

                                { "height", height },

                                { "model", model }

                            });

                            return;

                        }

                    }

                }

                catch (Exception ex)

                {

                    Log(string.Format("[SkinManager] Ошибка save-skin: {0}", ex.Message));

                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });

                    return;

                }

            }



            if (endpoint == "get-skin")

            {

                try

                {

                    string localSkinDir = GetLauncherSkinsDir();

                    string skinFile = Path.Combine(localSkinDir, "current_skin.png");

                    if (File.Exists(skinFile))

                    {

                        byte[] bytes = File.ReadAllBytes(skinFile);

                        string model = "classic";

                        string jsonFile = Path.Combine(localSkinDir, "current_skin.json");

                        if (File.Exists(jsonFile))

                        {

                            try

                            {

                                string jtext = File.ReadAllText(jsonFile);

                                if (jtext.IndexOf("\"slim\"", StringComparison.OrdinalIgnoreCase) >= 0)

                                    model = "slim";

                            }

                            catch { }

                        }

                        else

                        {

                            string modelFile = Path.Combine(localSkinDir, "model.txt");

                            if (File.Exists(modelFile)) model = File.ReadAllText(modelFile).Trim().ToLower();

                        }



                        string name = "";

                        string nameFile = Path.Combine(localSkinDir, "name.txt");

                        if (File.Exists(nameFile)) name = File.ReadAllText(nameFile).Trim();



                        SendJson(ctx, new Dictionary<string, object> {

                            { "ok", true },

                            { "skin", "data:image/png;base64," + Convert.ToBase64String(bytes) },

                            { "model", model == "slim" ? "slim" : "classic" },

                            { "name", name }

                        });

                        return;

                    }

                }

                catch { }

                SendJson(ctx, new Dictionary<string, object> { { "ok", false } });

                return;

            }



            if (endpoint == "friends/state")
            {
                HandleFriendsStateApi(ctx);
                return;
            }

            if (endpoint == "friends/skin")
            {
                HandleFriendsSkinApi(ctx);
                return;
            }

            if (endpoint == "friends/chat")
            {
                HandleFriendsChatApi(ctx);
                return;
            }

            if (endpoint == "set-performance-mode")

            {

                try

                {

                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))

                    {

                        string body = reader.ReadToEnd();

                        var data = jss.Deserialize<Dictionary<string, object>>(body);

                        bool enabled = false;

                        if (data != null && data.ContainsKey("enabled"))

                        {

                            object val = data["enabled"];

                            if (val is bool) enabled = (bool)val;

                            else if (val != null) bool.TryParse(val.ToString(), out enabled);

                        }

                        SetPerformanceModeEnabled(enabled);

                        string mcDir = GetTargetMcDir();

                        if (!string.IsNullOrEmpty(mcDir))

                        {

                            ApplyPerformanceModeConfig(mcDir, enabled);

                        }

                        Log(string.Format("[PerformanceMode] Режим производительности установлен: {0}", enabled));

                        SendJson(ctx, new Dictionary<string, object> { { "ok", true }, { "performanceMode", enabled } });

                        return;

                    }

                }

                catch (Exception ex)

                {

                    Log(string.Format("[PerformanceMode] Ошибка set-performance-mode: {0}", ex.Message));

                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });

                    return;

                }

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

                                // Только "Minecraft": здесь перебирается и java.exe (сервер тоже java.exe),
                                // а консоль сервера может иметь в заголовке путь "...\Civilization DEV\server".
                                // Иначе запущенный сервер считался бы игрой и блокировал обновление.
                                if (title.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0)

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



        static string FindJavaExe(string mcDir, string explicitCivDir = null)
        {
            var reqInfo = GetRequiredJavaInfo(mcDir, null, explicitCivDir);
            int minMajor = reqInfo.Major > 0 ? reqInfo.Major : 21;
            var candidateRuntimesList = new List<string>();
            if (!string.IsNullOrEmpty(reqInfo.Component)) candidateRuntimesList.Add(reqInfo.Component);
            string[] defaults = new string[] {
                "java-runtime-epsilon",
                "java-runtime-delta",
                "java-runtime-gamma",
                "java-runtime-beta",
                "java-runtime-alpha",
                "jre-x64",
                "java"
            };
            foreach (string dr in defaults)
            {
                if (!candidateRuntimesList.Contains(dr)) candidateRuntimesList.Add(dr);
            }

            foreach (string r in candidateRuntimesList)
            {
                string p1 = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}\bin\javaw.exe", r));
                if (File.Exists(p1) && GetJavaMajor(p1) >= minMajor) return p1;
                string p2 = Path.Combine(mcDir, string.Format(@"runtime\{0}\bin\javaw.exe", r));
                if (File.Exists(p2) && GetJavaMajor(p2) >= minMajor) return p2;
            }

            string tlJvms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\jvms");

            if (Directory.Exists(tlJvms))

            {

                foreach (var d in Directory.GetDirectories(tlJvms))

                {

                    string jw = Path.Combine(d, @"bin\javaw.exe");

                    if (File.Exists(jw) && GetJavaMajor(jw) >= minMajor) return jw;

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

                        if (File.Exists(jw) && GetJavaMajor(jw) >= minMajor) return jw;

                    }

                }

                string msDir = Path.Combine(pf, @"Microsoft\jdk");

                if (Directory.Exists(msDir))

                {

                    foreach (var d in Directory.GetDirectories(msDir))

                    {

                        string jw = Path.Combine(d, @"bin\javaw.exe");

                        if (File.Exists(jw) && GetJavaMajor(jw) >= minMajor) return jw;

                    }

                }

            }



            string jh = Environment.GetEnvironmentVariable("JAVA_HOME");

            if (!string.IsNullOrEmpty(jh))

            {

                string jw = Path.Combine(jh, @"bin\javaw.exe");

                if (File.Exists(jw) && GetJavaMajor(jw) >= minMajor) return jw;

            }



            return null;

        }



        static bool LaunchCivilizationGame(string mcDir, string buildVer, out string error)
        {
            error = null;
            try
            {
                string ver = NormalizeBuildVersion(buildVer);
                try { AdServerBlocker.CleanAll(mcDir); } catch { }
                string civDir = ResolveCivDirForVersion(mcDir, ver, false);
                string jsonPath = Path.Combine(civDir, "Civilization.json");

                if (!File.Exists(jsonPath))

                {

                    error = "Файл версии Civilization.json не найден";

                    return false;

                }



                // Синхронизируем скин в рабочую папку игры перед запуском

                try

                {

                    SkinManager.SyncSkinToGame(appDir, mcDir, civDir);

                }

                catch (Exception ex)

                {

                    Log(string.Format("[SkinManager] Ошибка синхронизации скина перед запуском: {0}", ex.Message));

                }



                // Применяем настройки режима производительности (Sodium / options.txt) перед запуском

                // User video/sodium settings are kept untouched on launch (performance mode only applies when manually toggled in UI)
                // User video/sodium settings are kept untouched on launch (performance mode only applies when manually toggled in UI)
                Log(string.Format("[PerformanceMode] Launching game - preserving player settings (performance mode toggle: {0})", IsPerformanceModeEnabled()));
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

                username = SanitizeMinecraftUsername(username);
                if (string.IsNullOrEmpty(uuid) || uuid.Length != 32 || !Regex.IsMatch(uuid, "^[a-fA-F0-9]{32}$"))
                {
                    uuid = GenerateOfflineUuid(username);
                }
                Log(string.Format("[Account] Подготовлен аккаунт для запуска: {0} (UUID: {1})", username, uuid));



                string currentModel = SkinManager.GetCurrentSkinModel(appDir);

                Log(string.Format("[SkinManager] Выбранная модель скина: {0}, UUID игрока: {1}", currentModel, uuid));



                // 2. Умное выделение памяти (Smart RAM Allocation) на основе физического объема ОЗУ ПК

                double totalRamGb = GetTotalPhysicalRamGb();

                int ramMb = CalculateOptimalRamMb();

                Log(string.Format("[SmartRAM] Авто-подбор памяти для Minecraft: {0} MB (Всего ОЗУ на ПК: {1:F1} GB)", ramMb, totalRamGb));



                try

                {

                    string tlProps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".tlauncher\tlauncher-2.0.properties");

                    if (File.Exists(tlProps))

                    {

                        var lines = new List<string>(File.ReadAllLines(tlProps, Encoding.UTF8));

                        bool hasVer = false;

                        bool hasRam = false;

                        for (int i = 0; i < lines.Count; i++)

                        {

                            if (Regex.IsMatch(lines[i], @"^minecraft\.memory\.ram2=\d+"))

                            {

                                lines[i] = string.Format("minecraft.memory.ram2={0}", ramMb);

                                hasRam = true;

                            }



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

                        if (!hasRam) lines.Add(string.Format("minecraft.memory.ram2={0}", ramMb));

                        File.WriteAllLines(tlProps, lines.ToArray(), Encoding.UTF8);

                    }

                }

                catch { }



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



                string javaExe = FindJavaExe(mcDir, civDir);
                if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe))
                {
                    Log("Required Java is missing before game launch. Triggering EnsureJava on the fly...");
                    try
                    {
                        EnsureJava(mcDir, null, civDir);
                        javaExe = FindJavaExe(mcDir, civDir);
                    }
                    catch (Exception ex)
                    {
                        Log("EnsureJava during launch failed: " + ex.Message);
                    }
                }

                // Если javaw не найден или classpath пуст, запасной вариант — TLauncher в фоне / свернуто
                if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe) || cpList.Count == 0)
                {
                    Log(string.Format("Direct launch not possible (javaExe='{0}', cpCount={1}). Trying TLauncher...", javaExe ?? "null", cpList.Count));

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

                    error = "Не найден Java 21 и не удалось его автоматически загрузить. Проверьте интернет-соединение.";

                    Log("Launch error: " + error);

                    return false;

                }



                string mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient";

                if (root != null && root.ContainsKey("mainClass") && root["mainClass"] != null)

                {

                    string mc = root["mainClass"].ToString().Trim();

                    if (!string.IsNullOrEmpty(mc)) mainClass = mc;

                }



                bool isFabric = mainClass.IndexOf("fabricmc", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                jsonTxt.IndexOf("fabricmc", StringComparison.OrdinalIgnoreCase) >= 0;

                bool isForge = mainClass.IndexOf("minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0 ||

                               jsonTxt.IndexOf("minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0;



                string assetIndex = "34";

                if (root != null && root.ContainsKey("assets") && root["assets"] != null)

                {

                    string ai = root["assets"].ToString().Trim();

                    if (!string.IsNullOrEmpty(ai)) assetIndex = ai;

                }

                try { EnsureAssets(mcDir, root); } catch (Exception ex) { Log("[Assets] EnsureAssets launch error: " + ex.Message); }



                // 4. Построение аргументов команды

                var args = new StringBuilder();

                args.Append("-Dos.name=\"Windows 10\" ");

                args.Append("-Dos.version=10.0 ");

                args.Append("-Dfile.encoding=UTF-8 ");

                args.Append("-Dsun.jnu.encoding=UTF-8 ");

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

                args.Append("--enable-native-access=ALL-UNNAMED ");

                args.Append("--add-exports java.base/jdk.internal.misc=ALL-UNNAMED ");

                args.Append("-Dvoxy.geometryBufferSizeOverrideMB=384 ");

                args.Append(string.Format("-Xmx{0}M ", ramMb));

                args.Append("-XX:+UnlockExperimentalVMOptions ");



                int javaMajor = GetJavaMajor(javaExe);
                args.Append("-XX:+UseG1GC ");
                args.Append("-XX:G1NewSizePercent=20 ");
                args.Append("-XX:G1ReservePercent=20 ");
                args.Append("-XX:MaxGCPauseMillis=50 ");
                args.Append("-XX:G1HeapRegionSize=32M ");
                Log(string.Format("[JVM] Выбран стабильный сборщик мусора: G1GC (Java version: {0})", javaMajor));

                if (isFabric)
                {
                    args.Append("-DFabricMcEmu=\" net.minecraft.client.main.Main \" ");
                }
                else if (isForge)
                {
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
                }



                args.Append(string.Format("-cp \"{0}\" ", string.Join(";", cpList.ToArray())));

                args.Append(mainClass + " ");



                args.Append(string.Format("--username \"{0}\" ", username));

                args.Append("--version \"Civilization\" ");

                args.Append(string.Format("--gameDir \"{0}\" ", civDir));

                args.Append(string.Format("--assetsDir \"{0}\" ", Path.Combine(mcDir, "assets")));

                args.Append(string.Format("--assetIndex \"{0}\" ", assetIndex));

                args.Append(string.Format("--uuid \"{0}\" ", uuid));

                args.Append("--accessToken \"null\" ");

                args.Append("--clientId \"null\" ");

                args.Append("--xuid \"null\" ");

                args.Append("--userType \"mojang\" ");

                args.Append("--versionType \"release\" ");

                args.Append("--width 925 ");

                args.Append("--height 530 ");



                if (isForge)

                {

                    args.Append("--launchTarget \"forge_client\"");

                }



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
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                LogServerReachability();

                activeGameProcess = Process.Start(psi);
                if (activeGameProcess != null)
                {
                    int pid = activeGameProcess.Id;
                    Log(string.Format("Game process spawned successfully! PID: {0}", pid));

                    activeGameProcess.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Log("[Game/STDOUT] " + e.Data);
                        }
                    };
                    activeGameProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Log("[Game/STDERR] " + e.Data);
                        }
                    };
                    try { activeGameProcess.BeginOutputReadLine(); } catch { }
                    try { activeGameProcess.BeginErrorReadLine(); } catch { }

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



        static Dictionary<string, object> LoadState(string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string sFile = GetStateFileForVersion(ver);
            if (File.Exists(sFile))
            {
                try
                {
                    string json = File.ReadAllText(sFile, Encoding.UTF8);
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                }
                catch { }
            }
            return null;
        }

        private static readonly Dictionary<string, string> cachedRemoteShaByVer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> lastRemoteCheckByVer = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> checkingUpdateByVer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static object updateCheckLock = new object();

        static string GetCachedShaOnly(string buildVer)
        {
            string ver = NormalizeBuildVersion(buildVer);
            lock (updateCheckLock)
            {
                string val;
                if (cachedRemoteShaByVer.TryGetValue(ver, out val)) return val;
            }
            return null;
        }

        static string GetCachedOrFetchRemoteSha(string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            string curVal = null;
            lock (updateCheckLock)
            {
                DateTime lastChk;
                if (cachedRemoteShaByVer.TryGetValue(ver, out curVal) &&
                    lastRemoteCheckByVer.TryGetValue(ver, out lastChk) &&
                    (DateTime.Now - lastChk).TotalSeconds < 30)
                {
                    return curVal;
                }
            }

            bool alreadyChecking;
            lock (updateCheckLock) { alreadyChecking = checkingUpdateByVer.Contains(ver); }
            if (!alreadyChecking)
            {
                ThreadPool.QueueUserWorkItem((s) => FetchRemoteShaInBackground(ver));
            }
            return curVal;
        }

        static string FetchRemoteShaSynchronous(string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            string branch = GetRepoBranchForVersion(ver);

            if (ver == "26.3")
            {
                string localVersionRepo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\version_repo");
                if (!Directory.Exists(localVersionRepo) && IsDevEnvironment)
                {
                    localVersionRepo = @"E:\Civilization DEV\version_repo";
                }
                if (Directory.Exists(localVersionRepo) && File.Exists(Path.Combine(localVersionRepo, @"versions\Civilization\Civilization.json")))
                {
                    try
                    {
                        var psiBr = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD");
                        psiBr.WorkingDirectory = localVersionRepo;
                        psiBr.CreateNoWindow = true;
                        psiBr.UseShellExecute = false;
                        psiBr.RedirectStandardOutput = true;
                        string curBr = "";
                        using (var pbr = Process.Start(psiBr))
                        {
                            curBr = pbr.StandardOutput.ReadToEnd().Trim();
                            pbr.WaitForExit(2000);
                        }
                        if (curBr == "26.3")
                        {
                            var psiLocal = new ProcessStartInfo("git", "rev-parse HEAD");
                            psiLocal.WorkingDirectory = localVersionRepo;
                            psiLocal.CreateNoWindow = true;
                            psiLocal.UseShellExecute = false;
                            psiLocal.RedirectStandardOutput = true;
                            using (var pl = Process.Start(psiLocal))
                            {
                                string outLocal = pl.StandardOutput.ReadToEnd().Trim();
                                pl.WaitForExit(3000);
                                if (!string.IsNullOrEmpty(outLocal) && outLocal.Length >= 20)
                                {
                                    lock (updateCheckLock)
                                    {
                                        cachedRemoteShaByVer[ver] = outLocal;
                                        lastRemoteCheckByVer[ver] = DateTime.Now;
                                    }
                                    return outLocal;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            try
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
                catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/civilkaserver-sketch/Civilka-version/commits/" + branch);
                req.UserAgent = "CivilizationLauncher";
                req.Timeout = 6000;
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
                            cachedRemoteShaByVer[ver] = sha;
                            lastRemoteCheckByVer[ver] = DateTime.Now;
                        }
                        return sha;
                    }
                }
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo("git", "ls-remote https://github.com/civilkaserver-sketch/Civilka-version.git refs/heads/" + branch);
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
                                cachedRemoteShaByVer[ver] = sha;
                                lastRemoteCheckByVer[ver] = DateTime.Now;
                            }
                            return sha;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static void FetchRemoteShaInBackground(string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            lock (updateCheckLock)
            {
                if (checkingUpdateByVer.Contains(ver)) return;
                checkingUpdateByVer.Add(ver);
            }
            try
            {
                string sha = FetchRemoteShaSynchronous(ver);
                if (!string.IsNullOrEmpty(sha))
                {
                    lock (updateCheckLock)
                    {
                        cachedRemoteShaByVer[ver] = sha;
                        lastRemoteCheckByVer[ver] = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("FetchRemoteShaInBackground error (" + ver + "): " + ex.Message);
            }
            finally
            {
                lock (updateCheckLock) { checkingUpdateByVer.Remove(ver); }
            }
        }

        private static string CurrentLauncherSha = "09f6297297d150011d4365df39cefabf3d624aa0";
        public static string launcherUpdateState = "idle"; // "idle", "downloading", "restarting", "error"
        public static int launcherUpdatePct = 0;
        public static string launcherUpdateMsg = "";

        private static string cachedLauncherRemoteSha = null;

        private static DateTime lastLauncherCheck = DateTime.MinValue;

        private static bool isCheckingLauncherUpdate = false;

        private static object launcherCheckLock = new object();



        static bool IsDevWorkspace()

        {

            try

            {

                string dir = appDir ?? AppDomain.CurrentDomain.BaseDirectory;

                return Directory.Exists(Path.Combine(dir, ".git"));

            }

            catch { return false; }

        }



        static string GetCurrentLauncherSha()

        {

            try

            {

                string p = Path.Combine(GetLauncherDataDir(), "version.sha");

                if (File.Exists(p))

                {

                    string s = File.ReadAllText(p).Trim();

                    if (!string.IsNullOrEmpty(s)) return s;

                }

                string legacy = Path.Combine(appDir ?? AppDomain.CurrentDomain.BaseDirectory, "version.sha");

                if (File.Exists(legacy))

                {

                    string s = File.ReadAllText(legacy).Trim();

                    if (!string.IsNullOrEmpty(s)) return s;

                }

            }

            catch { }

            return CurrentLauncherSha;

        }



        static string GetCachedOrFetchLauncherRemoteSha()

        {

            if (IsDevWorkspace()) return GetCurrentLauncherSha();

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

                // public repository, no token needed

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

                var psi = new ProcessStartInfo("git", "ls-remote https://github.com/civilkaserver-sketch/CV-LAUNCHER.git refs/heads/main");

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

            if (IsDevWorkspace()) return;

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



        static bool DownloadFileFromRepo(string repoFileName, string destinationPath, Action<int, string> onProgress = null)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    string url = "https://api.github.com/repos/civilkaserver-sketch/CV-LAUNCHER/contents/" + Uri.EscapeDataString(repoFileName) + "?ref=main";
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "CV-Launcher";
                    req.Timeout = 120000;
                    req.ReadWriteTimeout = 300000;
                    // public repository, no token needed
                    req.Accept = "application/vnd.github.v3.raw";

                    using (var resp = req.GetResponse())
                    {
                        long totalBytes = resp.ContentLength;
                        using (var stream = resp.GetResponseStream())
                        using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[65536];
                            long totalRead = 0;
                            int read;
                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                fs.Write(buffer, 0, read);
                                totalRead += read;
                                if (totalBytes > 0 && onProgress != null)
                                {
                                    int pct = (int)((totalRead * 100) / totalBytes);
                                    string msg = string.Format("{0:F1} / {1:F1} МБ", totalRead / (1024.0 * 1024.0), totalBytes / (1024.0 * 1024.0));
                                    onProgress(pct, msg);
                                }
                            }
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
                    Log(string.Format("DownloadFileFromRepo ({0}) попытка {1}/3 ошибка: {2}", repoFileName, attempt, ex.Message));
                    if (attempt < 3) Thread.Sleep(2000);
                }
            }
            return false;
        }

        static void ExecuteLauncherUpdate(string newExePath, string newUiPath, string remoteSha)
        {
            try
            {
                if (IsDevWorkspace())
                {
                    Log("ExecuteLauncherUpdate aborted: .git directory detected. Local files will not be replaced.");
                    return;
                }

                launcherUpdateState = "restarting";
                launcherUpdatePct = 100;
                launcherUpdateMsg = "Перезапуск лаунчера...";

                Process curProc = Process.GetCurrentProcess();
                int curPid = curProc.Id;
                string currentExe = curProc.MainModule.FileName;
                string currentExeName = Path.GetFileName(currentExe);
                string currentDir = Path.GetDirectoryName(currentExe);
                string versionShaFile = Path.Combine(GetLauncherDataDir(), "version.sha");

                string tempDir = Path.GetTempPath();
                string batPath = Path.Combine(tempDir, "cv_launcher_updater.bat");
                string newShaFile = Path.Combine(tempDir, "version_new.sha");

                if (!string.IsNullOrEmpty(remoteSha))
                {
                    // version.sha пишем НЕ здесь: только после успешной подмены exe
                    // (это делает bat-апдейтер). Иначе при неудачной копии лаунчер
                    // считает себя обновлённым и больше не предложит апдейт.
                    try { File.WriteAllText(newShaFile, remoteSha); } catch { }
                }

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 > nul");
                sb.AppendLine("timeout /t 1 /nobreak > nul");
                sb.AppendLine(string.Format("taskkill /f /pid {0} > nul 2>&1", curPid));
                sb.AppendLine(string.Format("taskkill /f /im \"{0}\" > nul 2>&1", currentExeName));
                sb.AppendLine("set /a attempts=0");
                sb.AppendLine(":retry_copy_exe");
                sb.AppendLine("set /a attempts+=1");
                sb.AppendLine(string.Format("copy /y \"{0}\" \"{1}\" > nul 2>&1", newExePath, currentExe));
                sb.AppendLine("if errorlevel 1 (");
                sb.AppendLine("    if %attempts% geq 20 goto copy_failed");
                sb.AppendLine("    timeout /t 1 /nobreak > nul");
                sb.AppendLine("    goto retry_copy_exe");
                sb.AppendLine(")");
                sb.AppendLine(string.Format("if exist \"{0}\" (", newShaFile));
                sb.AppendLine(string.Format("    copy /y \"{0}\" \"{1}\" > nul 2>&1", newShaFile, versionShaFile));
                sb.AppendLine(string.Format("    del /f /q \"{0}\" > nul 2>&1", newShaFile));
                sb.AppendLine(")");
                sb.AppendLine(string.Format("del /f /q \"{0}\" > nul 2>&1", newExePath));
                sb.AppendLine(string.Format("cd /d \"{0}\"", currentDir));
                sb.AppendLine(string.Format("start \"\" \"{0}\"", currentExe));
                sb.AppendLine("del \"%~f0\" & exit");
                sb.AppendLine(":copy_failed");
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
                    Thread.Sleep(500);
                    Log("Terminating process for updater restart...");
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                launcherUpdateState = "error";
                launcherUpdateMsg = ex.Message;
                Log("ExecuteLauncherUpdate error: " + ex.Message);
            }
        }



        static bool SaveState(string mcDir, List<string> files, string commitSha = null, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string targetFile = GetStateFileForVersion(ver);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                    var dict = LoadState(ver) ?? new Dictionary<string, object>();
                    dict["mcDir"] = mcDir;
                    dict["version"] = ver;
                    dict["installedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    dict["files"] = files;
                    if (!string.IsNullOrEmpty(commitSha)) dict["commitSha"] = commitSha;
                    string json = new JavaScriptSerializer().Serialize(dict);
                    string tmp = targetFile + ".tmp";
                    File.WriteAllText(tmp, json, Encoding.UTF8);
                    if (File.Exists(targetFile)) File.Replace(tmp, targetFile, null);
                    else File.Move(tmp, targetFile);
                    return true;
                }
                catch (Exception ex)
                {
                    Log(string.Format("SaveState attempt {0}/3 failed: {1}", attempt, ex.Message));
                    Thread.Sleep(200 * attempt);
                }
            }
            Log("SaveState FAILED: state.json was not written. Update state is now stale.");
            return false;
        }

        static void ClearState(string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string targetFile = GetStateFileForVersion(ver);
            try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { }
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



        private static string cachedSelectedVersion = null;

        static string NormalizeBuildVersion(string v)
        {
            if (!string.IsNullOrEmpty(v) && v.Trim() == "1.21.8") return "1.21.8";
            return "26.3";
        }

        static string GetSelectedBuildVersion()
        {
            if (!string.IsNullOrEmpty(cachedSelectedVersion)) return cachedSelectedVersion;
            try
            {
                string selPath = Path.Combine(StateDir, "selected_version.txt");
                if (File.Exists(selPath))
                {
                    cachedSelectedVersion = NormalizeBuildVersion(File.ReadAllText(selPath, Encoding.UTF8));
                    return cachedSelectedVersion;
                }
            }
            catch { }
            cachedSelectedVersion = "26.3";
            return cachedSelectedVersion;
        }

        static void SetSelectedBuildVersion(string ver)
        {
            string norm = NormalizeBuildVersion(ver);
            cachedSelectedVersion = norm;
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                File.WriteAllText(Path.Combine(StateDir, "selected_version.txt"), norm, Encoding.UTF8);
            }
            catch { }
        }

        static string GetRepoBranchForVersion(string ver)
        {
            return NormalizeBuildVersion(ver) == "1.21.8" ? "main" : "26.3";
        }

        static string GetRepoZipUrlForVersion(string ver)
        {
            return "https://github.com/civilkaserver-sketch/Civilka-version/archive/refs/heads/" + GetRepoBranchForVersion(ver) + ".zip";
        }

        static string GetStateFileForVersion(string ver)
        {
            return NormalizeBuildVersion(ver) == "1.21.8" ? Path.Combine(StateDir, "state_1.21.8.json") : StateFile;
        }

        static string GetCacheDirForVersion(string ver)
        {
            return NormalizeBuildVersion(ver) == "1.21.8" ? Path.Combine(Path.GetTempPath(), "Civilka-version-main") : CacheDir;
        }

        static string GetExtractDirForVersion(string ver)
        {
            return NormalizeBuildVersion(ver) == "1.21.8" ? Path.Combine(Path.GetTempPath(), "civilka_extracted_main") : ExtractDir;
        }

        static bool IsJsonVersion1218(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath)) return false;
                string txt = File.ReadAllText(jsonPath, Encoding.UTF8);
                return txt.IndexOf("intermediary:26.3", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { return false; }
        }

        static string ResolveCivDirForVersion(string mcDir, string ver, bool forInstall = false)
        {
            string norm = NormalizeBuildVersion(ver);
            string primary = Path.Combine(mcDir, @"versions\Civilization");
            if (norm == "26.3") return primary;

            string sep = Path.Combine(mcDir, @"versions\Civilization-1.21.8");
            if (File.Exists(Path.Combine(sep, "Civilization.json"))) return sep;
            if (!forInstall)
            {
                string primaryJson = Path.Combine(primary, "Civilization.json");
                if (File.Exists(primaryJson) && IsJsonVersion1218(primaryJson)) return primary;
            }
            return sep;
        }

        static string GetCivRelativeSubdir(string mcDir, string civDir)
        {
            if (!string.IsNullOrEmpty(civDir) && civDir.EndsWith("Civilization-1.21.8", StringComparison.OrdinalIgnoreCase))
            {
                return @"versions\Civilization-1.21.8";
            }
            return @"versions\Civilization";
        }

        static string MapPackRelPath(string rel, string targetCivSubdir)
        {
            string norm = (rel ?? "").Replace('/', '\\');
            string prefix = @"versions\Civilization\";
            if (!string.IsNullOrEmpty(targetCivSubdir) &&
                !targetCivSubdir.Equals(@"versions\Civilization", StringComparison.OrdinalIgnoreCase) &&
                norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return targetCivSubdir + "\\" + norm.Substring(prefix.Length);
            }
            return norm;
        }

        static bool TestIsVersionInstalledForSelected(string mcDir)
        {
            bool dummy;
            return TestIsVersionInstalled(mcDir, GetSelectedBuildVersion(), out dummy);
        }

        static bool TestIsVersionInstalled(string mcDir, string ver, out bool legacyReinstallNeeded)
        {
            legacyReinstallNeeded = false;
            if (string.IsNullOrEmpty(mcDir)) return false;
            string norm = NormalizeBuildVersion(ver);

            if (norm == "1.21.8")
            {
                string sepJson = Path.Combine(mcDir, @"versions\Civilization-1.21.8\Civilization.json");
                if (File.Exists(sepJson)) return true;
                string primaryJson = Path.Combine(mcDir, @"versions\Civilization\Civilization.json");
                if (File.Exists(primaryJson) && IsJsonVersion1218(primaryJson)) return true;
                return false;
            }

            string jsonPath = Path.Combine(mcDir, @"versions\Civilization\Civilization.json");
            if (File.Exists(jsonPath))
            {
                if (!IsJsonVersion1218(jsonPath)) return true;
                legacyReinstallNeeded = true;
                return false;
            }
            try
            {
                string modsDir = Path.Combine(mcDir, @"versions\Civilization\mods");
                if (Directory.Exists(modsDir) && Directory.GetFiles(modsDir, "*.jar").Length > 0)
                {
                    legacyReinstallNeeded = true;
                    return false;
                }
            }
            catch { }
            return false;
        }

        static bool IsLegacyVersionInstalled(string mcDir)
        {
            if (string.IsNullOrEmpty(mcDir)) return false;
            try
            {
                string jsonPath = Path.Combine(mcDir, @"versions\Civilization\Civilization.json");
                if (File.Exists(jsonPath)) return IsJsonVersion1218(jsonPath);
                string modsDir = Path.Combine(mcDir, @"versions\Civilization\mods");
                if (Directory.Exists(modsDir) && Directory.GetFiles(modsDir, "*.jar").Length > 0) return true;
            }
            catch { }
            return false;
        }

        public static List<string> GetAllPotentialMcDirs()

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



        public static string GetActiveMcDir()

        {

            try

            {

                var state = LoadState();

                if (state != null && state.ContainsKey("mcDir"))

                {

                    string custom = (string)state["mcDir"];

                    if (!string.IsNullOrEmpty(custom))

                    {

                        custom = custom.Trim().TrimEnd('\\', '/');

                        if (custom.Length > 0) return custom;

                    }

                }

            }

            catch { }



            string inst = GetInstalledMcDir();

            if (!string.IsNullOrEmpty(inst)) return inst;



            return GetDetectedMcDir();

        }



        static string GetTargetMcDir()

        {

            return GetActiveMcDir();

        }



        public class JavaReqInfo

        {

            public string Component = "java-runtime-delta";

            public int Major = 21;

        }



        static JavaReqInfo GetRequiredJavaInfo(string mcDir, string sourceDir = null, string explicitCivDir = null)
        {
            var def = new JavaReqInfo();
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitCivDir)) candidates.Add(Path.Combine(explicitCivDir, "Civilization.json"));
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

                string srcExe = Path.Combine(srcHome, @"bin\javaw.exe");

                if (!File.Exists(srcExe)) srcExe = Path.Combine(srcHome, @"bin\java.exe");

                if (!File.Exists(srcExe)) return false;

                if (srcHome.TrimEnd('\\').Equals(runtimeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))

                {

                    return GetJavaMajor(srcExe) >= need;

                }



                string winDir = Path.GetDirectoryName(runtimeDir);

                if (!Directory.Exists(winDir)) Directory.CreateDirectory(winDir);

                if (Directory.Exists(runtimeDir)) ForceDeleteDirectory(runtimeDir);

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



                string testExe = Path.Combine(runtimeDir, @"bin\javaw.exe");

                if (!File.Exists(testExe)) testExe = Path.Combine(runtimeDir, @"bin\java.exe");

                int mActual = GetJavaMajor(testExe);

                return mActual >= need;

            }

            catch { return false; }

        }



        static string DownloadJava(int major)

        {

            string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";

            string url = string.Format("https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/{1}/jre/hotspot/normal/eclipse?project=jdk", major, arch);

            string zip = Path.Combine(Path.GetTempPath(), string.Format("temurin-jre-{0}.zip", major));

            string ext = Path.Combine(Path.GetTempPath(), string.Format("temurin-jre-{0}", major));



            if (File.Exists(zip)) try { File.Delete(zip); } catch { }

            ForceDeleteDirectory(ext);



            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            long estTotal = 49L * 1024 * 1024;

            DownloadFileCancellable(url, zip, (rec, tot) =>

            {

                long target = (tot > 0) ? tot : estTotal;

                int pct = Math.Max(0, Math.Min(99, (int)((rec * 100) / target)));

                double mbRec = rec / (1024.0 * 1024.0);

                double mbTot = target / (1024.0 * 1024.0);

                SetProgress(90 + (int)(pct * 0.08), string.Format("Загрузка Java {0}: {1:F1} / {2:F1} MB ({3}%)...", major, mbRec, mbTot, pct), true, false, null);

            });



            if (!File.Exists(zip) || new FileInfo(zip).Length < 1024 * 1024)

            {

                throw new Exception("Не удалось скачать архив Java " + major);

            }



            ZipFile.ExtractToDirectory(zip, ext);

            try { File.Delete(zip); } catch { }



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



        static bool EnsureJava(string mcDir, string sourceDir, string explicitCivDir = null)
        {
            var info = GetRequiredJavaInfo(mcDir, sourceDir, explicitCivDir);
            SetProgress(88, string.Format("Проверка Java {0}...", info.Major), true, false, null);
            int need = info.Major;
            string comp = info.Component;

            string runtimeDir = Path.Combine(mcDir, string.Format(@"runtime\{0}\windows\{0}", comp));

            string runtimeJava = Path.Combine(runtimeDir, @"bin\javaw.exe");

            if (!File.Exists(runtimeJava)) runtimeJava = Path.Combine(runtimeDir, @"bin\java.exe");

            int major = GetJavaMajor(runtimeJava);

            Log(string.Format("EnsureJava: required major={0} ({1}). Current runtime Java: '{2}' (detected major: {3})", need, comp, runtimeJava, major));

            if (major >= need) return true;



            Log("Searching installed Java installations on system...");

            foreach (string jh in FindJavaHomes(mcDir))

            {

                string jExe = Path.Combine(jh, @"bin\javaw.exe");

                if (!File.Exists(jExe)) jExe = Path.Combine(jh, @"bin\java.exe");

                int m = GetJavaMajor(jExe);

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

                try { ForceDeleteDirectory(downloaded); } catch { }

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



        static void EnsureAssets(string mcDir, Dictionary<string, object> root = null)

        {

            try

            {

                string assetIndex = "34";

                string assetIndexUrl = "https://piston-meta.mojang.com/v1/packages/331e5c80937c9cd79647ab34bdc1cbb5ef09f456/34.json";



                if (root == null)

                {

                    string civDir = Path.Combine(mcDir, @"versions\Civilization");

                    string jsonPath = Path.Combine(civDir, "Civilization.json");

                    if (File.Exists(jsonPath))

                    {

                        try

                        {

                            string jsonTxt = File.ReadAllText(jsonPath, Encoding.UTF8);

                            root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(jsonTxt);

                        }

                        catch { }

                    }

                }



                if (root != null)

                {

                    if (root.ContainsKey("assets") && root["assets"] != null)

                    {

                        string ai = root["assets"].ToString().Trim();

                        if (!string.IsNullOrEmpty(ai)) assetIndex = ai;

                    }

                    if (root.ContainsKey("assetIndex") && root["assetIndex"] is Dictionary<string, object>)

                    {

                        var aiDict = (Dictionary<string, object>)root["assetIndex"];

                        if (aiDict.ContainsKey("id") && aiDict["id"] != null)

                        {

                            string ai = aiDict["id"].ToString().Trim();

                            if (!string.IsNullOrEmpty(ai)) assetIndex = ai;

                        }

                        if (aiDict.ContainsKey("url") && aiDict["url"] != null)

                        {

                            string url = aiDict["url"].ToString().Trim();

                            if (!string.IsNullOrEmpty(url)) assetIndexUrl = url;

                        }

                    }

                }



                string assetsDir = Path.Combine(mcDir, "assets");

                string indexFilePath = Path.Combine(assetsDir, "indexes", assetIndex + ".json");

                string indexDir = Path.GetDirectoryName(indexFilePath);



                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;



                if (!File.Exists(indexFilePath))

                {

                    Log(string.Format("[Assets] Asset index missing. Downloading {0}.json...", assetIndex));

                    if (!Directory.Exists(indexDir)) Directory.CreateDirectory(indexDir);

                    using (var wc = new WebClient())

                    {

                        wc.Headers.Add("User-Agent", "CV-Launcher");

                        wc.DownloadFile(assetIndexUrl, indexFilePath);

                    }

                    Log("[Assets] Asset index downloaded successfully.");

                }



                var targetLangFiles = new string[] {
                    "pack.mcmeta",
                    "minecraft/lang/ru_ru.json",
                    "minecraft/lang/uk_ua.json",
                    "minecraft/lang/be_by.json",
                    "minecraft/lang/kk_kz.json"
                };

                var langHashes = new List<string>();

                if (File.Exists(indexFilePath))
                {
                    try
                    {
                        string indexJson = File.ReadAllText(indexFilePath, Encoding.UTF8);
                        var indexRoot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(indexJson);
                        if (indexRoot != null && indexRoot.ContainsKey("objects") && indexRoot["objects"] is Dictionary<string, object>)
                        {
                            var objs = (Dictionary<string, object>)indexRoot["objects"];
                            foreach (var langKey in targetLangFiles)
                            {
                                if (objs.ContainsKey(langKey) && objs[langKey] is Dictionary<string, object>)
                                {
                                    var lObj = (Dictionary<string, object>)objs[langKey];
                                    if (lObj.ContainsKey("hash") && lObj["hash"] != null)
                                    {
                                        string h = lObj["hash"].ToString().Trim();
                                        if (!string.IsNullOrEmpty(h) && !langHashes.Contains(h)) langHashes.Add(h);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("[Assets] Warning parsing index json: {0}", ex.Message));
                    }
                }

                // Ensure critical MC 1.21.8 language definition assets are always present
                if (!langHashes.Contains("6cb57c8bf7f43ecb75339be01e23b5be0c83f0c9"))
                {
                    langHashes.Add("6cb57c8bf7f43ecb75339be01e23b5be0c83f0c9"); // pack.mcmeta LanguageMetadataSection
                }
                if (!langHashes.Contains("e5572ca1507224dc231272b15dbf45851d36d9af"))
                {
                    langHashes.Add("e5572ca1507224dc231272b15dbf45851d36d9af"); // ru_ru.json
                }
                if (!langHashes.Contains("e3c58e2ec87d68c8671ec6be4b30e261f7b1fe9e"))
                {
                    langHashes.Add("e3c58e2ec87d68c8671ec6be4b30e261f7b1fe9e"); // uk_ua.json
                }



                foreach (string hash in langHashes)

                {

                    if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                    string sub = hash.Substring(0, 2);

                    string objPath = Path.Combine(assetsDir, "objects", sub, hash);

                    if (!File.Exists(objPath))

                    {

                        string objDir = Path.GetDirectoryName(objPath);

                        if (!Directory.Exists(objDir)) Directory.CreateDirectory(objDir);

                        string objUrl = string.Format("https://resources.download.minecraft.net/{0}/{1}", sub, hash);

                        try

                        {

                            Log(string.Format("[Assets] Downloading language asset ({0})...", hash));

                            using (var wc = new WebClient())

                            {

                                wc.Headers.Add("User-Agent", "CV-Launcher");

                                wc.DownloadFile(objUrl, objPath);

                            }

                            Log(string.Format("[Assets] Language asset {0} downloaded successfully.", hash));

                        }

                        catch (Exception ex)

                        {

                            Log(string.Format("[Assets] Warning: Failed to download language asset {0}: {1}", hash, ex.Message));

                        }

                    }

                }



                try
                {
                    string[] checkPaths = new string[] {
                        Path.Combine(mcDir, @"versions\Civilization\options.txt"),
                        Path.Combine(mcDir, "options.txt")
                    };
                    foreach (string optPath in checkPaths)
                    {
                        if (File.Exists(optPath))
                        {
                            string opt = File.ReadAllText(optPath, Encoding.UTF8);
                            bool changed = false;
                            if (opt.Contains("lang:en_us"))
                            {
                                opt = opt.Replace("lang:en_us", "lang:ru_ru");
                                changed = true;
                            }
                            if (opt.Contains("Philips") || opt.Contains("TAT2139"))
                            {
                                opt = Regex.Replace(opt, @"soundDevice:""[^""]*""", @"soundDevice:""""");
                                changed = true;
                            }
                            if (changed)
                            {
                                File.WriteAllText(optPath, opt, Encoding.UTF8);
                            }
                        }
                    }
                }
                catch { }

            }

            catch (Exception ex)

            {

                Log(string.Format("[Assets] Error in EnsureAssets: {0}", ex.Message));

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



        static bool IsProtectedPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            string norm = rel.Replace('/', '\\').Trim('\\');
            string fileName = Path.GetFileName(norm);

            // User configuration and state files that should NEVER be overwritten or deleted
            if (fileName.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("optionsshaders.txt", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("servers.dat", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("usercache.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("profile.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("xaerominimap", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("xaeroworldmap", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Strip version directory prefix if present
            int idx = norm.IndexOf(@"versions\Civilization\", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) norm = norm.Substring(idx + @"versions\Civilization\".Length);

            // Entire user directories that must be protected
            if (norm.StartsWith("config\\", StringComparison.OrdinalIgnoreCase) || norm.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("shaderpacks\\", StringComparison.OrdinalIgnoreCase) || norm.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("screenshots\\", StringComparison.OrdinalIgnoreCase) || norm.Equals("screenshots", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("saves\\", StringComparison.OrdinalIgnoreCase) || norm.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith("xaero\\", StringComparison.OrdinalIgnoreCase) || norm.Equals("xaero", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        static bool IsOfficialMod(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string name = fileName.ToLowerInvariant();
            string[] officialPrefixes = new string[] {
                "c2me", "cloth-config", "entityculling", "fabric-api", "fallingtree",
                "ferritecore", "fusion", "immediatelyfast", "iris", "krypton", "lithium",
                "modmenu", "ok_zoomer", "placeholder-api", "playertrading",
                "sodium", "voxy", "xaerominimap", "xaeroworldmap"
            };
            foreach (var p in officialPrefixes)
            {
                if (name.StartsWith(p)) return true;
            }
            return false;
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
                string rel = item.Substring(src.Length).TrimStart('\\', '/');
                string target = Path.Combine(dst, rel);
                string td = Path.GetDirectoryName(target);
                if (!Directory.Exists(td)) Directory.CreateDirectory(td);

                bool isProtected = IsProtectedPath(rel);

                if (File.Exists(target) && isProtected)
                {
                    files.Add(Path.Combine(prefix, rel));
                    continue;
                }

                File.Copy(item, target, true);
                files.Add(Path.Combine(prefix, rel));
            }
        }

        static List<string> InstallFiles(string sourceDir, string mcDir, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string targetCivDir = ResolveCivDirForVersion(mcDir, ver, true);
            string targetCivRel = GetCivRelativeSubdir(mcDir, targetCivDir);

            var files = new List<string>();
            SetProgress(60, "Копирование библиотек Fabric...", true, false, null);
            CopyTracked(Path.Combine(sourceDir, "libraries"), Path.Combine(mcDir, "libraries"), "libraries", files);
            if (Directory.Exists(Path.Combine(sourceDir, "assets")))
            {
                CopyTracked(Path.Combine(sourceDir, "assets"), Path.Combine(mcDir, "assets"), "assets", files);
            }

            try
            {
                string targetMods = Path.Combine(targetCivDir, "mods");
                string sourceMods = Path.Combine(sourceDir, @"versions\Civilization\mods");
                if (Directory.Exists(targetMods) && Directory.Exists(sourceMods))
                {
                    var newModFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mf in Directory.GetFiles(sourceMods, "*.jar"))
                    {
                        newModFiles.Add(Path.GetFileName(mf));
                    }

                    var prevPackMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string prevModsPrefix1 = @"versions\Civilization\mods\";
                    string prevModsPrefix2 = targetCivRel + @"\mods\";
                    foreach (string tracked in GetTrackedFilesList(LoadState(ver)))
                    {
                        string norm = tracked.Replace('/', '\\');
                        if ((norm.StartsWith(prevModsPrefix1, StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith(prevModsPrefix2, StringComparison.OrdinalIgnoreCase)) &&
                            norm.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        {
                            prevPackMods.Add(Path.GetFileName(norm));
                        }
                    }

                    foreach (var existingMod in Directory.GetFiles(targetMods, "*.jar"))
                    {
                        string fn = Path.GetFileName(existingMod);
                        if (newModFiles.Contains(fn)) continue;

                        bool wasInstalledByPack = prevPackMods.Count > 0
                            ? prevPackMods.Contains(fn)
                            : IsOfficialMod(fn);

                        if (!wasInstalledByPack) continue;

                        try
                        {
                            File.Delete(existingMod);
                            Log("Deleted obsolete pack mod: " + fn);
                        }
                        catch (Exception ex)
                        {
                            Log("Could not delete obsolete mod " + fn + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error during cleaning obsolete mods: " + ex.Message);
            }

            SetProgress(75, "Копирование модов, ресурспаков и конфигов...", true, false, null);
            CopyTracked(Path.Combine(sourceDir, @"versions\Civilization"), targetCivDir, targetCivRel, files);

            return files;
        }

        static void RemoveStale(string mcDir, List<string> oldFiles, List<string> newFiles, string explicitCivDir = null)
        {
            if (oldFiles == null || oldFiles.Count == 0) return;
            var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string o in oldFiles)
            {
                if (IsProtectedPath(o)) continue;
                if (!newSet.Contains(o))
                {
                    string p = Path.Combine(mcDir, o);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); } catch { }
                    }
                }
            }

            string civDir = !string.IsNullOrEmpty(explicitCivDir) ? explicitCivDir : Path.Combine(mcDir, @"versions\Civilization");
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



        static void ForceDeleteDirectory(string path)

        {

            if (!Directory.Exists(path)) return;

            try

            {

                var di = new DirectoryInfo(path);

                foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))

                {

                    try { fi.Attributes = FileAttributes.Normal; } catch { }

                }

                di.Delete(true);

            }

            catch (Exception ex)

            {

                Log("ForceDeleteDirectory error: " + ex.Message);

            }

        }



        static string DownloadBuild(bool isUpdate = false, string pinnedSha = null, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string branch = GetRepoBranchForVersion(ver);
            string cacheDir = GetCacheDirForVersion(ver);
            string extractDir = GetExtractDirForVersion(ver);

            Log(string.Format("DownloadBuild started (isUpdate={0}, ver={1})", isUpdate, ver));

            if (ver == "26.3")
            {
                string localVersionRepo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\version_repo");
                if (!Directory.Exists(localVersionRepo) && IsDevEnvironment)
                {
                    localVersionRepo = @"E:\Civilization DEV\version_repo";
                }
                if (Directory.Exists(localVersionRepo) && File.Exists(Path.Combine(localVersionRepo, @"versions\Civilization\Civilization.json")))
                {
                    Log("Using local version_repo: " + localVersionRepo);
                    SetProgress(50, "Использование локального репозитория сборки...", true, false, null);
                    return localVersionRepo;
                }
            }

            string labelPrep = isUpdate ? "Подготовка к загрузке обновления..." : "Подготовка к загрузке сборки...";

            int initialPct = isUpdate ? 25 : 5;

            SetProgress(initialPct, labelPrep, true, false, null);

            CheckCancelled();



            // 1. Быстрое обновление через Git, если репозиторий уже есть в cacheDir

            if (Directory.Exists(Path.Combine(cacheDir, ".git")))

            {

                try

                {

                    Log("Existing git repository found in cacheDir. Performing fast update...");

                    SetProgress(isUpdate ? 30 : 15, "Быстрое обновление через Git...", true, false, null);

                    var psiFetch = new ProcessStartInfo("git", "fetch --depth 1 origin " + branch);

                    psiFetch.WorkingDirectory = cacheDir;

                    psiFetch.CreateNoWindow = true;

                    psiFetch.UseShellExecute = false;

                    using (var pf = Process.Start(psiFetch))

                    {

                        int w = 0;

                        while (w < 45000)

                        {

                            if (pf.WaitForExit(200)) break;

                            w += 200;

                            if (cancelRequested) { try { pf.Kill(); } catch { } throw new OperationCancelled(); }

                        }

                        if (pf.HasExited && pf.ExitCode == 0)

                        {

                            var psiReset = new ProcessStartInfo("git", "reset --hard origin/" + branch);

                            psiReset.WorkingDirectory = cacheDir;

                            psiReset.CreateNoWindow = true;

                            psiReset.UseShellExecute = false;

                            using (var pr = Process.Start(psiReset))

                            {

                                pr.WaitForExit(10000);

                                if (pr.ExitCode == 0 && File.Exists(Path.Combine(cacheDir, @"versions\Civilization\Civilization.json")))

                                {

                                    Log("Fast git update succeeded in " + cacheDir);

                                    return cacheDir;

                                }

                            }

                        }

                    }

                }

                catch (OperationCancelled) { throw; }

                catch (Exception ex)

                {

                    Log("Fast git update skipped: " + ex.Message);

                }

            }



            // 2. Пробуем полный git clone, если репозитория еще нет

            try

            {

                Log("Attempting git clone from https://github.com/civilkaserver-sketch/Civilka-version.git...");

                ForceDeleteDirectory(cacheDir);

                var psi = new ProcessStartInfo("git", string.Format("clone --depth 1 -b {0} \"https://github.com/civilkaserver-sketch/Civilka-version.git\" \"{1}\"", branch, cacheDir));

                psi.CreateNoWindow = true;

                psi.UseShellExecute = false;

                using (var p = Process.Start(psi))

                {

                    int waited = 0;

                    bool exited = false;

                    while (waited < 90000)

                    {

                        if (p.WaitForExit(200)) { exited = true; break; }

                        waited += 200;

                        if ((waited % 2000) == 0)

                        {

                            int pct = isUpdate ? (25 + Math.Min(45, (waited / 2000))) : (10 + Math.Min(40, (waited / 2000)));

                            SetProgress(pct, isUpdate ? "Клонирование обновления через Git..." : "Клонирование сборки через Git...", true, false, null);

                        }

                        if (cancelRequested)

                        {

                            try { p.Kill(); } catch { }

                            throw new OperationCancelled();

                        }

                    }

                    if (exited && p.ExitCode == 0 && File.Exists(Path.Combine(cacheDir, @"versions\Civilization\Civilization.json")))

                    {

                        Log("Git clone succeeded into " + cacheDir);

                        return cacheDir;

                    }

                    else

                    {

                        Log(string.Format("Git clone not usable (exited={0}, code={1})", exited, exited ? p.ExitCode : -1));

                        if (!exited) { try { p.Kill(); } catch { } }

                    }

                }

            }

            catch (OperationCancelled) { throw; }

            catch (Exception ex)

            {

                Log("Git clone skipped or failed: " + ex.Message);

            }



            // 3. Загрузка через zip с отображением мегабайтов и процентов в реальном времени

            CheckCancelled();

            string zipPath = Path.Combine(Path.GetTempPath(), "civilka_" + branch + ".zip");

            if (File.Exists(zipPath)) File.Delete(zipPath);

            ForceDeleteDirectory(extractDir);



            // Качаем ровно тот коммит, который потом запишем в state.json.
            // Иначе получается «state новее, а файлы старые» и бесконечные обновления.
            string zipUrl = string.IsNullOrEmpty(pinnedSha)
                ? GetRepoZipUrlForVersion(ver)
                : string.Format("https://github.com/civilkaserver-sketch/Civilka-version/archive/{0}.zip", pinnedSha);

            Log("Downloading zip archive from " + zipUrl + " to " + zipPath);

            const int zipAttempts = 3;
            bool zipOk = false;
            for (int attempt = 1; attempt <= zipAttempts && !zipOk; attempt++)
            {
                CheckCancelled();
                if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { } }
                try
                {
                    Log(string.Format("Zip download attempt {0}/{1}", attempt, zipAttempts));
                    DownloadFileCancellable(zipUrl, zipPath, (received, total) =>
                    {
                        double mbRec = received / (1024.0 * 1024.0);
                        string txt;
                        int pct;
                        if (total > 0)
                        {
                            double mbTot = total / (1024.0 * 1024.0);
                            pct = Math.Max(0, Math.Min(99, (int)((received * 100) / total)));
                            txt = string.Format("{0}{1:F1} / {2:F1} MB ({3}%)...", isUpdate ? "Загрузка обновления: " : "Загрузка сборки: ", mbRec, mbTot, pct);
                        }
                        else
                        {
                            // GitHub отдаёт архив chunked, без Content-Length. Не показываем
                            // выдуманный знаменатель — только реально скачанное.
                            pct = Math.Max(0, Math.Min(99, (int)(received * 100 / (300L * 1024 * 1024))));
                            txt = string.Format("{0}{1:F1} MB скачано...", isUpdate ? "Загрузка обновления: " : "Загрузка сборки: ", mbRec);
                        }
                        int p = isUpdate ? (25 + (int)(pct * 0.45)) : (10 + (int)(pct * 0.4));
                        SetProgress(p, txt, true, false, null);
                    });

                    if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 1024 * 1024) zipOk = true;
                    else Log("Zip archive is missing or too small after attempt " + attempt);
                }
                catch (OperationCancelled) { throw; }
                catch (Exception ex)
                {
                    Log(string.Format("Zip download attempt {0}/{1} failed: {2}", attempt, zipAttempts, ex.Message));
                    if (attempt < zipAttempts) Thread.Sleep(2000 * attempt);
                }
            }

            if (!zipOk)
            {
                Log("Zip archive could not be downloaded after all attempts");
                throw new TransientUpdateException("не удалось скачать архив сборки с GitHub. Проверьте интернет и повторите обновление.");
            }

            Log(string.Format("Zip archive downloaded successfully, size: {0} bytes", new FileInfo(zipPath).Length));

            CheckCancelled();

            SetProgress(isUpdate ? 72 : 52, isUpdate ? "Распаковка обновления..." : "Распаковка сборки...", true, false, null);

            Log("Extracting zip to: " + extractDir);

            ForceDeleteDirectory(extractDir);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (Exception zex)
            {
                ForceDeleteDirectory(extractDir);
                try { File.Delete(zipPath); } catch { }
                Log("Zip extraction failed: " + zex.ToString());
                throw new TransientUpdateException("архив сборки повреждён при загрузке. Повторите обновление.");
            }

            try { File.Delete(zipPath); } catch { }

            CheckCancelled();



            string unpacked = Path.Combine(extractDir, (branch == "main" ? "Civilka-version-main" : "Civilka-version-26.3"));

            if (!Directory.Exists(unpacked))

            {

                foreach (string d in Directory.GetDirectories(extractDir))

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

            DownloadFileCancellable(url, destPath, (rec, tot) =>

            {

                if (onPercent != null)

                {

                    int pct = (tot > 0) ? (int)((rec * 100) / tot) : 0;

                    onPercent(pct);

                }

            });

        }



        static void DownloadFileCancellable(string url, string destPath, Action<long, long> onProgress, string authHeader = null)

        {

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            using (var wc = new WebClient())

            using (var done = new ManualResetEvent(false))

            {

                try { wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"; } catch { }

                if (!string.IsNullOrEmpty(authHeader))

                {

                    try { wc.Headers[HttpRequestHeader.Authorization] = authHeader; } catch { }

                }

                Exception dlError = null;

                bool dlCancelled = false;

                long lastReportBytes = -1;

                long lastReportTime = 0;

                var sw = Stopwatch.StartNew();



                wc.DownloadProgressChanged += (s, e) =>

                {

                    long now = sw.ElapsedMilliseconds;

                    if (e.BytesReceived - lastReportBytes > 256 * 1024 || (now - lastReportTime > 250))

                    {

                        lastReportBytes = e.BytesReceived;

                        lastReportTime = now;

                        if (onProgress != null)

                        {

                            try { onProgress(e.BytesReceived, e.TotalBytesToReceive); } catch { }

                        }

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



        // Длина файла на сервере (HEAD). null — если узнать не удалось.
        static long? GetRemoteContentLength(string url, string authHeader)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                req.Timeout = 15000;
                req.AllowAutoRedirect = true;
                if (!string.IsNullOrEmpty(authHeader)) req.Headers[HttpRequestHeader.Authorization] = authHeader;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.ContentLength > 0) return resp.ContentLength;
                }
            }
            catch { }
            return null;
        }

        // Скачивание файла с докачкой (HTTP Range) и повторами.
        // Недокачанный файл остаётся на диске как .part, поэтому следующий повтор
        // продолжает с места обрыва, а не с нуля. Если всё же не вышло —
        // бросает TransientUpdateException (не повод качать весь билд заново).
        static void DownloadFileResumable(string url, string destPath, Action<long, long> onProgress, string authHeader, int attempts)

        {

            if (attempts < 1) attempts = 1;

            Exception lastError = null;

            for (int attempt = 1; attempt <= attempts; attempt++)

            {

                CheckCancelled();

                long existing = 0;

                try { if (File.Exists(destPath)) existing = new FileInfo(destPath).Length; } catch { existing = 0; }

                try

                {

                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                    var req = (HttpWebRequest)WebRequest.Create(url);

                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

                    req.Timeout = 30000;

                    req.ReadWriteTimeout = 60000;

                    req.AllowAutoRedirect = true;

                    if (!string.IsNullOrEmpty(authHeader)) req.Headers[HttpRequestHeader.Authorization] = authHeader;

                    if (existing > 0) req.AddRange(existing);

                    using (var resp = (HttpWebResponse)req.GetResponse())

                    {

                        long startOffset = existing;

                        long contentLen = resp.ContentLength;

                        // Сервер не умеет Range — начинаем файл заново.
                        if (startOffset > 0 && resp.StatusCode != HttpStatusCode.PartialContent)

                        {

                            startOffset = 0;

                        }

                        long total = contentLen > 0 ? contentLen + startOffset : -1;

                        long received = startOffset;

                        long lastReport = -1;

                        using (var stream = resp.GetResponseStream())

                        using (var fs = new FileStream(destPath, startOffset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))

                        {

                            byte[] buffer = new byte[131072];

                            int read;

                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)

                            {

                                fs.Write(buffer, 0, read);

                                received += read;

                                if (onProgress != null && received - lastReport >= 262144)

                                {

                                    lastReport = received;

                                    try { onProgress(received, total); } catch { }

                                }

                                CheckCancelled();

                            }

                            fs.Flush();

                        }

                    }

                    if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return;

                    lastError = new Exception("файл не сохранился на диск");

                }

                catch (OperationCancelled) { throw; }

                catch (WebException wex)

                {

                    var httpResp = wex.Response as HttpWebResponse;

                    if (httpResp != null && (int)httpResp.StatusCode == 416)

                    {

                        // 416 = файл .part уже не меньше, чем на сервере.
                        // Если размеры совпали — файл докачан целиком, повторно его не тянем.
                        long? remoteLen = GetRemoteContentLength(url, authHeader);

                        long localLen = 0;

                        try { if (File.Exists(destPath)) localLen = new FileInfo(destPath).Length; } catch { }

                        if (remoteLen.HasValue && remoteLen.Value > 0 && remoteLen.Value == localLen)

                        {

                            Log("Resumable download: " + Path.GetFileName(destPath) + " already complete (" + localLen + " bytes).");

                            return;

                        }

                        try { File.Delete(destPath); } catch { }

                    }

                    lastError = wex;

                    Log(string.Format("Resumable download attempt {0}/{1} failed ({2}): {3}", attempt, attempts, url, wex.Message));

                }

                catch (Exception ex)

                {

                    lastError = ex;

                    Log(string.Format("Resumable download attempt {0}/{1} failed ({2}): {3}", attempt, attempts, url, ex.Message));

                }

                if (attempt < attempts) Thread.Sleep(1000 * attempt);

            }

            throw new TransientUpdateException(lastError != null ? lastError.Message : "неизвестная ошибка загрузки");

        }



        static void DoInstall(string mcDir, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            string targetCivDir = ResolveCivDirForVersion(mcDir, ver, true);
            Log("=== Starting Installation (" + ver + "): mcDir=" + mcDir + ", civDir=" + targetCivDir + " ===");
            try
            {
                if (!Directory.Exists(mcDir)) Directory.CreateDirectory(mcDir);

                string installSha = GetCachedShaOnly(ver) ?? FetchRemoteShaSynchronous(ver);
                string sourceDir = DownloadBuild(false, installSha, ver);
                CheckCancelled();

                if (ver == "26.3" && IsLegacyVersionInstalled(mcDir))
                {
                    Log("Legacy 1.21.8 build detected before DoInstall - cleaning old mods and version json...");
                    try
                    {
                        string oldModsDir = Path.Combine(mcDir, @"versions\Civilization\mods");
                        string backupModsDir = Path.Combine(mcDir, @"versions\Civilization\mods_1.21.8_backup");
                        if (Directory.Exists(oldModsDir))
                        {
                            foreach (string jar in Directory.GetFiles(oldModsDir, "*.jar"))
                            {
                                string fn = Path.GetFileName(jar);
                                if (IsOfficialMod(fn))
                                {
                                    try { File.Delete(jar); Log("Removed legacy pack mod: " + fn); } catch { }
                                }
                                else
                                {
                                    try
                                    {
                                        if (!Directory.Exists(backupModsDir)) Directory.CreateDirectory(backupModsDir);
                                        string dst = Path.Combine(backupModsDir, fn);
                                        if (File.Exists(dst)) File.Delete(dst);
                                        File.Move(jar, dst);
                                        Log("Backed up legacy custom mod: " + fn);
                                    }
                                    catch { }
                                }
                            }
                        }
                        string oldCivJson = Path.Combine(mcDir, @"versions\Civilization\Civilization.json");
                        if (File.Exists(oldCivJson))
                        {
                            try { File.Delete(oldCivJson); } catch { }
                        }
                    }
                    catch (Exception lex)
                    {
                        Log("Legacy cleanup warning: " + lex.Message);
                    }
                }

                Log("Copying files from: " + sourceDir);

                var files = InstallFiles(sourceDir, mcDir, ver);
                Log(string.Format("Copied {0} files", files.Count));

                CheckCancelled();
                SetProgress(82, "Регистрация профиля Civilization...", true, false, null);
                RegisterProfile(mcDir, targetCivDir);
                string currentSha = installSha ?? (GetCachedShaOnly(ver) ?? FetchRemoteShaSynchronous(ver));
                if (!SaveState(mcDir, files, currentSha, ver))
                {
                    throw new TransientUpdateException("файлы установлены, но state.json не записался (антивирус/права/диск).");
                }
                Log("Profile registered and state saved (" + ver + ", SHA: " + currentSha + ").");

                CheckCancelled();
                EnsureJava(mcDir, sourceDir, targetCivDir);
                EnsureAssets(mcDir);
                try { AdServerBlocker.CleanAll(mcDir); AdServerBlocker.SetupWatchers(); } catch { }



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



        static List<string> GetTrackedFilesList(Dictionary<string, object> state)
        {
            var list = new List<string>();
            if (state != null && state.ContainsKey("files"))
            {
                var arr = state["files"] as ArrayList;
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        if (item != null) list.Add(item.ToString());
                    }
                }
            }
            return list;
        }

        static bool TryFastDiffUpdate(string mcDir, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? "26.3" : NormalizeBuildVersion(buildVer);
            string targetCivDir = ResolveCivDirForVersion(mcDir, ver, false);
            string targetCivSubdir = GetCivRelativeSubdir(mcDir, targetCivDir);
            try
            {
                var state = LoadState(ver);
                if (state == null || !state.ContainsKey("commitSha"))
                {
                    Log("FastDiffUpdate: no commitSha in state, fallback to full download.");
                    return false;
                }

                string localSha = (state["commitSha"] ?? "").ToString().Trim();
                if (string.IsNullOrEmpty(localSha))
                {
                    Log("FastDiffUpdate: localSha is empty, fallback to full download.");
                    return false;
                }

                string remoteSha = GetCachedShaOnly(ver) ?? FetchRemoteShaSynchronous(ver);
                if (string.IsNullOrEmpty(remoteSha))
                {
                    Log("FastDiffUpdate: could not fetch remoteSha, fallback to full download.");
                    return false;
                }

                if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    Log(string.Format("FastDiffUpdate: localSha == remoteSha ({0}), already up to date.", localSha));
                    SetProgress(100, "Сборка уже обновлена!", false, true, null);
                    return true;
                }

                Log(string.Format("FastDiffUpdate: comparing {0} -> {1}", localSha, remoteSha));
                SetProgress(15, "Проверка изменений обновления...", true, false, null);

                string compareUrl = string.Format("https://api.github.com/repos/civilkaserver-sketch/Civilka-version/compare/{0}...{1}", localSha, remoteSha);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(compareUrl);
                req.UserAgent = "CivilizationLauncher";
                req.Timeout = 15000;
                // public repository, no token needed

                string json = null;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    json = sr.ReadToEnd();
                }

                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (root == null || !root.ContainsKey("files"))
                {
                    Log("FastDiffUpdate: compare returned invalid payload, fallback to full download.");
                    return false;
                }

                var filesList = root["files"] as ArrayList;
                if (filesList == null)
                {
                    Log("FastDiffUpdate: files is null, fallback to full download.");
                    return false;
                }

                if (filesList.Count == 0)
                {
                    Log("FastDiffUpdate: 0 changed files reported by compare, saving new SHA.");
                    var curFiles = GetTrackedFilesList(state);
                    if (!SaveState(mcDir, curFiles, remoteSha, ver)) throw new TransientUpdateException("не удалось записать state.json");
                    RegisterProfile(mcDir, targetCivDir);
                    SetProgress(100, "Обновление успешно завершено!", false, true, null);
                    return true;
                }

                if (filesList.Count > 100)
                {
                    Log(string.Format("FastDiffUpdate: {0} files changed, exceeding diff limit (100). Fallback to full download.", filesList.Count));
                    return false;
                }

                Log(string.Format("FastDiffUpdate: found {0} changed file(s). Performing quick update...", filesList.Count));

                var trackedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (state.ContainsKey("files"))
                {
                    var arr = state["files"] as ArrayList;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            if (item != null) trackedFiles.Add(item.ToString());
                        }
                    }
                }

                var filesToDownload = new List<Dictionary<string, object>>();
                var filesToDelete = new List<string>();

                foreach (var item in filesList)
                {
                    var f = item as Dictionary<string, object>;
                    if (f == null || !f.ContainsKey("filename")) continue;

                    string fn = f["filename"].ToString().Trim().Replace('/', '\\');
                    string status = f.ContainsKey("status") ? f["status"].ToString() : "";

                    if (fn.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("install.bat", StringComparison.OrdinalIgnoreCase) ||
                        fn.Equals("update.bat", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsProtectedPath(fn))
                    {
                        Log("FastDiffUpdate: skipping protected file " + fn);
                        continue;
                    }

                    if (status == "removed")
                    {
                        filesToDelete.Add(fn);
                    }
                    else
                    {
                        if (status == "renamed" && f.ContainsKey("previous_filename"))
                        {
                            string prev = f["previous_filename"].ToString().Trim().Replace('/', '\\');
                            if (!IsProtectedPath(prev)) filesToDelete.Add(prev);
                        }
                        filesToDownload.Add(f);
                    }
                }

                foreach (var rawDelRel in filesToDelete)
                {
                    string delRel = MapPackRelPath(rawDelRel, targetCivSubdir);
                    try
                    {
                        string targetPath = Path.Combine(mcDir, delRel);
                        if (File.Exists(targetPath))
                        {
                            File.Delete(targetPath);
                            Log("FastDiffUpdate: removed obsolete file " + delRel);
                        }
                        // Не оставляем мусор: недокачанный .part удалённого из пака файла.
                        string stalePartial = targetPath + ".part";
                        if (File.Exists(stalePartial))
                        {
                            try { File.Delete(stalePartial); Log("FastDiffUpdate: removed stale partial " + delRel + ".part"); }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("FastDiffUpdate: could not delete " + delRel + ": " + ex.Message);
                    }
                    trackedFiles.Remove(delRel);
                }

                int totalToDl = filesToDownload.Count;
                var failedFiles = new List<string>();
                for (int i = 0; i < totalToDl; i++)
                {
                    CheckCancelled();
                    var f = filesToDownload[i];
                    string rawFn = f["filename"].ToString().Trim().Replace('/', '\\');
                    string fn = MapPackRelPath(rawFn, targetCivSubdir);
                    string targetPath = Path.Combine(mcDir, fn);
                    string targetDir = Path.GetDirectoryName(targetPath);
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    string downloadUrl = string.Format("https://raw.githubusercontent.com/civilkaserver-sketch/Civilka-version/{0}/{1}", remoteSha, rawFn.Replace('\\', '/'));
                    string fileNameOnly = Path.GetFileName(fn);

                    int basePct = 20 + (int)((double)i / (double)totalToDl * 65.0);
                    int nextPct = 20 + (int)((double)(i + 1) / (double)totalToDl * 65.0);

                    SetProgress(basePct, string.Format("Загрузка обновления: {0} ({1}/{2})...", fileNameOnly, i + 1, totalToDl), true, false, null);

                    // Файл мог быть успешно заменён в прошлой попытке (тогда упал другой файл).
                    // Если размер совпадает с серверным — не качаем 78 МБ заново.
                    long? remoteLen = GetRemoteContentLength(downloadUrl, null);
                    long localLen = 0;
                    try { if (File.Exists(targetPath)) localLen = new FileInfo(targetPath).Length; } catch { }
                    // Порог 4 МБ: у мелких текстовых файлов совпадение размера ничего не
                    // доказывает, их дешевле скачать заново. Экономим только на jar/zip.
                    if (remoteLen.HasValue && remoteLen.Value >= 4L * 1024 * 1024 && remoteLen.Value == localLen)
                    {
                        Log("FastDiffUpdate: " + fn + " is already up to date (size " + localLen + "), skipping download.");
                        trackedFiles.Add(fn);
                        continue;
                    }

                    // Постоянное имя .part: обрыв докачивается с того же места,
                    // в том числе при следующем запуске лаунчера.
                    string tempFile = targetPath + ".part";
                    try
                    {
                        DownloadFileResumable(downloadUrl, tempFile, (recv, total) =>
                        {
                            if (total <= 0) return;
                            double fileFraction = (double)recv / (double)total;
                            int currentPct = basePct + (int)(fileFraction * (nextPct - basePct));
                            double mbRecv = (double)recv / (1024.0 * 1024.0);
                            double mbTotal = (double)total / (1024.0 * 1024.0);
                            SetProgress(currentPct, string.Format("Загрузка {0}: {1:F1} / {2:F1} МБ ({3}/{4})", fileNameOnly, mbRecv, mbTotal, i + 1, totalToDl), true, false, null);
                        }, null, 4);

                        if (File.Exists(targetPath))
                        {
                            File.Delete(targetPath);
                        }
                        File.Move(tempFile, targetPath);
                        trackedFiles.Add(fn);
                        Log(string.Format("FastDiffUpdate: updated {0} ({1}/{2})", fn, i + 1, totalToDl));
                    }
                    catch (OperationCancelled) { throw; }
                    catch (TransientUpdateException ex)
                    {
                        // Один файл не скачался — остальные всё равно обновляем.
                        // .part остаётся на диске, повтор продолжит с места обрыва.
                        Log("FastDiffUpdate: file failed, will retry later: " + fn + " — " + ex.Message);
                        failedFiles.Add(fn);
                    }
                    catch (Exception ex)
                    {
                        bool fileLocked = (ex is IOException) || (ex is UnauthorizedAccessException);
                        Log("FastDiffUpdate: could not apply " + fn + ": " + ex.Message);
                        failedFiles.Add(fileLocked ? fn + " — файл занят (закройте Minecraft)" : fn);
                    }
                }

                if (failedFiles.Count > 0)
                {
                    // Не откатываемся в полную перезаливку 300 МБ из-за пары файлов.
                    throw new TransientUpdateException(string.Format(
                        "не удалось обновить {0} из {1} файл(ов): {2}. Нажмите «Обновить» ещё раз — загрузка продолжится с места обрыва.",
                        failedFiles.Count, totalToDl, failedFiles[0]));
                }

                CheckCancelled();

                SetProgress(90, "Обновление конфигурации профиля...", true, false, null);
                RegisterProfile(mcDir, targetCivDir);
                EnsureJava(mcDir, null, targetCivDir);
                EnsureAssets(mcDir);

                var finalFilesList = new List<string>(trackedFiles);
                if (!SaveState(mcDir, finalFilesList, remoteSha, ver))
                {
                    throw new TransientUpdateException("файлы обновлены, но не удалось записать state.json (антивирус/права/диск)");
                }

                SetProgress(100, "Обновление успешно завершено!", false, true, null);
                Log(string.Format("=== FastDiffUpdate finished successfully ({0} files updated, {1} deleted, SHA={2}) ===", filesToDownload.Count, filesToDelete.Count, remoteSha));
                return true;
            }
            catch (OperationCancelled)
            {
                throw;
            }
            catch (TransientUpdateException)
            {
                // Сетевая/дисковая проблема: полную перезаливку не запускаем.
                throw;
            }
            catch (Exception ex)
            {
                Log("FastDiffUpdate failed with exception, falling back to full download: " + ex.ToString());
                return false;
            }
        }

        static void DoUpdate(string mcDir, bool userInitiated = true, string buildVer = null)
        {
            if (Interlocked.CompareExchange(ref updateInProgress, 1, 0) != 0)
            {
                Log("DoUpdate skipped: another update is already in progress.");
                return;
            }
            try
            {
                DoUpdateCore(mcDir, userInitiated, buildVer);
            }
            finally
            {
                Interlocked.Exchange(ref updateInProgress, 0);
            }
        }

        static void DoUpdateCore(string mcDir, bool userInitiated, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            string targetCivDir = ResolveCivDirForVersion(mcDir, ver, false);
            Log("=== Starting Update (" + ver + "): mcDir=" + mcDir + ", civDir=" + targetCivDir + " (userInitiated=" + userInitiated + ") ===");
            try
            {
                if (IsGameRunning())

                {

                    Log("Update skipped: game is running.");

                    if (userInitiated)

                    {

                        const string lockMsg = "Minecraft запущен — файлы мода заняты игрой. Закройте игру и повторите обновление.";

                        SetProgress(0, lockMsg, false, false, lockMsg);

                    }

                    return;

                }



                SetProgress(10, "Проверяем наличие обновлений...", true, false, null);

                Thread.Sleep(500);

                CheckCancelled();



                if (!Directory.Exists(mcDir)) Directory.CreateDirectory(mcDir);



                try

                {

                    if (TryFastDiffUpdate(mcDir, ver)) return;
                }
                catch (TransientUpdateException tex)
                {
                    cancelRequested = false;
                    Log("Diff update aborted, full rebuild suppressed: " + tex.Message);
                    SetProgress(0, "Обновление прервано: " + tex.Message, false, false, tex.Message);
                    return;
                }

                SetProgress(20, "Обновление найдено, подготовка к загрузке...", true, false, null);
                string targetSha = GetCachedShaOnly(ver) ?? FetchRemoteShaSynchronous(ver);
                string sourceDir = DownloadBuild(true, targetSha, ver);
                CheckCancelled();

                SetProgress(75, "Распаковка и установка файлов...", true, false, null);
                var state = LoadState(ver);
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

                var files = InstallFiles(sourceDir, mcDir, ver);
                Log(string.Format("Copied {0} new files", files.Count));

                SetProgress(88, "Очистка устаревших файлов...", true, false, null);
                RemoveStale(mcDir, oldFiles, files, targetCivDir);

                SetProgress(94, "Обновление профиля игры...", true, false, null);
                RegisterProfile(mcDir, targetCivDir);
                string currentSha = targetSha ?? (GetCachedShaOnly(ver) ?? FetchRemoteShaSynchronous(ver));
                if (!SaveState(mcDir, files, currentSha, ver))
                {
                    throw new TransientUpdateException("файлы установлены, но state.json не записался (антивирус/права/диск). Повторите обновление.");
                }
                Log("Profile registered and state saved (" + ver + ", SHA: " + currentSha + ").");

                EnsureJava(mcDir, sourceDir, targetCivDir);
                EnsureAssets(mcDir);
                try { AdServerBlocker.CleanAll(mcDir); AdServerBlocker.SetupWatchers(); } catch { }

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



        static void DoUninstall(string mcDir, string buildVer = null)
        {
            string ver = string.IsNullOrEmpty(buildVer) ? GetSelectedBuildVersion() : NormalizeBuildVersion(buildVer);
            string targetCivDir = ResolveCivDirForVersion(mcDir, ver, false);
            Log("=== Starting Uninstall (" + ver + "): mcDir=" + mcDir + ", civDir=" + targetCivDir + " ===");
            try
            {
                SetProgress(10, "Удаление профиля Civilization...", true, false, null);
                if (ver == "26.3") UnregisterProfile(mcDir);

                var state = LoadState(ver);
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

                string otherVer = ver == "26.3" ? "1.21.8" : "26.3";
                bool otherLeg = false;
                bool otherInstalled = TestIsVersionInstalled(mcDir, otherVer, out otherLeg) || otherLeg;
                foreach (string rel in files)
                {
                    if (otherInstalled && (rel.StartsWith("libraries\\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("assets\\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))) continue;
                    string p = Path.Combine(mcDir, rel);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); delCount++; } catch { }
                    }
                }
                Log(string.Format("Deleted {0} of {1} tracked files", delCount, files.Count));

                string civDst = targetCivDir;
                if (Directory.Exists(civDst))
                {
                    try { Directory.Delete(civDst, true); Log("Deleted " + civDst); } catch (Exception ex) { Log("Failed to delete civDst: " + ex.Message); }
                }

                string libRoot = Path.Combine(mcDir, "libraries");

                if (Directory.Exists(libRoot))

                {

                    CleanEmptyDirs(libRoot);

                }



                ClearState(ver);
                string vCache = GetCacheDirForVersion(ver);
                try { if (Directory.Exists(vCache)) Directory.Delete(vCache, true); } catch { }

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

            Form topOwner = null;

            try

            {

                topOwner = new Form();

                topOwner.Size = new System.Drawing.Size(1, 1);

                topOwner.StartPosition = FormStartPosition.Manual;

                topOwner.Location = new System.Drawing.Point(-32000, -32000);

                topOwner.ShowInTaskbar = false;

                topOwner.TopMost = true;

                topOwner.Show();

                topOwner.BringToFront();

                topOwner.Activate();

                ownerHandle = topOwner.Handle;

            }

            catch { }



            try

            {

                // Дополнительный наблюдатель для мгновенного подъёма диалога (#32770)

                ThreadPool.QueueUserWorkItem(delegate

                {

                    int currentPid = Process.GetCurrentProcess().Id;

                    for (int i = 0; i < 40; i++)

                    {

                        Thread.Sleep(50);

                        IntPtr dlgHwnd = IntPtr.Zero;

                        Program.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)

                        {

                            if (dlgHwnd != IntPtr.Zero) return false;

                            if (Program.IsWindow(hWnd) && Program.IsWindowVisible(hWnd))

                            {

                                uint pid;

                                Program.GetWindowThreadProcessId(hWnd, out pid);

                                if (pid == (uint)currentPid)

                                {

                                    StringBuilder sb = new StringBuilder(64);

                                    Program.GetClassName(hWnd, sb, 64);

                                    if (sb.ToString() == "#32770")

                                    {

                                        dlgHwnd = hWnd;

                                        return false;

                                    }

                                }

                            }

                            return true;

                        }, IntPtr.Zero);



                        if (dlgHwnd != IntPtr.Zero)

                        {

                            Program.ForceForeground(dlgHwnd);

                            break;

                        }

                    }

                });



                Program.AllowSetForegroundWindow(-1);



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

                if (topOwner != null)

                {

                    try { topOwner.Close(); topOwner.Dispose(); topOwner = null; } catch { }

                }



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



                    DialogResult dr = (topOwner != null && !topOwner.IsDisposed) ? fbd.ShowDialog(topOwner) : fbd.ShowDialog();

                    if (topOwner != null)

                    {

                        try { topOwner.Close(); topOwner.Dispose(); topOwner = null; } catch { }

                    }



                    if (dr == DialogResult.OK)

                    {

                        return fbd.SelectedPath;

                    }

                }

                return null;

            }

            finally

            {

                if (topOwner != null)

                {

                    try { topOwner.Close(); topOwner.Dispose(); } catch { }

                }

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

        private static readonly object cleanLock = new object();

        private static readonly object runLock = new object();

        private static System.Threading.Timer debounceTimer = null;

        private static bool hostsChecked = false;

        private static bool hostsWarned = false;



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

                object parsed = jss.DeserializeObject(json);

                ArrayList list = null;
                Dictionary<string, object> wrapper = null;

                if (parsed is object[])
                {
                    list = new ArrayList((object[])parsed);
                }
                else if (parsed is ArrayList)
                {
                    list = (ArrayList)parsed;
                }
                else if (parsed is Dictionary<string, object>)
                {
                    wrapper = (Dictionary<string, object>)parsed;
                    if (wrapper.ContainsKey("servers"))
                    {
                        if (wrapper["servers"] is object[]) list = new ArrayList((object[])wrapper["servers"]);
                        else if (wrapper["servers"] is ArrayList) list = (ArrayList)wrapper["servers"];
                    }
                }

                if (list == null || list.Count == 0) return 0;



                var kept = new ArrayList();

                int removed = 0;

                foreach (object item in list)

                {

                    var dict = item as Dictionary<string, object>;

                    if (dict == null)
                    {
                        kept.Add(item);
                        continue;
                    }

                    string addr = dict.ContainsKey("address") ? (dict["address"] as string) : null;

                    string name = dict.ContainsKey("name") ? (dict["name"] as string) : null;

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

                    object toSerialize = kept;
                    if (wrapper != null)
                    {
                        wrapper["servers"] = kept;
                        toSerialize = wrapper;
                    }

                    string newJson = jss.Serialize(toSerialize);

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



        public static bool TryBlockHosts(bool force = false)

        {

            if (hostsChecked && !force) return false;

            hostsChecked = true;

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

            catch (Exception ex)

            {

                if (!hostsWarned)

                {

                    hostsWarned = true;

                    Program.Log("AdServerBlocker: hosts block skipped (" + ex.Message + ")");

                }

            }

            return false;

        }



        private static List<string> CollectMcRoots(string mcDir)

        {

            var roots = new List<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string> addRoot = (d) =>

            {

                if (string.IsNullOrEmpty(d)) return;

                string norm = d.Trim().TrimEnd('\\', '/');

                if (norm.Length > 0 && Directory.Exists(norm) && seen.Add(norm))

                {

                    roots.Add(norm);

                }

            };



            addRoot(mcDir);

            try { addRoot(Program.GetActiveMcDir()); } catch { }

            try { addRoot(Program.GetInstalledMcDir()); } catch { }

            try { addRoot(Program.GetDetectedMcDir()); } catch { }

            try

            {

                var all = Program.GetAllPotentialMcDirs();

                if (all != null)

                {

                    foreach (var d in all) addRoot(d);

                }

            }

            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            addRoot(Path.Combine(appData, ".minecraft"));

            return roots;

        }



        public static int CleanAll(string mcDir = null, bool forceHosts = false)

        {

            int totalRemoved = 0;

            lock (runLock)

            {

                try

                {

                    var roots = CollectMcRoots(mcDir);

                    var candidates = new List<string>();

                    Action<string> addCandidate = (p) =>

                    {

                        if (!string.IsNullOrEmpty(p)) candidates.Add(p);

                    };



                    foreach (var root in roots)

                    {

                        addCandidate(Path.Combine(root, "servers.dat"));

                        addCandidate(Path.Combine(root, "servers.dat_old"));

                        addCandidate(Path.Combine(root, @"versions\Civilization\servers.dat"));

                        addCandidate(Path.Combine(root, @"versions\Civilization\servers.dat_old"));

                        addCandidate(Path.Combine(root, @"versions\Civilization-1.21.8\servers.dat"));

                        addCandidate(Path.Combine(root, @"versions\Civilization-1.21.8\servers.dat_old"));

                        string versionsDir = Path.Combine(root, "versions");

                        if (Directory.Exists(versionsDir))

                        {

                            try

                            {

                                foreach (var sub in Directory.GetDirectories(versionsDir))

                                {

                                    addCandidate(Path.Combine(sub, "servers.dat"));

                                    addCandidate(Path.Combine(sub, "servers.dat_old"));

                                }

                            }

                            catch { }

                        }

                        try

                        {

                            foreach (var jf in Directory.GetFiles(root, "InnerStateServer*.json"))

                            {

                                addCandidate(jf);

                            }

                        }

                        catch { }

                    }



                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                    string tlDir = Path.Combine(appData, ".tlauncher");

                    if (Directory.Exists(tlDir))

                    {

                        try

                        {

                            foreach (var jf in Directory.GetFiles(tlDir, "InnerStateServer*.json"))

                            {

                                addCandidate(jf);

                            }

                        }

                        catch { }

                    }



                    var checkedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var p in candidates)

                    {

                        if (string.IsNullOrEmpty(p) || !checkedSet.Add(p) || !File.Exists(p)) continue;



                        if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))

                        {

                            totalRemoved += CleanInnerStateJson(p);

                        }

                        else if (p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||

                                 p.EndsWith(".dat_old", StringComparison.OrdinalIgnoreCase))

                        {

                            totalRemoved += CleanServersDat(p);

                        }

                    }



                    TryBlockHosts(forceHosts);

                }

                catch (Exception ex)

                {

                    Program.Log("AdServerBlocker CleanAll error: " + ex.Message);

                }

            }

            return totalRemoved;

        }



        public static void SetupWatchers()

        {

            try

            {

                var dirs = new List<string>();

                var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Action<string> addDir = (d) =>

                {

                    if (string.IsNullOrEmpty(d)) return;

                    string norm = d.Trim().TrimEnd('\\', '/');

                    if (norm.Length > 0 && Directory.Exists(norm) && seenDirs.Add(norm))

                    {

                        dirs.Add(norm);

                    }

                };



                foreach (var root in CollectMcRoots(null))

                {

                    addDir(root);

                    addDir(Path.Combine(root, @"versions\Civilization"));

                    addDir(Path.Combine(root, @"versions\Civilization-1.21.8"));

                    string versionsDir = Path.Combine(root, "versions");

                    if (Directory.Exists(versionsDir))

                    {

                        try

                        {

                            foreach (var sub in Directory.GetDirectories(versionsDir, "Civilization*"))

                            {

                                addDir(sub);

                            }

                        }

                        catch { }

                    }

                }



                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                addDir(Path.Combine(appData, ".tlauncher"));



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

                                fn.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase) ||

                                fn.StartsWith("InnerStateServer", StringComparison.OrdinalIgnoreCase))

                            {

                                TriggerDebounce();

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



        private static void TriggerDebounce()

        {

            lock (cleanLock)

            {

                if (debounceTimer != null)

                {

                    try { debounceTimer.Dispose(); } catch { }

                    debounceTimer = null;

                }

                debounceTimer = new System.Threading.Timer(state => {

                    try { CleanAll(); } catch { }

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

            ushort len = (ushort)ReadNbtShort(br);

            if (len == 0) return "";

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



    public partial class Program
    {
        private static readonly string FriendsBackendDefaultUrl = "https://civilization-friends.civilkaserver.workers.dev";
        private static readonly string FriendsClientKey = "civ-friends-26-3-k9m4x8q2";
        private static readonly string[] FriendsRoster = new string[] {
            "gugomot",
            "wwzmx",
            "wlimon4ik",
            "batton111221",
            "syn_mijaghi",
            "IMPERATOR_MIDASS"
        };

        private static readonly object friendsLock = new object();
        private static string cachedFriendsStateJson = null;
        private static string lastUploadedSkinHash = null;
        private static string lastUploadedSkinNick = null;
        private static long friendsLastBurstUtcTicks = 0;
        private static readonly AutoResetEvent friendsWakeEvent = new AutoResetEvent(false);
        private static readonly Dictionary<string, byte[]> friendsSkinBytesCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> friendsSkinHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void NotifyFriendsChanged()
        {
            try
            {
                Interlocked.Exchange(ref friendsLastBurstUtcTicks, DateTime.UtcNow.Ticks);
                friendsWakeEvent.Set();
            }
            catch { }
        }

        static string GetFriendsBackendUrl()
        {
            try
            {
                string envUrl = Environment.GetEnvironmentVariable("CIVILIZATION_FRIENDS_URL");
                if (!string.IsNullOrEmpty(envUrl))
                {
                    return envUrl.Trim().TrimEnd('/');
                }
            }
            catch { }

            try
            {
                string fileUrl = Path.Combine(GetLauncherDataDir(), "friends_url.txt");
                if (File.Exists(fileUrl))
                {
                    string txt = File.ReadAllText(fileUrl, Encoding.UTF8).Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(txt)) return txt;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(FriendsBackendDefaultUrl))
            {
                return FriendsBackendDefaultUrl.Trim().TrimEnd('/');
            }
            return "";
        }

        static string NormalizeFriendNick(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string t = raw.Trim();
            if (t.Length == 0) return null;
            string low = t.ToLowerInvariant();
            for (int i = 0; i < FriendsRoster.Length; i++)
            {
                if (FriendsRoster[i].ToLowerInvariant() == low) return FriendsRoster[i];
            }
            if (low == "maksim72622" || low == "persic760gmail_c" || low == "batton" || low == "batun") return "batton111221";
            if (low == "wlimonchik") return "wlimon4ik";
            if (low == "imperator_midas") return "IMPERATOR_MIDASS";
            return null;
        }

        static string GetActiveUsername()
        {
            try
            {
                string envNick = Environment.GetEnvironmentVariable("CIVILIZATION_FRIENDS_NICK");
                if (!string.IsNullOrEmpty(envNick))
                {
                    string cleanEnv = SanitizeMinecraftUsername(envNick.Trim());
                    string normEnv = NormalizeFriendNick(cleanEnv);
                    return normEnv ?? cleanEnv;
                }
            }
            catch { }

            string username = "gugomot";
            try
            {
                string mcDir = GetActiveMcDir();
                string tpPath = !string.IsNullOrEmpty(mcDir) ? Path.Combine(mcDir, "TlauncherProfiles.json") : "";
                if (string.IsNullOrEmpty(tpPath) || !File.Exists(tpPath))
                {
                    tpPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @".minecraft\TlauncherProfiles.json");
                }
                if (File.Exists(tpPath))
                {
                    var tp = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(tpPath, Encoding.UTF8));
                    if (tp != null)
                    {
                        string selUuid = tp.ContainsKey("selectedAccountUUID") ? (tp["selectedAccountUUID"] as string) : null;
                        if (string.IsNullOrEmpty(selUuid) && tp.ContainsKey("freeAccountUUID")) selUuid = tp["freeAccountUUID"] as string;
                        if (tp.ContainsKey("accounts") && tp["accounts"] is Dictionary<string, object>)
                        {
                            var accs = (Dictionary<string, object>)tp["accounts"];
                            Dictionary<string, object> chosen = null;
                            if (!string.IsNullOrEmpty(selUuid) && accs.ContainsKey(selUuid))
                            {
                                chosen = accs[selUuid] as Dictionary<string, object>;
                            }
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
                                if (chosen.ContainsKey("username") && chosen["username"] != null) username = chosen["username"].ToString();
                                else if (chosen.ContainsKey("displayName") && chosen["displayName"] != null) username = chosen["displayName"].ToString();
                            }
                        }
                    }
                }
            }
            catch { }

            string sanitized = SanitizeMinecraftUsername(username);
            string canonical = NormalizeFriendNick(sanitized);
            return canonical ?? sanitized;
        }

        static void ReadLocalSkinForSync(out string skinHash, out string skinModel, out byte[] skinBytes)
        {
            skinHash = "";
            skinModel = "classic";
            skinBytes = null;

            try
            {
                string localSkinDir = GetLauncherSkinsDir();
                string skinFile = Path.Combine(localSkinDir, "current_skin.png");
                if (!File.Exists(skinFile)) return;

                byte[] bytes = File.ReadAllBytes(skinFile);
                if (bytes == null || bytes.Length < 24 || bytes.Length > 165000) return;

                string jsonFile = Path.Combine(localSkinDir, "current_skin.json");
                if (File.Exists(jsonFile))
                {
                    try
                    {
                        string jtext = File.ReadAllText(jsonFile);
                        if (jtext.IndexOf("\"slim\"", StringComparison.OrdinalIgnoreCase) >= 0) skinModel = "slim";
                    }
                    catch { }
                }
                else
                {
                    string modelFile = Path.Combine(localSkinDir, "model.txt");
                    if (File.Exists(modelFile))
                    {
                        try
                        {
                            if (File.ReadAllText(modelFile).Trim().Equals("slim", StringComparison.OrdinalIgnoreCase)) skinModel = "slim";
                        }
                        catch { }
                    }
                }

                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hashBytes = sha.ComputeHash(bytes);
                    var sb = new StringBuilder(hashBytes.Length * 2);
                    for (int i = 0; i < hashBytes.Length; i++) sb.Append(hashBytes[i].ToString("x2"));
                    skinHash = sb.ToString();
                }
                skinBytes = bytes;
            }
            catch { }
        }

        static bool HttpRequestFriends(string method, string pathAndQuery, string jsonBody, out int statusCode, out byte[] responseBytes)
        {
            statusCode = 0;
            responseBytes = null;
            string baseUrl = GetFriendsBackendUrl();
            if (string.IsNullOrEmpty(baseUrl)) return false;

            try
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
                catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
                string fullUrl = baseUrl + pathAndQuery;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(fullUrl);
                req.Method = method;
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                req.Headers.Add("X-Client-Key", FriendsClientKey);
                req.UserAgent = "CivilizationLauncher/26.3";

                if (!string.IsNullOrEmpty(jsonBody))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(jsonBody);
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = payload.Length;
                    using (var reqStream = req.GetRequestStream())
                    {
                        reqStream.Write(payload, 0, payload.Length);
                    }
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    statusCode = (int)resp.StatusCode;
                    using (var ms = new MemoryStream())
                    using (var s = resp.GetResponseStream())
                    {
                        if (s != null) s.CopyTo(ms);
                        responseBytes = ms.ToArray();
                    }
                    return statusCode >= 200 && statusCode < 300;
                }
            }
            catch (WebException wex)
            {
                try
                {
                    var errResp = wex.Response as HttpWebResponse;
                    if (errResp != null)
                    {
                        statusCode = (int)errResp.StatusCode;
                        using (var ms = new MemoryStream())
                        using (var s = errResp.GetResponseStream())
                        {
                            if (s != null) s.CopyTo(ms);
                            responseBytes = ms.ToArray();
                        }
                    }
                }
                catch { }
                return false;
            }
            catch
            {
                return false;
            }
        }

        static string BuildFallbackFriendsStateJson(string me, bool configured, string errorMsg)
        {
            var friendsList = new List<Dictionary<string, object>>();
            string meNorm = NormalizeFriendNick(me) ?? me;
            for (int i = 0; i < FriendsRoster.Length; i++)
            {
                string nick = FriendsRoster[i];
                if (!string.IsNullOrEmpty(meNorm) && nick.Equals(meNorm, StringComparison.OrdinalIgnoreCase)) continue;
                friendsList.Add(new Dictionary<string, object> {
                    { "nick", nick },
                    { "status", "offline" },
                    { "activity", "Оффлайн" },
                    { "lastSeenMs", 0 },
                    { "skinHash", "" },
                    { "skinModel", "classic" },
                    { "unread", 0 },
                    { "lastMsgId", 0 }
                });
            }
            var payload = new Dictionary<string, object> {
                { "ok", true },
                { "configured", configured },
                { "offline", true },
                { "me", meNorm },
                { "error", errorMsg ?? "" },
                { "friends", friendsList }
            };
            return new JavaScriptSerializer().Serialize(payload);
        }

        static void SendRawJson(HttpListenerContext ctx, string json, int statusCode)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            ctx.Response.AddHeader("Pragma", "no-cache");
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Flush();
            ctx.Response.Close();
        }

        static void FriendsSyncLoop()
        {
            Thread.Sleep(600);
            while (isRunning)
            {
                try
                {
                    string baseUrl = GetFriendsBackendUrl();
                    string me = GetActiveUsername();

                    if (!string.IsNullOrEmpty(baseUrl))
                    {
                        bool inGame = false;
                        try { inGame = IsGameRunning(); } catch { }
                        string status = inGame ? "ingame" : "launcher";
                        string activity = inGame ? "Играет в Civilization" : "В лаунчере";

                        string skinHash;
                        string skinModel;
                        byte[] skinBytes;
                        ReadLocalSkinForSync(out skinHash, out skinModel, out skinBytes);

                        var body = new Dictionary<string, object> {
                            { "nick", me },
                            { "status", status },
                            { "activity", activity },
                            { "skinHash", skinHash },
                            { "skinModel", skinModel }
                        };

                        bool needsUpload = !string.IsNullOrEmpty(skinHash) &&
                            skinBytes != null &&
                            (!skinHash.Equals(lastUploadedSkinHash, StringComparison.OrdinalIgnoreCase) ||
                             !me.Equals(lastUploadedSkinNick, StringComparison.OrdinalIgnoreCase));

                        if (needsUpload)
                        {
                            body["skinBase64"] = Convert.ToBase64String(skinBytes);
                        }

                        var jss = new JavaScriptSerializer();
                        int code;
                        byte[] respBytes;
                        bool ok = HttpRequestFriends("POST", "/friends/sync", jss.Serialize(body), out code, out respBytes);
                        if (ok && respBytes != null && respBytes.Length > 0)
                        {
                            if (needsUpload || string.IsNullOrEmpty(skinHash))
                            {
                                lastUploadedSkinHash = skinHash;
                                lastUploadedSkinNick = me;
                            }
                            string rawJson = Encoding.UTF8.GetString(respBytes);
                            try
                            {
                                var parsed = jss.Deserialize<Dictionary<string, object>>(rawJson);
                                if (parsed != null)
                                {
                                    parsed["configured"] = true;
                                    parsed["offline"] = false;
                                    string normalizedJson = jss.Serialize(parsed);
                                    lock (friendsLock) { cachedFriendsStateJson = normalizedJson; }
                                }
                            }
                            catch
                            {
                                lock (friendsLock) { cachedFriendsStateJson = rawJson; }
                            }
                        }
                        else
                        {
                            lock (friendsLock)
                            {
                                if (string.IsNullOrEmpty(cachedFriendsStateJson))
                                {
                                    cachedFriendsStateJson = BuildFallbackFriendsStateJson(me, true, code > 0 ? ("HTTP " + code) : "Сервер недоступен");
                                }
                            }
                        }
                    }
                    else
                    {
                        lock (friendsLock)
                        {
                            cachedFriendsStateJson = BuildFallbackFriendsStateJson(me, false, "Бэкенд друзей не настроен");
                        }
                    }
                }
                catch { }

                long lastBurst = Interlocked.Read(ref friendsLastBurstUtcTicks);
                bool inBurst = lastBurst > 0 && (DateTime.UtcNow.Ticks - lastBurst) < TimeSpan.FromSeconds(30).Ticks;
                int sleepMs = inBurst ? 4500 : 12000;
                friendsWakeEvent.WaitOne(sleepMs);
            }
        }

        static void HandleFriendsStateApi(HttpListenerContext ctx)
        {
            bool active = ctx.Request.QueryString["active"] == "1";
            bool force = ctx.Request.QueryString["force"] == "1";
            if (active)
            {
                long prevBurst = Interlocked.Exchange(ref friendsLastBurstUtcTicks, DateTime.UtcNow.Ticks);
                bool wasCold = prevBurst == 0 || (DateTime.UtcNow.Ticks - prevBurst) >= TimeSpan.FromSeconds(30).Ticks;
                if (wasCold || force) friendsWakeEvent.Set();
            }

            string json;
            lock (friendsLock)
            {
                json = cachedFriendsStateJson;
            }
            if (string.IsNullOrEmpty(json))
            {
                string me = GetActiveUsername();
                bool configured = !string.IsNullOrEmpty(GetFriendsBackendUrl());
                json = BuildFallbackFriendsStateJson(me, configured, "");
            }
            SendRawJson(ctx, json, 200);
        }

        static void HandleFriendsSkinApi(HttpListenerContext ctx)
        {
            string rawNick = ctx.Request.QueryString["nick"] ?? "";
            string nick = NormalizeFriendNick(rawNick);
            string hash = ctx.Request.QueryString["h"] ?? "";
            if (string.IsNullOrEmpty(nick))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            byte[] cachedBytes = null;
            lock (friendsLock)
            {
                string cachedHash;
                if (friendsSkinHashCache.TryGetValue(nick, out cachedHash) &&
                    !string.IsNullOrEmpty(hash) &&
                    hash == cachedHash &&
                    friendsSkinBytesCache.TryGetValue(nick, out cachedBytes))
                {
                    // cache hit
                }
                else
                {
                    cachedBytes = null;
                }
            }

            if (cachedBytes != null && cachedBytes.Length > 0)
            {
                ServeBytes(ctx, cachedBytes, "image/png");
                return;
            }

            int code;
            byte[] remoteBytes;
            bool ok = HttpRequestFriends("GET", "/friends/skin?nick=" + Uri.EscapeDataString(nick), null, out code, out remoteBytes);
            if (ok && remoteBytes != null && remoteBytes.Length >= 24)
            {
                lock (friendsLock)
                {
                    friendsSkinBytesCache[nick] = remoteBytes;
                    if (!string.IsNullOrEmpty(hash)) friendsSkinHashCache[nick] = hash;
                }
                ServeBytes(ctx, remoteBytes, "image/png");
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        static void HandleFriendsChatApi(HttpListenerContext ctx)
        {
            Interlocked.Exchange(ref friendsLastBurstUtcTicks, DateTime.UtcNow.Ticks);
            string me = GetActiveUsername();
            string baseUrl = GetFriendsBackendUrl();
            if (string.IsNullOrEmpty(baseUrl))
            {
                SendJson(ctx, new Dictionary<string, object> {
                    { "ok", false },
                    { "configured", false },
                    { "error", "Бэкенд друзей не настроен (friends_url.txt)" }
                });
                return;
            }

            if (ctx.Request.HttpMethod == "POST")
            {
                try
                {
                    string rawBody = ReadBody(ctx);
                    var jss = new JavaScriptSerializer();
                    var data = jss.Deserialize<Dictionary<string, object>>(rawBody);
                    string to = data != null && data.ContainsKey("to") && data["to"] != null ? data["to"].ToString() : "";
                    string text = data != null && data.ContainsKey("text") && data["text"] != null ? data["text"].ToString() : "";
                    var forward = new Dictionary<string, object> {
                        { "from", me },
                        { "to", to },
                        { "text", text }
                    };
                    int code;
                    byte[] respBytes;
                    bool ok = HttpRequestFriends("POST", "/friends/chat", jss.Serialize(forward), out code, out respBytes);
                    friendsWakeEvent.Set();
                    if (respBytes != null && respBytes.Length > 0)
                    {
                        SendRawJson(ctx, Encoding.UTF8.GetString(respBytes), ok ? 200 : (code > 0 ? code : 502));
                        return;
                    }
                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", "Не удалось отправить сообщение" } });
                    return;
                }
                catch (Exception ex)
                {
                    SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
                    return;
                }
            }

            string friend = NormalizeFriendNick(ctx.Request.QueryString["friend"] ?? "");
            string after = ctx.Request.QueryString["after"] ?? "0";
            if (string.IsNullOrEmpty(friend))
            {
                SendJson(ctx, new Dictionary<string, object> { { "ok", false }, { "error", "Неизвестный друг" } });
                return;
            }

            int getCode;
            byte[] getBytes;
            string q = "/friends/chat?me=" + Uri.EscapeDataString(me) + "&friend=" + Uri.EscapeDataString(friend) + "&after=" + Uri.EscapeDataString(after);
            bool getOk = HttpRequestFriends("GET", q, null, out getCode, out getBytes);
            if (getOk && getBytes != null && getBytes.Length > 0)
            {
                try
                {
                    var jss = new JavaScriptSerializer();
                    lock (friendsLock)
                    {
                        if (!string.IsNullOrEmpty(cachedFriendsStateJson))
                        {
                            var st = jss.Deserialize<Dictionary<string, object>>(cachedFriendsStateJson);
                            if (st != null && st.ContainsKey("friends") && st["friends"] is ArrayList)
                            {
                                var arr = (ArrayList)st["friends"];
                                foreach (object item in arr)
                                {
                                    var fd = item as Dictionary<string, object>;
                                    if (fd != null && fd.ContainsKey("nick") && friend.Equals(fd["nick"] as string, StringComparison.OrdinalIgnoreCase))
                                    {
                                        fd["unread"] = 0;
                                    }
                                }
                                cachedFriendsStateJson = jss.Serialize(st);
                            }
                        }
                    }
                }
                catch { }

                SendRawJson(ctx, Encoding.UTF8.GetString(getBytes), 200);
                return;
            }

            SendJson(ctx, new Dictionary<string, object> {
                { "ok", false },
                { "error", getCode > 0 ? ("HTTP " + getCode) : "Нет связи с сервером друзей" }
            });
        }
    }

    public static class SkinManager

    {

        private static readonly byte[] PngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };



        public static bool ValidatePng(byte[] bytes, out int width, out int height, out string error)

        {

            width = 0;

            height = 0;

            error = null;



            if (bytes == null || bytes.Length < 24)

            {

                error = "Файл скина пуст или поврежден (слишком маленький размер)";

                return false;

            }



            for (int i = 0; i < PngHeader.Length; i++)

            {

                if (bytes[i] != PngHeader[i])

                {

                    error = "Файл не является корректным изображением формата PNG";

                    return false;

                }

            }



            // Читаем размеры из IHDR чанка (байты 16-23 Big Endian)

            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];

            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];



            if (width <= 0 || height <= 0)

            {

                error = "Некорректные размеры изображения в заголовке PNG";

                return false;

            }



            // Проверяем стандартные разрешения скинов:

            // 64x64 (стандарт), 64x32 (старый формат), 128x128 (HD)

            bool isStandard = (width == 64 && (height == 64 || height == 32));

            bool isHd = (width == 128 && (height == 128 || height == 64));



            if (!isStandard && !isHd)

            {

                Program.Log(string.Format("[SkinManager] Предупреждение: нестандартное разрешение скина: {0}x{1}", width, height));

            }



            return true;

        }



        public static void SaveSkinFiles(string targetDir, byte[] pngBytes, string model)

        {

            if (string.IsNullOrEmpty(targetDir)) return;

            try

            {

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);



                string skinPngPath = Path.Combine(targetDir, "current_skin.png");

                string skinJsonPath = Path.Combine(targetDir, "current_skin.json");



                if (pngBytes == null || pngBytes.Length == 0)

                {

                    RemoveSkinFiles(targetDir);

                    return;

                }



                File.WriteAllBytes(skinPngPath, pngBytes);



                string normModel = (model != null && model.Trim().ToLower() == "slim") ? "slim" : "classic";

                string json = string.Format("{{\n  \"model\": \"{0}\"\n}}", normModel);

                File.WriteAllText(skinJsonPath, json, Encoding.UTF8);



                // Дополнительно сохраняем model.txt для обратной совместимости

                try

                {

                    File.WriteAllText(Path.Combine(targetDir, "model.txt"), normModel, Encoding.UTF8);

                }

                catch { }

            }

            catch (Exception ex)

            {

                Program.Log(string.Format("[SkinManager] Ошибка сохранения скина в {0}: {1}", targetDir, ex.Message));

            }

        }



        public static void RemoveSkinFiles(string targetDir)

        {

            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

            try

            {

                string skinPngPath = Path.Combine(targetDir, "current_skin.png");

                if (File.Exists(skinPngPath)) File.Delete(skinPngPath);

            }

            catch { }

            try

            {

                string skinJsonPath = Path.Combine(targetDir, "current_skin.json");

                if (File.Exists(skinJsonPath)) File.Delete(skinJsonPath);

            }

            catch { }

            try

            {

                string modelTxt = Path.Combine(targetDir, "model.txt");

                if (File.Exists(modelTxt)) File.Delete(modelTxt);

            }

            catch { }

        }



        public static void SyncSkinToGame(string appDir, string mcDir, string explicitCivDir = null)
        {
            try
            {
                string localSkinDir = (Directory.Exists(Path.Combine(appDir, "skins"))) ? Path.Combine(appDir, "skins") : Program.GetLauncherSkinsDir();
                string localPng = Path.Combine(localSkinDir, "current_skin.png");
                string civDir = !string.IsNullOrEmpty(explicitCivDir) ? explicitCivDir : (!string.IsNullOrEmpty(mcDir) ? Path.Combine(mcDir, @"versions\Civilization") : null);
                string sep1218Dir = !string.IsNullOrEmpty(mcDir) ? Path.Combine(mcDir, @"versions\Civilization-1.21.8") : null;

                if (!File.Exists(localPng))

                {

                    // Скин не выбран - удаляем файлы из директорий игры, чтобы использовался дефолтный скин

                    if (!string.IsNullOrEmpty(civDir)) RemoveSkinFiles(civDir);

                    if (!string.IsNullOrEmpty(mcDir)) RemoveSkinFiles(mcDir);

                    Program.Log("[SkinManager] Скин не выбран, файлы current_skin.png/json удалены из папки игры.");

                    return;

                }



                byte[] bytes = File.ReadAllBytes(localPng);

                int width, height;

                string valError;

                if (!ValidatePng(bytes, out width, out height, out valError))

                {

                    Program.Log(string.Format("[SkinManager] Локальный скин поврежден: {0}. Очищаем файлы.", valError));

                    if (!string.IsNullOrEmpty(civDir)) RemoveSkinFiles(civDir);

                    if (!string.IsNullOrEmpty(mcDir)) RemoveSkinFiles(mcDir);

                    return;

                }



                string model = "classic";

                string jsonFile = Path.Combine(localSkinDir, "current_skin.json");

                if (File.Exists(jsonFile))

                {

                    try

                    {

                        string jtext = File.ReadAllText(jsonFile);

                        if (jtext.IndexOf("\"slim\"", StringComparison.OrdinalIgnoreCase) >= 0)

                            model = "slim";

                    }

                    catch { }

                }

                else

                {

                    string modelTxt = Path.Combine(localSkinDir, "model.txt");

                    if (File.Exists(modelTxt))

                    {

                        string mt = File.ReadAllText(modelTxt).Trim().ToLower();

                        if (mt == "slim") model = "slim";

                    }

                }



                // Сохраняем в Game Directory (<mcDir>\versions\Civilization)

                if (!string.IsNullOrEmpty(civDir))

                {

                    SaveSkinFiles(civDir, bytes, model);
                    if (!string.IsNullOrEmpty(sep1218Dir) && Directory.Exists(sep1218Dir) && !sep1218Dir.Equals(civDir, StringComparison.OrdinalIgnoreCase)) { SaveSkinFiles(sep1218Dir, bytes, model); }

                    Program.Log(string.Format("[SkinManager] Синхронизирован скин в GameDir ({0}): {1}x{2}, model={3}", civDir, width, height, model));

                }



                // Сохраняем в корневую .minecraft (<mcDir>)

                if (!string.IsNullOrEmpty(mcDir))

                {

                    SaveSkinFiles(mcDir, bytes, model);

                }

            }

            catch (Exception ex)

            {

                Program.Log(string.Format("[SkinManager] Ошибка синхронизации скина перед запуском: {0}", ex.Message));

            }

        }



        public static string GetCurrentSkinModel(string appDir)

        {

            try

            {

                string localSkinDir = (Directory.Exists(Path.Combine(appDir, "skins"))) ? Path.Combine(appDir, "skins") : Program.GetLauncherSkinsDir();

                string jsonFile = Path.Combine(localSkinDir, "current_skin.json");

                if (File.Exists(jsonFile))

                {

                    string jtext = File.ReadAllText(jsonFile);

                    if (jtext.IndexOf("\"slim\"", StringComparison.OrdinalIgnoreCase) >= 0)

                        return "slim";

                    return "classic";

                }

                string modelTxt = Path.Combine(localSkinDir, "model.txt");

                if (File.Exists(modelTxt))

                {

                    string mt = File.ReadAllText(modelTxt).Trim().ToLower();

                    if (mt == "slim") return "slim";

                }

            }

            catch { }

            return "classic";

        }



        public static int GetJavaUuidHashCode(ulong msb, ulong lsb)

        {

            long hilo = (long)(msb ^ lsb);

            return ((int)(hilo >> 32)) ^ (int)hilo;

        }



        public static string AdjustUuidForModel(string hexUuid, bool wantSlim)

        {

            if (string.IsNullOrEmpty(hexUuid)) return wantSlim ? "ee8cd5dee7bb400ba8e50e60210c3926" : "ee8cd5dee7bb400ba8e50e60210c3927";

            try

            {

                string clean = hexUuid.Replace("-", "").Trim();

                if (clean.Length != 32) return hexUuid;



                ulong msb = Convert.ToUInt64(clean.Substring(0, 16), 16);

                ulong lsb = Convert.ToUInt64(clean.Substring(16, 16), 16);



                bool currentSlim = (GetJavaUuidHashCode(msb, lsb) & 1) != 0;

                if (currentSlim != wantSlim)

                {

                    lsb ^= 1UL;

                }



                return msb.ToString("x16") + lsb.ToString("x16");

            }

            catch

            {

                return hexUuid;

            }

        }

    }

}

