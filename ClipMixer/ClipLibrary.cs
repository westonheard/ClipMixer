using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    public enum ClipSortMode{Name, Newest, Oldest, Longest, Shortest, Largest}

    public static class ClipSort{
        public static string label(ClipSortMode m) => m switch{
            ClipSortMode.Newest => "Newest first",
            ClipSortMode.Oldest => "Oldest first",
            ClipSortMode.Longest => "Longest first",
            ClipSortMode.Shortest => "Shortest first",
            ClipSortMode.Largest => "Biggest file first",
            _ => "Name (A-Z)",
        };

        //unknown lengths always go at the bottom, doesnt matter which way ur sorting
        private static int unknownLast(ClipEntry a, ClipEntry b) => (a.DurationSeconds <= 0).CompareTo(b.DurationSeconds <= 0);

        public static IComparer<ClipEntry> comparer(ClipSortMode m) => Comparer<ClipEntry>.Create((a, b) => {
            int c = m switch{
                ClipSortMode.Newest => b.ModifiedUtc.CompareTo(a.ModifiedUtc),
                ClipSortMode.Oldest => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
                ClipSortMode.Longest => unknownLast(a, b) != 0 ? unknownLast(a, b) : b.DurationSeconds.CompareTo(a.DurationSeconds),
                ClipSortMode.Shortest => unknownLast(a, b) != 0 ? unknownLast(a, b) : a.DurationSeconds.CompareTo(b.DurationSeconds),
                ClipSortMode.Largest => b.SizeBytes.CompareTo(a.SizeBytes),
                _ => 0,
            };
            return c != 0 ? c : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
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
            string folder, bool recurse, FfmpegRunner runner, ClipCache cache, ISet<string> activePaths,
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

                if(!cache.tryGet(path, fi.Length, fi.LastWriteTimeUtc, out bool trimmed, out double duration)){
                    try{
                        MediaInfo info = await runner.probeAsync(path, ct).ConfigureAwait(false);
                        trimmed = ClipProcessor.isTrimTag(info.getTag("comment"));
                        duration = info.DurationSeconds;
                        cache.set(path, fi.Length, fi.LastWriteTimeUtc, trimmed, duration);
                    }catch(OperationCanceledException){throw;}catch{
                        //broken file, still list it so it can be deleted, but dont cache it
                        trimmed = false;
                        duration = 0;
                    }
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

            cache.save();
            return summary;
        }
    }

    //remembers checked files (path + size + mtime) so rescans are instant, trimmed tag is in the file itself anyway
    public sealed class ClipCache{
        private sealed class Entry{
            public long Size { get; set; }
            public long MtimeTicks { get; set; }
            public bool Trimmed { get; set; }
            public double Duration { get; set; }
        }

        private readonly string file;
        private readonly object lock_ = new();
        private Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        public ClipCache(string file){
            this.file = file;
            try{
                if(File.Exists(file)){
                    entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file))
                               is {} d ? new Dictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase) : entries;
                }
            }catch{}//corrupt cache, start fresh
        }

        public bool tryGet(string path, long size, DateTime mtimeUtc, out bool trimmed, out double duration){
            lock(lock_){
                //old versions didnt store length (0), treat as unknown so it gets rechecked once
                if(entries.TryGetValue(path, out Entry? e) && e.Size == size && e.MtimeTicks == mtimeUtc.Ticks && e.Duration > 0){
                    trimmed = e.Trimmed;
                    duration = e.Duration;
                    return true;
                }
            }
            trimmed = false;
            duration = 0;
            return false;
        }

        public void set(string path, long size, DateTime mtimeUtc, bool trimmed, double duration){
            lock(lock_){entries[path] = new Entry { Size = size, MtimeTicks = mtimeUtc.Ticks, Trimmed = trimmed, Duration = duration };}
        }

        public void markTrimmed(string path, double duration){
            try{
                FileInfo fi = new FileInfo(path);
                if(fi.Exists){set(path, fi.Length, fi.LastWriteTimeUtc, true, duration);}
            }catch{}
        }

        public void remove(string path){
            lock(lock_){entries.Remove(path);}
        }

        public void save(){
            try{
                string json;
                lock(lock_){
                    foreach(string k in entries.Keys.Where(k => !File.Exists(k)).ToList()){entries.Remove(k);}
                    json = JsonSerializer.Serialize(entries);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, file, overwrite: true);
            }catch{}//its just a cache, whatever
        }
    }
}
