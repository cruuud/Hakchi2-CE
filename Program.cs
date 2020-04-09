﻿#pragma warning disable 0618
using com.clusterrr.hakchi_gui.Properties;
using Microsoft.Win32.SafeHandles;
using SpineGen.DrawingBitmaps;
using SpineGen.JSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using TeamShinkansen.Scrapers.Interfaces;

namespace com.clusterrr.hakchi_gui
{
    static class Program
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, uint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, uint hTemplateFile);

        private const int MY_CODE_PAGE = 437;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 0x3;
        public static string BaseDirectoryInternal = Path.GetDirectoryName(Application.ExecutablePath);
        public static string BaseDirectoryExternal;
        public static bool isPortable = false;
        public static List<Stream> debugStreams = new List<Stream>();
        private static Dictionary<string, SpineTemplate<Bitmap>> _SpineTemplates;
        public static IReadOnlyDictionary<string, SpineTemplate<Bitmap>> SpineTemplates
        {
            get => _SpineTemplates;
        }

        private static List<IScraper> _Scrapers = new List<IScraper>();
        public static IReadOnlyList<IScraper> Scrapers
        {
            get => _Scrapers;
        }

        public static MultiFormContext FormContext = new MultiFormContext();
        internal static TeamShinkansen.Scrapers.TheGamesDB.Scraper TheGamesDBAPI = null;
        static void SetupScrapers()
        {
            if (Resources.TheGamesDBKey != "")
            {
                TheGamesDBAPI = new TeamShinkansen.Scrapers.TheGamesDB.Scraper()
                {
                    ApiKey = Resources.TheGamesDBKey,
                    CachePath = Path.Combine(Program.BaseDirectoryExternal, "cache", "thegamesdb"),
                    ArtSize = TeamShinkansen.Scrapers.TheGamesDB.ArtSize.Thumb
                };

                _Scrapers.Add(TheGamesDBAPI);
                TeamShinkansen.Scrapers.TheGamesDB.API.TraceURLs = true;
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            var versionFileArgIndex = -1;
            if (args != null && (versionFileArgIndex = Array.IndexOf(args, "--versionFile")) != -1)
            {
                string versionFormat = "{0}";
                var versionFormatArgIndex = -1;
                if (args != null && (versionFormatArgIndex = Array.IndexOf(args, "--versionFormat")) != -1)
                {
                    versionFormat = args[versionFormatArgIndex + 1];
                }
                File.WriteAllText(args[versionFileArgIndex + 1], String.Format(versionFormat, Shared.AppDisplayVersion));
                return;
            }
#if DEBUG
            try
            {
                AllocConsole();
                IntPtr stdHandle = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                SafeFileHandle safeFileHandle = new SafeFileHandle(stdHandle, true);
                FileStream consoleFileStream = new FileStream(safeFileHandle, FileAccess.Write);
                Encoding encoding = System.Text.Encoding.GetEncoding(MY_CODE_PAGE);
                StreamWriter standardOutput = new StreamWriter(consoleFileStream, encoding);
                standardOutput.AutoFlush = true;
                Console.SetOut(standardOutput);
                debugStreams.Add(consoleFileStream);
                Debug.Listeners.Add(new TextWriterTraceListener(System.Console.Out));
            }
            catch { }
            try
            {
                Stream logFile = File.Create("debuglog.txt");
                debugStreams.Add(logFile);
                Debug.Listeners.Add(new TextWriterTraceListener(logFile));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
            }
            Debug.AutoFlush = true;
#else
            Trace.Listeners.Clear();
#endif
#if TRACE
            try
            {
                MemoryStream inMemoryLog = new MemoryStream();
                debugStreams.Add(inMemoryLog);
                Trace.Listeners.Add(new TextWriterTraceListener(new StreamWriter(inMemoryLog, System.Text.Encoding.GetEncoding(MY_CODE_PAGE))));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message + ex.StackTrace);
            }
            Trace.AutoFlush = true;
#endif
            isPortable = args == null || !args.Contains("/nonportable") || args.Contains("/portable");

            
            //When running on mounted ext4 disks, or wherever a .NET executable does not have normal network access,
            //run the executable locally and everything else on the external share
            //Of course, only applies to portable configs            
            if (isPortable)
            {
                var basedirFile = Path.Combine(BaseDirectoryInternal, "hakchi.basedir");
                if (args != null)
                {
                    var basedirIndex = -1;
                    if ((basedirIndex = Array.IndexOf(args, "--basedir")) != -1)
                    {
                        if ((basedirIndex + 1) < args.Length)
                        {
                            File.WriteAllText(basedirFile, args[basedirIndex + 1]);
                        }
                    }
                }
                if (File.Exists(basedirFile))
                {                    
                    BaseDirectoryInternal = File.ReadAllText(basedirFile);
                }
            }

            if (File.Exists(Path.Combine(BaseDirectoryInternal, "nonportable.flag")))
                isPortable = false;

            bool isFirstRun = false;
            
            if (!isPortable)
            {
                isFirstRun = Shared.isFirstRun();
            }

            try
            {
                bool createdNew = true;
                using (Mutex mutex = new Mutex(true, "hakchi2", out createdNew))
                {
                    if (createdNew)
                    {
                        if (!isPortable)
                        {
                            BaseDirectoryExternal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "hakchi2");
                            try
                            {
                                if (!Directory.Exists(BaseDirectoryExternal))
                                {
                                    Directory.CreateDirectory(BaseDirectoryExternal);
                                }

                                // There are some folders which should be accessed by user
                                // Moving them to "My documents"
                                var externalDirs = new string[]
                                    { "art", "folder_images", "info", "patches", "sfrom_tool", "user_mods", "spine_templates" };
                                foreach (var dir in externalDirs)
                                {
                                    var sourceDir = Path.Combine(BaseDirectoryInternal, dir);
                                    var destDir = Path.Combine(BaseDirectoryExternal, dir);
                                    if (isFirstRun || !Directory.Exists(destDir))
                                    {
                                        Shared.DirectoryCopy(sourceDir, destDir, true, false, true, false);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // TODO: Test it on Windows XP
                                Trace.WriteLine(ex.Message);
                            }
                        }
                        else
                            BaseDirectoryExternal = BaseDirectoryInternal;

                        Directory.SetCurrentDirectory(BaseDirectoryInternal);

                        Trace.WriteLine("Base directory: " + BaseDirectoryExternal + " (" + (isPortable ? "portable" : "non-portable") + " mode)");
                        ConfigIni.Load();
                        try
                        {
                            if (!string.IsNullOrEmpty(ConfigIni.Instance.Language))
                                Thread.CurrentThread.CurrentUICulture = new CultureInfo(ConfigIni.Instance.Language);
                        }
                        catch { }

                        string languagesDirectory = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "languages");
                        const string langFileNames = "hakchi.resources.dll";
                        AppDomain.CurrentDomain.AppendPrivatePath(languagesDirectory);
                        // For updates
                        var oldFiles = Directory.GetFiles(Path.GetDirectoryName(Application.ExecutablePath), langFileNames, SearchOption.AllDirectories);
                        foreach (var d in oldFiles)
                        {
                            if (!d.Contains(Path.DirectorySeparatorChar + "languages" + Path.DirectorySeparatorChar))
                            {
                                var dir = Path.GetDirectoryName(d);
                                Trace.WriteLine("Removing old directory: " + dir);
                                if (!isPortable)
                                {
                                    var targetDir = Path.Combine(languagesDirectory, Path.GetFileName(dir));
                                    Directory.CreateDirectory(targetDir);
                                    var targetFile = Path.Combine(targetDir, langFileNames);
                                    if (File.Exists(targetFile))
                                        File.Delete(targetFile);
                                    File.Copy(Path.Combine(dir, langFileNames), targetFile);
                                }
                                else
                                    Directory.Delete(dir, true);
                            }
                        }

                        Trace.WriteLine("Loading spine templates");
                        var templateDir = new DirectoryInfo(Path.Combine(BaseDirectoryExternal, "spine_templates"));
                        _SpineTemplates = new Dictionary<string, SpineTemplate<Bitmap>>();
                        if (templateDir.Exists)
                        {
                            foreach (var dir in templateDir.GetDirectories())
                            {
                                if (dir.GetFiles().Where(file => file.Name == "template.json" || file.Name == "template.png").Count() == 2)
                                {
                                    using (var file = File.OpenRead(Path.Combine(dir.FullName, "template.png")))
                                        _SpineTemplates.Add(dir.Name, SpineTemplate<Bitmap>.FromJsonFile(new SystemDrawingBitmap(new Bitmap(file) as Bitmap), Path.Combine(dir.FullName, "template.json")));
                                }
                            }
                        }

                        SetupScrapers();

                        Trace.WriteLine("Starting, version: " + Shared.AppDisplayVersion);

                        System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)4080; // set default security protocol
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);

                        FormContext.AllFormsClosed += Process.GetCurrentProcess().Kill; // Suicide! Just easy and dirty way to kill all threads.

                        FormContext.AddForm(new MainForm());
                        Application.Run(FormContext);
                        Trace.WriteLine("Done.");
                    }
                    else
                    {
                        Process current = Process.GetCurrentProcess();
                        foreach (Process process in Process.GetProcessesByName("hakchi"))
                        {
                            if (process.Id != current.Id)
                            {
                                ShowWindow(process.MainWindowHandle, 9); // Restore
                                SetForegroundWindow(process.MainWindowHandle); // Foreground
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(ex.Message + ex.StackTrace, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static string GetCurrentLogContent()
        {
            MemoryStream stream = debugStreams.OfType<MemoryStream>().FirstOrDefault();
            if (stream != default(MemoryStream))
            {
                return Encoding.GetEncoding(MY_CODE_PAGE).GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
            return "";
        }

        [DllImport("Shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)]Guid rfid, uint dwFlags,
            IntPtr hToken, out IntPtr ppszPath);
        private static string GetDocumentsLibraryPath()
        {
            IntPtr outPath;
            var documentsLibraryGuid = new Guid("7B0DB17D-9CD2-4A93-9733-46CC89022E7C");
            int result = SHGetKnownFolderPath(documentsLibraryGuid, 0, WindowsIdentity.GetCurrent().Token, out outPath);
            if (result >= 0)
            {
                var libConfigPath = Marshal.PtrToStringUni(outPath);
                var libConfig = new XmlDocument();
                libConfig.LoadXml(File.ReadAllText(libConfigPath));
                var nsmgr = new XmlNamespaceManager(libConfig.NameTable);
                nsmgr.AddNamespace("ns", libConfig.LastChild.NamespaceURI);
                var docs = libConfig.SelectSingleNode("//ns:searchConnectorDescription[ns:isDefaultSaveLocation='true']/ns:simpleLocation/ns:url/text()", nsmgr);
                if (Directory.Exists(docs.Value))
                    return docs.Value;
                else
                    throw new Exception("Invalid Documents directory: " + docs.Value);
            }
            else
            {
                throw new ExternalException("Cannot get the known folder path. It may not be available on this system.",
                    result);
            }
        }

    }
}
