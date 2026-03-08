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
        private readonly ClipProcessor processor;
        private readonly AppSettings settings = AppSettings.load();

        private readonly ObservableCollection<ClipEntry> clips = new();//what the list shows
        private readonly List<ClipEntry> allClips = new();//every clip found, trimmed ones too
        private int oldNamedExports;//"_trimmed" files from the old app, never listed
        private string? folder;
        private CancellationTokenSource? scanCts;

        //trim queue, one at a time so u can keep going thru clips
        private readonly ObservableCollection<ClipJob> jobs = new();
        private readonly Channel<ClipJob> jobQueue = Channel.CreateUnbounded<ClipJob>();
        private readonly HashSet<string> activeJobPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource shutdownCts = new();
        private Task? jobWorker;
        private bool closeConfirmed;

        private ClipEntry? current;
        private MediaInfo? info;
        private int loadToken;
        private double durationSec;
        private double inSec, outSec, playSec;
        private bool outUserSet;
        private bool draggingPlayhead;
        private bool deleting;
        private bool probeDone;//details for current are known (or failed)
        private bool suppressSelection;
        private string? appliedGraph;

        private readonly DispatcherTimer keyframeDebounce;
        private int keyframeToken;
        private readonly DispatcherTimer fxDebounce;

        private readonly ObservableCollection<TrackSettings> tracks = new();
        private bool suspendAudioEvents;
        private int menuOpen;

        //mpv time updates get squashed into one ui update at a time
        private double latestTime;
        private int timeUpdatePending;

        public MainWindow(){
            InitializeComponent();

            ClipList.ItemsSource = clips;
            JobList.ItemsSource = jobs;
            TrackList.ItemsSource = tracks;
            AudioExpander.IsExpanded = settings.AudioPanelOpen;

            SubfoldersCheck.IsChecked = settings.IncludeSubfolders;
            PreciseCheck.IsChecked = settings.PreciseCut;
            RecycleCheck.IsChecked = settings.UseRecycleBin;
            ConfirmDeleteCheck.IsChecked = settings.ConfirmDelete;
            AutoPlayCheck.IsChecked = settings.AutoPlay;

            processor = new ClipProcessor(runner, AppSettings.LogDir) { ReleaseFileAsync = releaseFromPlayerAsync };

            keyframeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            keyframeDebounce.Tick += (_, __) => {keyframeDebounce.Stop(); updateCutInfo();};
            fxDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            fxDebounce.Tick += (_, __) => {fxDebounce.Stop(); reapplyPreviewGraph();};

            //mpv fires these on its own thread, always BeginInvoke never a blocking Invoke
            Mpv.TimePosChanged += t => {
                latestTime = t;
                if(Interlocked.Exchange(ref timeUpdatePending, 1) == 0){
                    Dispatcher.BeginInvoke(() => {Interlocked.Exchange(ref timeUpdatePending, 0); onTimePos(latestTime);});
                }
            };
            Mpv.DurationChanged += d => Dispatcher.BeginInvoke(() => onMpvDuration(d));
            Mpv.PauseChanged += paused => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = paused ? "Play" : "Pause");
            Mpv.GraphRejected += () => Dispatcher.BeginInvoke(() =>
                status("The player couldn't apply the audio settings to this clip, so you're hearing it unprocessed."));

            Loaded += onLoaded;
            Closing += onClosing;

            updateAudioState();
            updateMarkerTexts();
            updateCounts();
            status("Open a folder of clips to begin (Ctrl+O).");
        }

        private void onLoaded(object sender, RoutedEventArgs e){
            jobWorker = runJobsAsync();
            focusRoot();

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
                }else if(!string.IsNullOrEmpty(settings.LastFolder) && Directory.Exists(settings.LastFolder)){
                    _ = openFolderAsync(settings.LastFolder, null);
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
            settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
            if(folder != null){_ = openFolderAsync(folder, current?.FullPath);}
        }

        private async Task openFolderAsync(string folder, string? selectPath){
            if(!Directory.Exists(folder)){status("Folder not found: " + folder); return;}

            scanCts?.Cancel();
            CancellationTokenSource cts = scanCts = new CancellationTokenSource();

            this.folder = folder;
            settings.LastFolder = folder;
            FolderText.Text = folder;
            FolderText.ToolTip = folder;

            clips.Clear();
            allClips.Clear();
            oldNamedExports = 0;
            await clearCurrentAsync();
            ScanText.Text = "Scanning...";

            HashSet<string> active = new HashSet<string>(activeJobPaths, StringComparer.OrdinalIgnoreCase);
            bool recurse = SubfoldersCheck.IsChecked == true;
            bool autoSelected = false;

            try{
                ScanSummary summary = await ClipLibrary.scanAsync(folder, recurse, runner, active,
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

                oldNamedExports = Math.Max(0, summary.AlreadyTrimmed - allClips.Count(c => c.IsTrimmed));
                updateCounts();

                if(!allClips.Any(c => !c.IsTrimmed)){
                    status(summary.Candidates == 0 && summary.AlreadyTrimmed == 0
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
            int pending = jobs.Count(j => !j.IsFinished);
            int toReview = allClips.Count(c => !c.IsTrimmed);
            int trimmed = allClips.Count(c => c.IsTrimmed) + oldNamedExports;
            List<string> parts = new List<string> { toReview == 1 ? "1 clip to review" : $"{toReview} clips to review" };
            if(pending > 0){parts.Add($"{pending} processing");}
            if(trimmed > 0){parts.Add($"{trimmed} already trimmed");}
            ScanText.Text = folder == null ? "" : string.Join("  -  ", parts);
        }

        //-------- clip selection / loading --------

        private void clipListSelectionChanged(object sender, SelectionChangedEventArgs e){
            if(suppressSelection || deleting){return;}
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
            appliedGraph = null;
            durationSec = 0;
            inSec = outSec = playSec = 0;
            outUserSet = false;
            probeDone = false;

            //dont keep showing the old clip under the new name while probing
            if(Mpv.LoadedPath != null && !string.Equals(Mpv.LoadedPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)){
                _ = Mpv.stopAsync();
            }
            clearAudio();
            TimeText.Text = "";

            FileText.Text = entry.FileName;
            FileText.ToolTip = entry.FullPath;
            ClipInfoText.Text = entry.Folder;
            CutInfoText.Text = "";
            KeyframeMarker.Visibility = Visibility.Collapsed;
            updateMarkerTexts();
            updateTimelineVisuals();

            MediaInfo? info = null;
            try{
                info = await runner.probeAsync(entry.FullPath);
            }catch(Exception ex){
                if(token == loadToken){status($"Couldn't read clip details ({ex.Message}). It may be damaged - you can still delete it.");}
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
                    : entry.IsTrimmed ? "This clip was already trimmed by ClipMixer. You can trim it again or delete it."
                    : "Set In (I) and Out (O), then Enter to trim - or Del to delete, N to skip.");
            }

            if(info != null){setupAudio(info);}

            appliedGraph = buildPreviewGraph();
            Mpv.load(entry.FullPath, appliedGraph, null, AutoPlayCheck.IsChecked != true);
            updateCutInfo();
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
            CutInfoText.Text = "";
            TimeText.Text = "";
            KeyframeMarker.Visibility = Visibility.Collapsed;
            updateMarkerTexts();
            updateTimelineVisuals();
            clearAudio();
            await Mpv.stopAsync();
        }

        private async Task goToIndexAfterRemovalAsync(int index){
            if(!clips.Any(c => !c.IsTrimmed)){
                await clearCurrentAsync();
                status(jobs.Any(j => !j.IsFinished) ? "All clips reviewed - waiting for trims to finish." : "All clips in this folder are reviewed.");
                return;
            }
            int start = Math.Clamp(index, 0, clips.Count - 1);
            ClipEntry next = clips.Skip(start).FirstOrDefault(c => !c.IsTrimmed)
                       ?? clips.Take(start).LastOrDefault(c => !c.IsTrimmed)
                       ?? clips[start];
            if(!ReferenceEquals(next, current)){selectClipForce(next);}
        }

        private void nextClick(object sender, RoutedEventArgs e) => navigate(+1);
        private void prevClick(object sender, RoutedEventArgs e) => navigate(-1);

        private void navigate(int delta){
            if(clips.Count == 0 || deleting){return;}
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
            if(current == null || durationSec <= 0 || deleting){return;}
            inSec = Math.Clamp(t, 0, Math.Max(0, durationSec - 0.1));
            if(inSec >= outSec - 0.05){outSec = durationSec; outUserSet = false;}//in past out, so out goes back to the end
            markersChanged();
        }

        private void setOut(double t){
            if(current == null || durationSec <= 0 || deleting){return;}
            outSec = Math.Clamp(t, 0, durationSec);
            outUserSet = true;
            if(outSec <= inSec + 0.05){inSec = 0;}//out before in, in goes back to start
            if(outSec < 0.1){outSec = durationSec; outUserSet = false;}
            markersChanged();
        }

        private void markersChanged(){
            updateMarkerTexts();
            updateTimelineVisuals();
            keyframeDebounce.Stop();
            keyframeDebounce.Start();
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

        private void updateCutInfo(){
            KeyframeMarker.Visibility = Visibility.Collapsed;
            updateTimelineVisuals();
            CutInfoText.Foreground = (Brush)FindResource("TextFg");
            if(current == null || durationSec <= 0){CutInfoText.Text = ""; return;}

            if(this.inSec <= 0.05 && outSec >= durationSec - 0.05){
                CutInfoText.Text = AudioChanged
                    ? "In/Out cover the whole clip: Enter keeps its full length and only applies the audio changes."
                    : "In/Out cover the whole clip. Set In (I) and/or Out (O) to trim it.";
                return;
            }
            if(PreciseCheck.IsChecked == true){
                CutInfoText.Text = "Frame-accurate: the trimmed clip starts exactly at In (video is re-encoded to H.264).";
                return;
            }
            if(this.inSec <= 0.0005){
                CutInfoText.Text = "Lossless video cut (no re-encode).";
                return;
            }

            string path = current.FullPath;
            double inSec = this.inSec;
            double startTime = info?.StartTime ?? 0;
            int token = ++keyframeToken;
            CutInfoText.Text = "Finding the nearest keyframe...";
            _ = Task.Run(async () => {
                try{
                    double k = await runner.getKeyframeAtOrBeforeAsync(path, inSec, startTime);
                    await Dispatcher.InvokeAsync(() => {
                        if(token != keyframeToken || current?.FullPath != path){return;}
                        double diff = inSec - k;
                        if(diff < 0.05){
                            CutInfoText.Text = "Lossless video cut (no re-encode). In is on a keyframe.";
                        }else{
                            CutInfoText.Text = $"Lossless cut starts at the keyframe at {FfmpegRunner.formatTs(k)}  ({diff:0.00}s before In)." +
                                               (diff > 1 ? " Tick 'Frame-accurate start' for an exact start." : "");
                            if(diff > 1){CutInfoText.Foreground = (Brush)FindResource("WarnFg");}
                            KeyframeMarker.Tag = k;
                            KeyframeMarker.Visibility = Visibility.Visible;
                            updateTimelineVisuals();
                        }
                    });
                }catch(Exception ex){
                    await Dispatcher.InvokeAsync(() => {
                        if(token == keyframeToken){CutInfoText.Text = "Couldn't read keyframes: " + ex.Message;}
                    });
                }
            });
        }

        //-------- trim & replace / delete --------

        private void trimClick(object sender, RoutedEventArgs e) => trimCurrent();
        private void deleteClick(object sender, RoutedEventArgs e) => _ = deleteCurrentAsync();

        private void trimCurrent(){
            ClipEntry? entry = current;
            if(deleting){return;}
            if(entry == null){status("No clip selected."); return;}
            if(durationSec <= 0){status("This clip is still loading (or couldn't be read)."); return;}
            if(outSec - inSec < 0.1){status("Out must be after In."); return;}
            AudioPlan? plan = currentPlan();
            bool audioChanged = plan != null && !plan.IsPassthrough;
            bool fullLength = inSec <= 0.05 && outSec >= durationSec - 0.05;
            if(fullLength && !audioChanged){
                status("In/Out cover the whole clip - nothing to trim. Set In (I) / Out (O) first, or press N to skip.");
                return;
            }
            if(plan != null && plan.Tracks.Count > 0 && plan.OutputTrackCount == 0){
                MessageBoxResult r = MessageBox.Show(this, "Every audio track is unticked, so the trimmed clip will have NO audio at all.\n\nTrim it without audio?",
                    "No audio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if(r != MessageBoxResult.Yes || !ReferenceEquals(entry, current) || deleting){return;}
            }

            TrimRequest req = new TrimRequest
            {
                SourcePath = entry.FullPath,
                InSec = fullLength ? 0 : inSec,
                OutSec = fullLength ? durationSec : outSec,
                PreciseCut = PreciseCheck.IsChecked == true && !fullLength,
                Audio = audioChanged ? plan : null,
                UseRecycleBin = RecycleCheck.IsChecked == true,
            };

            entry.Note = null;
            ClipJob job = new ClipJob(entry, req);
            jobs.Insert(0, job);
            activeJobPaths.Add(entry.FullPath);

            double keepLen = outSec - inSec;
            int idx = clips.IndexOf(entry);
            removeClip(entry.FullPath);
            _ = goToIndexAfterRemovalAsync(idx);

            jobQueue.Writer.TryWrite(job);
            status(fullLength
                ? $"Queued: {entry.FileName}  (full length, audio changes only). It's replaced only after the new file is verified."
                : $"Queued: {entry.FileName}  ({formatDur(keepLen)}). It's replaced only after the new file is verified.");
            updateCounts();
        }

        private async Task deleteCurrentAsync(){
            ClipEntry? entry = current;
            if(entry == null || deleting){return;}
            if(!probeDone){status("Still loading this clip - try again in a moment."); return;}

            bool recycle = RecycleCheck.IsChecked == true;
            if(ConfirmDeleteCheck.IsChecked == true){
                string how = recycle ? "It will be moved to the Recycle Bin." : "It will be PERMANENTLY deleted.";
                MessageBoxResult r = MessageBox.Show(this, $"Delete this clip?\n\n{entry.FileName}\n\n{how}",
                    "Delete clip", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if(r != MessageBoxResult.Yes){return;}
            }
            if(!ReferenceEquals(entry, current) || deleting){return;}

            //detach the clip before awaiting anything so nothing else can grab it meanwhile
            deleting = true;
            int idx = clips.IndexOf(entry);
            loadToken++;
            keyframeToken++;
            current = null;
            probeDone = false;
            durationSec = 0;
            inSec = outSec = playSec = 0;
            updateMarkerTexts();
            updateTimelineVisuals();
            status($"Deleting {entry.FileName}...");

            bool deleted = false;
            try{
                await Mpv.stopAsync();//player has to let go of the file first
                await FileOps.deleteAsync(entry.FullPath, recycle);
                deleted = true;

                removeClip(entry.FullPath);
                status($"Deleted {entry.FileName}{(recycle ? " (in the Recycle Bin)" : " permanently")}.");
            }catch(Exception ex){
                status($"Couldn't delete {entry.FileName}: {ex.Message}");
            }finally{
                deleting = false;
                updateCounts();
            }

            if(deleted){await goToIndexAfterRemovalAsync(idx);}else if(File.Exists(entry.FullPath) && clips.Contains(entry)){selectClipForce(entry);}
        }

        private void selectClipForce(ClipEntry entry){
            suppressSelection = true;
            ClipList.SelectedItem = entry;
            suppressSelection = false;
            ClipList.ScrollIntoView(entry);
            _ = loadClipAsync(entry);
        }

        //trim job calls this right before it renames the original
        private Task releaseFromPlayerAsync(string path){
            return Dispatcher.InvokeAsync(async () => {
                if(string.Equals(Mpv.LoadedPath, path, StringComparison.OrdinalIgnoreCase)){
                    await Mpv.stopAsync();
                }
                if(string.Equals(current?.FullPath, path, StringComparison.OrdinalIgnoreCase)){keyframeToken++;}
            }).Task.Unwrap();
        }

        private async Task runJobsAsync(){
            try{
                await foreach(ClipJob job in jobQueue.Reader.ReadAllAsync(shutdownCts.Token)){
                    string src = job.Request.SourcePath;
                    job.report("Starting", 0);
                    try{
                        TrimResult result = await Task.Run(() => processor.trimAndReplaceAsync(job.Request,
                            (stage, p) => Dispatcher.BeginInvoke(() => {if(!job.IsFinished){job.report(stage, p);}}),
                            shutdownCts.Token));

                        if(File.Exists(src) && !shutdownCts.IsCancellationRequested && belongsToOpenFolder(src)){
                            FileInfo fi = new FileInfo(src);
                            addClip(new ClipEntry
                            {
                                FullPath = src,
                                RelativePath = Path.GetRelativePath(folder!, src),
                                SizeBytes = fi.Length,
                                DurationSeconds = result.OutputDurationSec,
                                ModifiedUtc = fi.LastWriteTimeUtc,
                                IsTrimmed = true,
                            });
                        }
                        job.succeed(result);
                        status($"Trimmed {job.FileName} to {formatDur(result.OutputDurationSec)}." +
                               (result.Warnings.Count > 0 ? "  Note: " + string.Join(" ", result.Warnings) : ""));
                    }catch(Exception ex){
                        string msg = ex.Message;
                        string? log = (ex as ClipJobException)?.LogPath;
                        job.fail(msg, log);
                        status($"Trim failed for {job.FileName}: {msg}" + (log != null ? $"  (log: {log})" : ""));

                        if(File.Exists(src) && !shutdownCts.IsCancellationRequested && belongsToOpenFolder(src)){
                            FileInfo fi = new FileInfo(src);
                            ClipEntry back = new ClipEntry
                            {
                                FullPath = src,
                                RelativePath = Path.GetRelativePath(folder!, src),
                                SizeBytes = fi.Length,
                                DurationSeconds = job.Entry.DurationSeconds,
                                ModifiedUtc = fi.LastWriteTimeUtc,
                                IsTrimmed = job.Entry.IsTrimmed,
                                Note = "Last trim failed: " + msg,
                            };
                            addClip(back);
                            if(current == null && !deleting){selectClipForce(back);}
                        }
                    }finally{
                        activeJobPaths.Remove(src);
                        updateCounts();
                    }
                }
            }catch(OperationCanceledException){}
        }

        private bool belongsToOpenFolder(string path){
            if(folder == null){return false;}
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            if(SubfoldersCheck.IsChecked == true){
                return string.Equals(dir, root, StringComparison.OrdinalIgnoreCase) ||
                       dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
        }

        private void clearFinishedClick(object sender, RoutedEventArgs e){
            foreach(ClipJob j in jobs.Where(j => j.IsFinished).ToList()){jobs.Remove(j);}
        }

        private void optionChanged(object sender, RoutedEventArgs e){
            if(!IsLoaded){return;}
            saveSettings();
            if(ReferenceEquals(sender, PreciseCheck)){updateCutInfo();}
        }

        //-------- audio tracks (volume/channels/effects, separate or mixed) --------

        private void setupAudio(MediaInfo info){
            List<StreamInfo> audio = info.Streams.Where(s => s.CodecType == "audio").ToList();

            suspendAudioEvents = true;
            try{
                foreach(TrackSettings t in tracks){t.Changed -= onAudioChanged;}
                tracks.Clear();
                for(int i = 0; i < audio.Count; i++){
                    StreamInfo s = audio[i];
                    string label = $"{i + 1}. {s.DisplayName ?? $"Track {i + 1}"}";
                    string chans = s.Channels switch {1 => "mono", 2 => "stereo", > 2 => $"{s.Channels} ch", _ => ""};
                    TrackSettings t = new TrackSettings(i, label, $"{s.CodecName?.ToUpperInvariant()} {chans}".Trim(), s.Channels);
                    t.Changed += onAudioChanged;
                    tracks.Add(t);
                }
                SeparateRadio.IsChecked = true;
            }finally{suspendAudioEvents = false;}

            updateAudioState();
        }

        private void clearAudio(){
            foreach(TrackSettings t in tracks){t.Changed -= onAudioChanged;}
            tracks.Clear();
            updateAudioState();
        }

        private AudioPlan? currentPlan(){
            if(info == null || tracks.Count != info.AudioTrackCount){return null;}
            return new AudioPlan { MixDown = MixDownRadio.IsChecked == true, Tracks = tracks.Select(t => t.toPlan()).ToList() };
        }

        private bool AudioChanged => currentPlan() is { IsPassthrough: false };

        private void onAudioChanged(){
            if(suspendAudioEvents || !IsLoaded){return;}
            updateAudioState();
            if(current != null && durationSec > 0 && inSec <= 0.05 && outSec >= durationSec - 0.05){updateCutInfo();}
            fxDebounce.Stop();
            fxDebounce.Start();
        }

        private void updateAudioState(){
            AudioPlan? plan = currentPlan();
            AudioPlanText.Foreground = (Brush)FindResource("TextFg");
            if(current == null || plan == null){
                AudioPlanText.Text = "";
                AudioHeaderText.Text = "Audio tracks & effects";
                return;
            }
            int n = plan.Tracks.Count;
            if(n == 0){
                AudioPlanText.Text = "This clip has no audio.";
                AudioHeaderText.Text = "Audio tracks & effects (no audio in this clip)";
                return;
            }

            if(plan.IsPassthrough){
                AudioPlanText.Text = n == 1 ? "Audio: copied as-is (lossless)." : $"Audio: all {n} tracks copied as-is (lossless).";
                AudioHeaderText.Text = $"Audio tracks & effects ({n} track{(n == 1 ? "" : "s")}, untouched)";
            }else{
                List<string> parts = new List<string> { "Audio: " + plan.describe() + "." };
                if(plan.OutputTrackCount == 0){parts.Add("The trimmed clip will have NO audio.");}else if(plan.MixDown){parts.Add("Mixing can't be undone once the original is replaced.");}
                if(plan.Tracks.Any(t => t.Keep && !t.IsUntouched) || plan.MixDown){parts.Add("Changed audio is re-encoded (AAC 256 kbps).");}
                AudioPlanText.Text = string.Join(" ", parts);
                AudioHeaderText.Text = $"Audio tracks & effects ({n} track{(n == 1 ? "" : "s")}, CHANGED)";
                if(plan.MixDown || plan.Tracks.Any(t => !t.Keep)){AudioPlanText.Foreground = (Brush)FindResource("WarnFg");}
            }
        }

        private string? buildPreviewGraph(){
            AudioPlan? plan = currentPlan();
            if(plan == null){return null;}
            return AudioGraph.buildPreview(plan);
        }

        private void audioOutputChanged(object sender, RoutedEventArgs e) => onAudioChanged();

        private void audioExpanderChanged(object sender, RoutedEventArgs e){
            if(IsLoaded){settings.AudioPanelOpen = AudioExpander.IsExpanded;}
        }

        private void resetAudioClick(object sender, RoutedEventArgs e){
            suspendAudioEvents = true;
            try{
                foreach(TrackSettings t in tracks){
                    t.Volume = 1;
                    t.Keep = true;
                    t.Channel = ChannelMode.AsRecorded;
                    t.Effects.Clear();
                }
                SeparateRadio.IsChecked = true;
            }finally{suspendAudioEvents = false;}
            onAudioChanged();
        }

        private void volumeTextMouseDown(object sender, MouseButtonEventArgs e){
            if((sender as FrameworkElement)?.DataContext is TrackSettings t){t.Volume = 1;}
        }

        private void channelClick(object sender, RoutedEventArgs e){
            if(sender is not Button b || b.DataContext is not TrackSettings t){return;}
            ContextMenu menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach(ChannelMode mode in Enum.GetValues<ChannelMode>()){
                MenuItem item = new MenuItem { Header = Effects.channelName(mode), IsCheckable = true, IsChecked = mode == t.Channel };
                item.ToolTip = mode switch{
                    ChannelMode.MonoMix => "One channel, heard in both ears. Use for a mic or track that sounds lopsided.",
                    ChannelMode.LeftOnly => "One channel made from the left side only (e.g. a mic that only records on the left).",
                    ChannelMode.RightOnly => "One channel made from the right side only.",
                    _ => "Keep the track as it was recorded.",
                };
                item.Click += (_, __) => t.Channel = mode;
                menu.Items.Add(item);
            }
            menu.Opened += (_, __) => menuOpen++;
            menu.Closed += (_, __) => {menuOpen = Math.Max(0, menuOpen - 1); focusRoot();};
            menu.IsOpen = true;
        }

        private void addEffectClick(object sender, RoutedEventArgs e){
            if(sender is not Button b || b.DataContext is not TrackSettings t){return;}
            ContextMenu menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach(EffectDef def in Effects.All){
                MenuItem item = new MenuItem { Header = def.Name, ToolTip = def.Description };
                EffectKind kind = def.Kind;
                item.Click += (_, __) => t.addEffect(kind);
                menu.Items.Add(item);
            }
            menu.Opened += (_, __) => menuOpen++;
            menu.Closed += (_, __) => {menuOpen = Math.Max(0, menuOpen - 1); focusRoot();};
            menu.IsOpen = true;
        }

        private void moveEffectLeftClick(object sender, RoutedEventArgs e){
            if((sender as FrameworkElement)?.DataContext is EffectSettings fx){fx.Owner.moveEffect(fx, -1);}
        }

        private void moveEffectRightClick(object sender, RoutedEventArgs e){
            if((sender as FrameworkElement)?.DataContext is EffectSettings fx){fx.Owner.moveEffect(fx, +1);}
        }

        private void removeEffectClick(object sender, RoutedEventArgs e){
            if((sender as FrameworkElement)?.DataContext is EffectSettings fx){fx.Owner.Effects.Remove(fx);}
        }

        private void nextChoiceClick(object sender, RoutedEventArgs e){
            if((sender as FrameworkElement)?.DataContext is EffectParam p){p.nextChoice();}
        }

        private void reapplyPreviewGraph(){
            if(current == null || info == null){return;}
            //double pos = playSec;
            double pos = Mpv.getTimePosSeconds();
            string? graph = buildPreviewGraph();
            if(graph == appliedGraph){return;}
            appliedGraph = graph;
            //changing the graph on a playing file glitches the video so just reload at the same spot
            Mpv.load(current.FullPath, graph, pos, Mpv.IsPaused);
        }

        private void saveSettings(){
            settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
            settings.PreciseCut = PreciseCheck.IsChecked == true;
            settings.UseRecycleBin = RecycleCheck.IsChecked == true;
            settings.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true;
            settings.AutoPlay = AutoPlayCheck.IsChecked == true;
            settings.AudioPanelOpen = AudioExpander.IsExpanded;
            settings.save();
        }

        //-------- keyboard / mouse --------

        private void windowPreviewKeyDown(object sender, KeyEventArgs e){
            if(menuOpen > 0){return;}//a popup menu is open, arrows/enter belong to it
            //while typing a time only enter (commit) + escape (revert) do stuff, del/enter must never hit the clip actions from a textbox
            if(Keyboard.FocusedElement is TextBox tb){
                if(e.Key == Key.Enter){commitTextBox(tb); focusRoot(); e.Handled = true;}else if(e.Key == Key.Escape){updateMarkerTexts(); focusRoot(); e.Handled = true;}
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            if((mods & ModifierKeys.Control) != 0){
                if(e.Key == Key.O){showOpenFolderDialog(); e.Handled = true;}
                return;
            }
            if((mods & ModifierKeys.Alt) != 0){return;}
            bool shift = (mods & ModifierKeys.Shift) != 0;

            switch(e.Key){
                case Key.Space: togglePlay(); break;
                case Key.I: setIn(Mpv.getTimePosSeconds()); break;
                case Key.O: setOut(Mpv.getTimePosSeconds()); break;
                case Key.Left: if(current != null){Mpv.seekRelative(shift ? -1 : -5);} break;
                case Key.Right: if(current != null){Mpv.seekRelative(shift ? 1 : 5);} break;
                case Key.Home: seekTo(inSec); break;
                case Key.Up: case Key.P: case Key.PageUp: navigate(-1); break;
                case Key.Down: case Key.N: case Key.PageDown: navigate(+1); break;
                case Key.Enter:
                    if(!e.IsRepeat){trimCurrent();}
                    break;
                case Key.Delete:
                    if(!e.IsRepeat){_ = deleteCurrentAsync();}
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void windowPreviewMouseDown(object sender, MouseButtonEventArgs e){
            if(e.OriginalSource is DependencyObject d && findAncestor<TextBox>(d) == null){
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {if(menuOpen == 0){focusRoot();}}));
            }
        }

        protected override void OnActivated(EventArgs e){
            base.OnActivated(e);
            if(Keyboard.FocusedElement is not TextBox){focusRoot();}
        }

        private void focusRoot(){
            if(Keyboard.FocusedElement is TextBox tb){commitTextBox(tb);}
            RootGrid.Focus();
            Keyboard.Focus(RootGrid);
        }

        private static T? findAncestor<T>(DependencyObject? d) where T : DependencyObject{
            while(d != null){
                if(d is T t){return t;}
                //visual vs logical tree bs, Run/text elements arent Visuals so GetParent throws on them
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private async void onClosing(object? sender, CancelEventArgs e){
            if(!closeConfirmed){
                int busy = jobs.Count(j => !j.IsFinished);
                if(busy > 0){
                    e.Cancel = true;
                    MessageBoxResult r = MessageBox.Show(this,
                        $"{busy} clip(s) are still being trimmed.\n\nQuit anyway? Unfinished trims are cancelled and those originals are left untouched.",
                        "ClipMixer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if(r != MessageBoxResult.Yes){return;}

                    shutdownCts.Cancel();
                    status("Cancelling...");
                    try{if(jobWorker != null){await jobWorker.WaitAsync(TimeSpan.FromSeconds(15));}}catch{}
                    closeConfirmed = true;
                    Close();
                    return;
                }
            }

            scanCts?.Cancel();
            shutdownCts.Cancel();
            saveSettings();
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
            if(!has){KeyframeMarker.Visibility = Visibility.Collapsed; return;}

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

            if(KeyframeMarker.Visibility == Visibility.Visible && KeyframeMarker.Tag is double k){
                Canvas.SetLeft(KeyframeMarker, xFromTime(k) - 1);
                Canvas.SetTop(KeyframeMarker, TrackTop - 6);
            }

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
