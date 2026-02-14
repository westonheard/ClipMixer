using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipMixer{
    public partial class MainWindow : Window{
        private readonly FfmpegRunner runner = FfmpegRunner.createDefault();

        private readonly ObservableCollection<ClipEntry> clips = new();//what the list shows
        private readonly List<ClipEntry> allClips = new();//every clip found
        private string? folder;
        private CancellationTokenSource? scanCts;

        private ClipEntry? current;
        private MediaInfo? info;
        private int loadToken;
        private bool suppressSelection;

        public MainWindow(){
            InitializeComponent();

            ClipList.ItemsSource = clips;

            Loaded += onLoaded;
            Closing += onClosing;

            updateCounts();
            status("Open a folder of clips to begin.");
        }

        private void onLoaded(object sender, RoutedEventArgs e){
            //open a folder passed on the command line
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                string[] args = Environment.GetCommandLineArgs();
                string? arg = args.Length > 1 ? args[1] : null;
                if(arg != null && Directory.Exists(arg)){
                    _ = openFolderAsync(Path.GetFullPath(arg), null);
                }else if(arg != null && File.Exists(arg)){
                    _ = openFolderAsync(Path.GetDirectoryName(Path.GetFullPath(arg))!, Path.GetFullPath(arg));
                }
            }));
        }

        //-------- folder scanning --------

        private void openFolderClick(object sender, RoutedEventArgs e) => showOpenFolderDialog();

        private void showOpenFolderDialog(){
            OpenFolderDialog dlg = new OpenFolderDialog { Title = "Choose a folder of clips" };
            if(!string.IsNullOrEmpty(folder) && Directory.Exists(folder)){dlg.InitialDirectory = folder;}
            if(dlg.ShowDialog(this) == true){
                _ = openFolderAsync(dlg.FolderName, null);
            }
        }

        private void refreshClick(object sender, RoutedEventArgs e){
            if(folder != null){_ = openFolderAsync(folder, current?.FullPath);}
        }

        private async Task openFolderAsync(string folder, string? selectPath){
            if(!Directory.Exists(folder)){status("Folder not found: " + folder); return;}

            scanCts?.Cancel();
            CancellationTokenSource cts = scanCts = new CancellationTokenSource();

            this.folder = folder;
            FolderText.Text = folder;
            FolderText.ToolTip = folder;

            clips.Clear();
            allClips.Clear();
            clearCurrent();
            ScanText.Text = "Scanning...";

            bool autoSelected = false;

            try{
                ScanSummary summary = await ClipLibrary.scanAsync(folder, runner,
                    entry => Dispatcher.BeginInvoke(() => {
                        if(cts.IsCancellationRequested){return;}
                        addClip(entry);
                        updateCounts();
                        //open the first clip (or the requested one) asap
                        bool requested = selectPath != null && string.Equals(entry.FullPath, selectPath, StringComparison.OrdinalIgnoreCase);
                        if(!autoSelected && current == null && (requested ||
                            (selectPath == null && !entry.IsTrimmed))){
                            autoSelected = true;
                            selectClip(entry);
                        }
                    }),
                    (done, total) => Dispatcher.BeginInvoke(() => {
                        if(!cts.IsCancellationRequested && done < total){ScanText.Text = $"Checking clips... {done}/{total}";}
                    }),
                    cts.Token);

                if(cts.IsCancellationRequested){return;}

                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.Background);
                if(cts.IsCancellationRequested){return;}

                if(current == null && clips.Count > 0){selectClip(clips.FirstOrDefault(c => !c.IsTrimmed) ?? clips[0]);}

                updateCounts();

                if(!allClips.Any(c => !c.IsTrimmed)){
                    status(summary.Candidates == 0
                        ? "No video clips (.mp4 .mov .m4v .mkv) found in this folder."
                        : "Nothing left to review in this folder.");
                }
            }catch(OperationCanceledException){}catch(Exception ex){
                if(!cts.IsCancellationRequested){
                    ScanText.Text = "Scan failed.";
                    status("Couldn't scan folder: " + ex.Message);
                }
            }
        }

        private void addClip(ClipEntry entry){
            removeClip(entry.FullPath);
            allClips.Add(entry);
            if(entry.IsTrimmed){return;}
            int i = 0;
            while(i < clips.Count && string.Compare(clips[i].RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase) <= 0){i++;}
            clips.Insert(i, entry);
        }

        private void removeClip(string path){
            allClips.RemoveAll(c => string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase));
            foreach(ClipEntry c in clips.Where(c => string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase)).ToList()){
                clips.Remove(c);
            }
        }

        private void updateCounts(){
            int toReview = allClips.Count(c => !c.IsTrimmed);
            ScanText.Text = folder == null ? "" : toReview == 1 ? "1 clip to review" : $"{toReview} clips to review";
        }

        //-------- clip selection / loading --------

        private void clipListSelectionChanged(object sender, SelectionChangedEventArgs e){
            if(suppressSelection){return;}
            if(ClipList.SelectedItem is ClipEntry entry && !ReferenceEquals(entry, current)){
                _ = loadClipAsync(entry);
            }
        }

        private void selectClip(ClipEntry entry){
            ClipList.SelectedItem = entry;//this triggers loadClipAsync thru SelectionChanged
            ClipList.ScrollIntoView(entry);
        }

        private async Task loadClipAsync(ClipEntry entry){
            int token = ++loadToken;
            current = entry;
            this.info = null;

            FileText.Text = entry.FileName;
            FileText.ToolTip = entry.FullPath;
            ClipInfoText.Text = entry.Folder;

            MediaInfo? info = null;
            try{
                info = await runner.probeAsync(entry.FullPath);
            }catch(Exception ex){
                if(token == loadToken){status($"Couldn't read clip details ({ex.Message}). It may be damaged.");}
            }
            if(token != loadToken){return;}

            this.info = info;
            if(info != null){
                List<string> bits = new List<string>();
                if(info.Width > 0){bits.Add($"{info.Width}x{info.Height}");}
                if(info.VideoCodec != null){bits.Add(info.VideoCodec);}
                if(info.Fps > 0){bits.Add($"{info.Fps:0.##} fps");}
                bits.Add(info.AudioTrackCount == 1 ? "1 audio track" : $"{info.AudioTrackCount} audio tracks");
                if(info.DurationSeconds > 0){bits.Add(formatDur(info.DurationSeconds));}
                bits.Add(entry.SizeText);
                if(!string.IsNullOrEmpty(entry.Folder)){bits.Add(entry.Folder);}
                ClipInfoText.Text = string.Join("  |  ", bits);
                status(entry.HasNote ? entry.Note!
                    : "Hit Next for the next clip.");
            }
        }

        private void clearCurrent(){
            loadToken++;
            current = null;
            info = null;
            suppressSelection = true;
            ClipList.SelectedItem = null;
            suppressSelection = false;
            FileText.Text = "No clip selected";
            ClipInfoText.Text = "";
        }

        private void nextClick(object sender, RoutedEventArgs e) => navigate(+1);
        private void prevClick(object sender, RoutedEventArgs e) => navigate(-1);

        private void navigate(int delta){
            if(clips.Count == 0){return;}
            int idx = current != null ? clips.IndexOf(current) : -1;
            int target = idx < 0 ? 0 : idx + delta;
            if(target < 0 || target >= clips.Count){
                status(delta > 0 ? "That was the last clip in the list." : "That's the first clip in the list.");
                return;
            }
            selectClip(clips[target]);
        }

        private void onClosing(object? sender, CancelEventArgs e){
            scanCts?.Cancel();
        }

        private void status(string msg) => StatusText.Text = msg;

        private static string formatDur(double sec){
            if(sec < 0){sec = 0;}
            TimeSpan ts = TimeSpan.FromSeconds(sec);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }
    }
}
