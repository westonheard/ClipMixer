using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ClipMixer{
    public partial class App : Application{
        protected override void OnStartup(StartupEventArgs e){
            DispatcherUnhandledException += onDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) => {logCrash(args.Exception); args.SetObserved();};

            base.OnStartup(e);

            try{
                string baseDir = AppContext.BaseDirectory;
                string mpvDir = Path.Combine(baseDir, "mpv");
                string mpvDll = Path.Combine(mpvDir, "mpv-1.dll");

                if(Directory.Exists(mpvDir)){
                    addToProcessPath(mpvDir);

                    //copied this dll directory stuff, idk wtf it does exactly but mpv wont load its deps without it
                    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
                    AddDllDirectory(mpvDir);
                }

                if(File.Exists(mpvDll)){
                    IntPtr h = LoadLibraryW(mpvDll);//load it now so a missing dep shows up here and not later
                    if(h == IntPtr.Zero){
                        int err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err,
                            $"Couldn't load mpv-1.dll (Win32 error {err}).\n" +
                            $"Make sure all DLLs from your mpv build are in: {mpvDir}");
                    }
                }else{
                    MessageBox.Show(
                        $"mpv-1.dll not found at:\n{mpvDll}\n\n" +
                        "Put mpv-1.dll and its DLLs in an 'mpv' folder next to ClipMixer.exe.",
                        "ClipMixer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }catch(Exception ex){
                //dont block the ui from opening, just show it
                MessageBox.Show(ex.ToString(), "ClipMixer startup warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            //dont make the main window here, StartupUri already does. had 2 windows w/ 2 players open because of that lol
            //new MainWindow().Show();
        }

        private void onDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e){
            string path = logCrash(e.Exception);
            MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails saved to:\n{path}",
                "ClipMixer", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;//keep running, trims in the background still keep the originals safe
        }

        private static string logCrash(Exception ex){
            string path = Path.Combine(AppSettings.LogDir, "errors.log");
            try{
                Directory.CreateDirectory(AppSettings.LogDir);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
            }catch{}
            return path;
        }

        private static void addToProcessPath(string dir){
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if(!path.Contains(dir, StringComparison.OrdinalIgnoreCase)){
                Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
            }
        }

        private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr AddDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string NewDirectory);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);
    }
}
