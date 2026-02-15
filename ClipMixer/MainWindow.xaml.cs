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
        private double durationSec;
        private double inSec, outSec, playSec;
        private bool outUserSet;
        private bool draggingPlayhead;
        private bool probeDone;//details for current are known (or failed)
        private bool suppressSelection;

        //mpv time updates get squashed into one ui update at a time
        private double latestTime;
        private int timeUpdatePending;

        public MainWindow(){
            InitializeComponent();

            ClipList.ItemsSource = clips;

            //mpv fires these on its own thread, always BeginInvoke never a blocking Invoke
            Mpv.TimePosChanged += t => {
                latestTime = t;
                if(Interlocked.Exchange(ref timeUpdatePending, 1) == 0){
                    Dispatcher.BeginInvoke(() => {Interlocked.Exchange(ref timeUpdatePending, 0); onTimePos(latestTime);});
                }
            };
            Mpv.DurationChanged += d => Dispatcher.BeginInvoke(() => onMpvDuration(d));
            Mpv.PauseChanged += paused => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = paused ? "Play" : "Pause");

            Loaded += onLoaded;
            Closing += onClosing;

            updateMarkerTexts();
            updateCounts();
            status("Open a folder of clips to begin.");
        }

        private void onLoaded(object sender, RoutedEventArgs e){
            //give the video host a sec to make the player, then report problems / reopen last folder
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                if(Mpv.InitError != null){
                    status("Video preview unavailable: " + Mpv.InitError);
                }

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

        private void subfoldersChanged(object sender, RoutedEventArgs e){
            if(!IsLoaded){return;}
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
            await clearCurrentAsync();
            ScanText.Text = "Scanning...";

            bool recurse = SubfoldersCheck.IsChecked == true;
            bool autoSelected = false;

            try{
                ScanSummary summary = await ClipLibrary.scanAsync(folder, recurse, runner,
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
            durationSec = 0;
            inSec = outSec = playSec = 0;
            outUserSet = false;
            probeDone = false;

            //dont keep showing the old clip under the new name while probing
            if(Mpv.LoadedPath != null && !string.Equals(Mpv.LoadedPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)){
                _ = Mpv.stopAsync();
            }
            TimeText.Text = "";

            FileText.Text = entry.FileName;
            FileText.ToolTip = entry.FullPath;
            ClipInfoText.Text = entry.Folder;
            updateMarkerTexts();
            updateTimelineVisuals();

            MediaInfo? info = null;
            try{
                info = await runner.probeAsync(entry.FullPath);
            }catch(Exception ex){
                if(token == loadToken){status($"Couldn't read clip details ({ex.Message}). It may be damaged.");}
            }
            if(token != loadToken){return;}

            this.info = info;
            probeDone = true;
            if(info != null){
                if(info.DurationSeconds > 0){setDuration(info.DurationSeconds);}
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
                    : "Set In and Out, or hit Next to skip.");
            }

            Mpv.load(entry.FullPath, null, false);
        }

        private async Task clearCurrentAsync(){
            loadToken++;
            probeDone = false;
            current = null;
            info = null;
            durationSec = 0;
            inSec = outSec = playSec = 0;
            suppressSelection = true;
            ClipList.SelectedItem = null;
            suppressSelection = false;
            FileText.Text = "No clip selected";
            ClipInfoText.Text = "";
            TimeText.Text = "";
            updateMarkerTexts();
            updateTimelineVisuals();
            await Mpv.stopAsync();
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

        //-------- mpv callbacks (already on ui thread) --------

        private void onTimePos(double t){
            if(current == null){return;}
            double dur = durationSec > 0 ? durationSec : t;
            playSec = Math.Clamp(t, 0, Math.Max(0.001, dur));
            TimeText.Text = $"{FfmpegRunner.formatTs(playSec)} / {FfmpegRunner.formatTs(durationSec)}";
            if(!draggingPlayhead){updatePlayheadVisual();}
        }

        private void onMpvDuration(double d){
            //ffprobes duration wins, mpvs only gets used if probing failed
            if(current != null && probeDone && d > 0 && durationSec <= 0 &&
                string.Equals(Mpv.LoadedPath, current.FullPath, StringComparison.OrdinalIgnoreCase)){
                setDuration(d);
            }
        }

        private void setDuration(double d){
            durationSec = d;
            if(!outUserSet || outSec <= 0 || outSec > d){outSec = d;}
            inSec = Math.Clamp(inSec, 0, d);
            if(inSec >= outSec){inSec = 0;}
            updateMarkerTexts();
            updateTimelineVisuals();
        }

        private void playPauseClick(object sender, RoutedEventArgs e) => togglePlay();
        private void videoMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => togglePlay();

        private void togglePlay(){
            if(current == null){return;}
            Mpv.togglePause();
        }

        private void seekTo(double sec){
            if(current == null){return;}
            playSec = Math.Clamp(sec, 0, Math.Max(0, durationSec));
            updatePlayheadVisual();
            Mpv.seek(playSec);
        }

        private void setInClick(object sender, RoutedEventArgs e) => setIn(Mpv.getTimePosSeconds());
        private void setOutClick(object sender, RoutedEventArgs e) => setOut(Mpv.getTimePosSeconds());

        private void setIn(double t){
            if(current == null || durationSec <= 0){return;}
            inSec = Math.Clamp(t, 0, Math.Max(0, durationSec - 0.1));
            if(inSec >= outSec - 0.05){outSec = durationSec; outUserSet = false;}//in past out, so out goes back to the end
            markersChanged();
        }

        private void setOut(double t){
            if(current == null || durationSec <= 0){return;}
            outSec = Math.Clamp(t, 0, durationSec);
            outUserSet = true;
            if(outSec <= inSec + 0.05){inSec = 0;}//out before in, in goes back to start
            if(outSec < 0.1){outSec = durationSec; outUserSet = false;}
            markersChanged();
        }

        private void markersChanged(){
            updateMarkerTexts();
            updateTimelineVisuals();
        }

        private void updateMarkerTexts(){
            InText.Text = FfmpegRunner.formatTs(inSec);
            OutText.Text = FfmpegRunner.formatTs(outSec);
            KeepText.Text = durationSec > 0 ? $"Keep {formatDur(outSec - inSec)} of {formatDur(durationSec)}" : "";
        }

        private void inOutTextLostFocus(object sender, RoutedEventArgs e) => commitTextBox((TextBox)sender);

        private void commitTextBox(TextBox tb){
            if(current == null || durationSec <= 0){updateMarkerTexts(); return;}
            TimeSpan ts;
            bool ok;
            try{ok = FfmpegRunner.tryParseTs(tb.Text, out ts);}catch{ok = false; ts = TimeSpan.Zero;}
            if(!ok){
                status($"'{tb.Text}' isn't a valid time. Use 1:23.5, 00:01:23.500 or 83.5");
                updateMarkerTexts();
                return;
            }
            if(ReferenceEquals(tb, InText)){setIn(ts.TotalSeconds);}else{setOut(ts.TotalSeconds);}
        }

        private void onClosing(object? sender, CancelEventArgs e){
            scanCts?.Cancel();
        }

        //-------- timeline --------

        private const double TimelinePad = 14;
        private const double TrackTop = 30;

        private double DurationSeconds => durationSec;
        private double TrackLeft => TimelinePad;
        private double TrackRight => Math.Max(TrackLeft + 1, Timeline.ActualWidth - TimelinePad);
        private double TrackWidth => TrackRight - TrackLeft;

        private double xFromTime(double sec) => TrackLeft + (Math.Clamp(sec, 0, DurationSeconds) / DurationSeconds) * TrackWidth;
        private double timeFromX(double x) => Math.Clamp((x - TrackLeft) / Math.Max(1, TrackWidth) * DurationSeconds, 0, DurationSeconds);

        private void timelineSizeChanged(object sender, SizeChangedEventArgs e) => updateTimelineVisuals();

        private void timelineMouseLeftButtonDown(object sender, MouseButtonEventArgs e){
            if(current == null || durationSec <= 0){return;}
            seekTo(timeFromX(e.GetPosition(Timeline).X));
        }

        private void markerDragStarted(object sender, DragStartedEventArgs e){
            if(ReferenceEquals(sender, PlayheadThumb)){draggingPlayhead = true;}
        }

        private void markerDragCompleted(object sender, DragCompletedEventArgs e){
            draggingPlayhead = false;
            updateTimelineVisuals();
        }

        private double thumbCenter(Thumb t, double fallbackSec){
            double left = Canvas.GetLeft(t);
            if(double.IsNaN(left)){left = xFromTime(fallbackSec) - t.Width / 2.0;}
            return left + t.Width / 2.0;
        }

        private void inThumbDragDelta(object sender, DragDeltaEventArgs e){
            if(current == null || durationSec <= 0){return;}
            double x = Math.Clamp(thumbCenter(InThumb, inSec) + e.HorizontalChange, TrackLeft, Math.Max(TrackLeft, xFromTime(outSec) - 2));
            inSec = Math.Min(timeFromX(x), Math.Max(0, outSec - 0.1));
            markersChanged();
            seekTo(inSec);
        }

        private void outThumbDragDelta(object sender, DragDeltaEventArgs e){
            if(current == null || durationSec <= 0){return;}
            double x = Math.Clamp(thumbCenter(OutThumb, outSec) + e.HorizontalChange, Math.Min(TrackRight, xFromTime(inSec) + 2), TrackRight);
            outSec = Math.Max(timeFromX(x), Math.Min(durationSec, inSec + 0.1));
            outUserSet = true;
            markersChanged();
            seekTo(outSec);
        }

        private void playheadThumbDragDelta(object sender, DragDeltaEventArgs e){
            if(current == null || durationSec <= 0){return;}
            double x = Math.Clamp(thumbCenter(PlayheadThumb, playSec) + e.HorizontalChange, TrackLeft, TrackRight);
            seekTo(timeFromX(x));
        }

        private void updateTimelineVisuals(){
            if(Timeline == null){return;}

            Canvas.SetLeft(TimelineTrack, TrackLeft);
            Canvas.SetTop(TimelineTrack, TrackTop);
            TimelineTrack.Width = TrackWidth;

            bool has = current != null && durationSec > 0;
            Visibility vis = has ? Visibility.Visible : Visibility.Hidden;
            TimelineSelection.Visibility = InThumb.Visibility = OutThumb.Visibility = PlayheadThumb.Visibility = vis;
            if(!has){return;}

            double xIn = xFromTime(inSec);
            double xOut = xFromTime(outSec);
            if(xOut < xIn){(xIn, xOut) = (xOut, xIn);}
            Canvas.SetLeft(TimelineSelection, xIn);
            Canvas.SetTop(TimelineSelection, TrackTop);
            TimelineSelection.Width = Math.Max(0, xOut - xIn);

            Canvas.SetLeft(InThumb, xIn - InThumb.Width / 2.0);
            Canvas.SetTop(InThumb, 0);
            Canvas.SetLeft(OutThumb, xOut - OutThumb.Width / 2.0);
            Canvas.SetTop(OutThumb, 0);

            updatePlayheadVisual();
        }

        private void updatePlayheadVisual(){
            Canvas.SetLeft(PlayheadThumb, xFromTime(playSec) - PlayheadThumb.Width / 2.0);
            Canvas.SetTop(PlayheadThumb, 4);
        }

        private void status(string msg) => StatusText.Text = msg;

        private static string formatDur(double sec){
            if(sec < 0){sec = 0;}
            TimeSpan ts = TimeSpan.FromSeconds(sec);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }
    }
}
