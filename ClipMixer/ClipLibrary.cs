using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClipMixer{
    public sealed class ClipEntry : INotifyPropertyChanged{
        public string FullPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string FileName => Path.GetFileName(FullPath);
        public string Folder => Path.GetDirectoryName(RelativePath) ?? "";
        public long SizeBytes { get; init; }
        public string SizeText => SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} GB" : $"{SizeBytes / (double)(1L << 20):0} MB";

        public double DurationSeconds { get; init; }//seconds, 0 means unknown (couldnt read the file)
        public string DurationText {
            get{
                if(DurationSeconds <= 0){return "?:??";}
                TimeSpan t = TimeSpan.FromSeconds(DurationSeconds);
                return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
            }
        }

        public DateTime ModifiedUtc { get; init; }
        public string DateText => ModifiedUtc == default ? "" : ModifiedUtc.ToLocalTime().ToString("MMM d yyyy  HH:mm");

        public bool IsTrimmed { get; init; }

        private string? note;
        public string? Note {
            get => note;
            set{note = value; onChanged(); onChanged(nameof(HasNote));}
        }
        public bool HasNote => !string.IsNullOrEmpty(note);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void onChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class ScanSummary{
        public int Candidates;
        public int Untrimmed;
        public int AlreadyTrimmed;
    }

    public static class ClipLibrary{
        //todo: maybe add .avi/.webm too, havent tested those w/ the trim stuff
        public static readonly HashSet<string> VideoExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".mkv" };

        public static bool isWorkFile(string path){
            string n = Path.GetFileName(path);
            return n.Contains(ClipProcessor.TempMarker, StringComparison.OrdinalIgnoreCase) ||
                   n.Contains(ClipProcessor.BackupMarker, StringComparison.OrdinalIgnoreCase);
        }

        //the old version saved as ..._trimmed.mp4 w/ no tag, so go by name for those
        public static bool looksTrimmedByName(string path) =>
            Path.GetFileNameWithoutExtension(path).EndsWith("_trimmed", StringComparison.OrdinalIgnoreCase);

        public static List<string> enumerateVideos(string folder, bool recurse){
            EnumerationOptions opts = new EnumerationOptions{
                RecurseSubdirectories = recurse,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            };
            return Directory.EnumerateFiles(folder, "*", opts)
                .Where(p => VideoExtensions.Contains(Path.GetExtension(p)))
                .ToList();
        }

        //onClip fires from a worker thread per clip so the list fills in while its still scanning
        public static async Task<ScanSummary> scanAsync(
            string folder, bool recurse, FfmpegRunner runner, ISet<string> activePaths,
            Action<ClipEntry> onClip, Action<int, int>? onProgress, CancellationToken ct){
            ScanSummary summary = new ScanSummary();

            List<string> all = await Task.Run(() => enumerateVideos(folder, recurse), ct).ConfigureAwait(false);

            List<string> candidates = all
                .Where(p => !isWorkFile(p) && !looksTrimmedByName(p) && !activePaths.Contains(p))
                .OrderBy(p => Path.GetRelativePath(folder, p), StringComparer.OrdinalIgnoreCase)
                .ToList();
            summary.Candidates = candidates.Count;
            summary.AlreadyTrimmed = all.Count(p => !isWorkFile(p) && looksTrimmedByName(p));

            int done = 0;
            onProgress?.Invoke(0, candidates.Count);

            foreach(string path in candidates){
                ct.ThrowIfCancellationRequested();
                FileInfo fi = new FileInfo(path);
                if(!fi.Exists){continue;}

                bool trimmed;
                double duration;
                try{
                    MediaInfo info = await runner.probeAsync(path, ct).ConfigureAwait(false);
                    trimmed = ClipProcessor.isTrimTag(info.getTag("comment"));
                    duration = info.DurationSeconds;
                }catch(OperationCanceledException){throw;}catch{
                    //broken file, still list it so it can be deleted
                    trimmed = false;
                    duration = 0;
                }

                if(trimmed){summary.AlreadyTrimmed++;}else{summary.Untrimmed++;}
                onClip(new ClipEntry{
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(folder, path),
                    SizeBytes = fi.Length,
                    DurationSeconds = duration,
                    ModifiedUtc = fi.LastWriteTimeUtc,
                    IsTrimmed = trimmed,
                });
                onProgress?.Invoke(++done, candidates.Count);
            }

            return summary;
        }
    }
}
