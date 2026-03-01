using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace ClipMixer{
    //puts the mpv player inside wpf. events come straight off the mpv thread so subscribers have to BeginInvoke
    //everything just does nothing until the player exists
    public sealed class MpvHost : HwndHost{
        public event Action<double>? DurationChanged;
        public event Action<double>? TimePosChanged;
        public event Action<bool>? PauseChanged;
        public event Action? FileLoaded;
        public event Action<string>? LogMessage;
        public event Action? GraphRejected;

        private IntPtr hwnd;

        public MpvPlayer? Player { get; private set; }
        public string? InitError { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent){
            //WS_DISABLED so the video never steals focus/mouse, hotkeys keep going to the wpf window
            //idk why this shit took so long to figure out
            hwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_DISABLED,
                0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if(hwnd == IntPtr.Zero){
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create child window for mpv.");
            }

            if(!DesignerProperties.GetIsInDesignMode(this)){//xaml designer makes one of these too and might not have mpv
                try{
                    MpvPlayer p = new MpvPlayer(hwnd);
                    p.DurationChanged += d => DurationChanged?.Invoke(d);
                    p.TimePosChanged += t => TimePosChanged?.Invoke(t);
                    p.PauseChanged += v => PauseChanged?.Invoke(v);
                    p.FileLoaded += () => FileLoaded?.Invoke();
                    p.LogMessage += m => LogMessage?.Invoke(m);
                    p.GraphRejected += () => GraphRejected?.Invoke();
                    Player = p;
                }catch(Exception ex){
                    InitError = ex.Message;
                }
            }

            return new HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd){
            shutdownPlayer();
            if(hwnd.Handle != IntPtr.Zero){
                DestroyWindow(hwnd.Handle);
            }
        }

        protected override void Dispose(bool disposing){
            shutdownPlayer();
            base.Dispose(disposing);
        }

        private void shutdownPlayer(){
            MpvPlayer? p = Player;
            Player = null;
            p?.Dispose();
        }

        public string? LoadedPath => Player?.LoadedPath;
        public void load(string path, string? lavfiComplex = null, double? startSec = null, bool? paused = null) =>
            Player?.load(path, lavfiComplex, startSec, paused);
        public Task stopAsync() => Player?.stopAsync() ?? Task.CompletedTask;
        public void play() => Player?.play();
        public void pause() => Player?.pause();
        public void togglePause() => Player?.togglePause();
        public bool IsPaused => Player?.IsPaused ?? true;
        public void seek(double seconds) => Player?.seek(seconds);
        public void seekRelative(double seconds) => Player?.seekRelative(seconds);
        public double getTimePosSeconds() => Player?.getTimePos() ?? 0;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_DISABLED = 0x08000000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr hwndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
    }
}
