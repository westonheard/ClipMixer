using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ClipMixer{
    public partial class App : Application{
        protected override void OnStartup(StartupEventArgs e){
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
                            $"LoadLibrary failed for mpv-1.dll. Win32Error={err}.\n" +
                            $"Make sure ALL DLLs from your mpv build are in: {mpvDir}");
                    }
                }else{
                    MessageBox.Show(
                        $"mpv-1.dll not found.\nExpected at:\n{mpvDll}\n\n" +
                        "Create a folder named 'mpv' next to ClipMixer.exe and put mpv-1.dll and its dependency DLLs there.",
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
