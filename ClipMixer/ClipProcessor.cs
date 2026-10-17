using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClipMixer{
    public sealed class TrimRequest{
        public string SourcePath { get; init; } = "";
        public double InSec { get; init; }
        public double OutSec { get; init; }

        //true = reencode h264 so it starts exactly at In, false = lossless copy from the keyframe before In
        public bool PreciseCut { get; init; }

        public AudioPlan? Audio { get; init; }//null = copy every track as is

        public bool UseRecycleBin { get; init; } = true;
    }

    public sealed class TrimResult{
        public double CutStartSec { get; init; }
        public double CutEndSec { get; init; }
        public double OutputDurationSec { get; init; }
        public long OutputSizeBytes { get; init; }
        public List<string> Warnings { get; init; } = new();
        public string LogPath { get; init; } = "";
    }

    public sealed class VerificationException : Exception{
        public VerificationException(string message) : base(message){}
    }

    //export to temp -> verify it fully decodes -> swap w/ original -> verify again -> only then recycle the original
    //anything failing before the swap leaves the original alone, failing during the swap puts it back
    public sealed class ClipProcessor{
        public const string TrimTagValue = "ClipMixer trimmed";//goes in the comment tag, used to skip clips already trimmed
        public const string TempMarker = ".clipmixer-tmp";
        public const string BackupMarker = ".clipmixer-orig";

        private readonly FfmpegRunner runner;
        private readonly string logDir;

        public Func<string, Task>? ReleaseFileAsync { get; set; }//so the player lets go of the file before rename

        public Action<string>? AfterExportHook { get; set; }
        public Action<string>? BeforeReplaceHook { get; set; }

        public ClipProcessor(FfmpegRunner runner, string logDir){
            this.runner = runner;
            this.logDir = logDir;
        }

        public static bool isMp4Family(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

        public static bool isTrimTag(string? comment) =>
            comment != null && comment.Trim().Equals(TrimTagValue, StringComparison.OrdinalIgnoreCase);

        public async Task<TrimResult> trimAndReplaceAsync(TrimRequest req, Action<string, double>? progress, CancellationToken ct){
            string src = Path.GetFullPath(req.SourcePath);
            if(!File.Exists(src)){throw new FileNotFoundException("Clip no longer exists.", src);}

            string dir = Path.GetDirectoryName(src)!;
            string name = Path.GetFileNameWithoutExtension(src);
            string ext = Path.GetExtension(src);
            string tmp = Path.Combine(dir, name + TempMarker + ext);

            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName(name)}.log");
            using StreamWriter log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            void Log(string s){try{log.WriteLine(s);}catch{}}

            List<string> warnings = new List<string>();
            Log($"Source: {src}");
            Log($"Request: in={req.InSec:0.###} out={req.OutSec:0.###} precise={req.PreciseCut} recycle={req.UseRecycleBin}");
            Log($"Audio: {(req.Audio == null || req.Audio.IsPassthrough ? "copy all tracks" : req.Audio.describe())}");

            bool tempIsOurs = false;
            try{
                progress?.Invoke("Analyzing", 0);
                MediaInfo srcInfo = await runner.probeAsync(src, ct).ConfigureAwait(false);
                double srcDur = srcInfo.DurationSeconds;
                Log($"Source duration={srcDur:0.###} start={srcInfo.StartTime:0.###} video={srcInfo.VideoTrackCount} audio={srcInfo.AudioTrackCount} codec={srcInfo.VideoCodec} fps={srcInfo.Fps:0.###}");
                if(srcDur <= 0){throw new Exception("Couldn't read the clip's duration (file may be damaged).");}

                double inSec = Math.Clamp(req.InSec, 0, srcDur);
                double outSec = Math.Clamp(req.OutSec, 0, srcDur);
                if(outSec - inSec < 0.1){throw new Exception("Out must be at least 0.1s after In.");}
                bool toEnd = outSec >= srcDur - 0.01;

                //ffmpeg drops packets w/ dts before seekRef, so w/ b-frames it has to sit on the keyframes dts. this dts shit took forever to figure out
                double cutStart = inSec, seekRef = inSec;
                if(!req.PreciseCut && inSec > 0){
                    (double kPts, double? kDts) = await runner.getKeyframeInfoAsync(src, inSec, srcInfo.StartTime, ct).ConfigureAwait(false);
                    cutStart = kPts;
                    seekRef = kPts <= 0 ? 0 : Math.Max(0, Math.Min(kPts, kDts ?? kPts - 0.25) - 0.0005);
                    Log($"Keyframe at/before In: pts={kPts:0.######} dts={(kDts.HasValue ? kDts.Value.ToString("0.######") : "N/A")} seek={seekRef:0.######}");
                }
                double cutEnd = toEnd ? srcDur : outSec;
                double expectedDur = cutEnd - seekRef;

                int expVideo = srcInfo.VideoTrackCount;//all video tracks get kept
                AudioPlan? plan = req.Audio != null && req.Audio.IsPassthrough ? null : req.Audio;
                //ffmpeg drops a track w/ no audio inside the cut (mic turned off earlier etc) so write those as silence instead
                HashSet<int> emptyTracks = new HashSet<int>();
                List<StreamInfo> srcAudioStreams = srcInfo.Streams.Where(s => s.CodecType == "audio").ToList();
                for(int i = 0; i < srcAudioStreams.Count; i++){
                    StreamInfo s = srcAudioStreams[i];
                    if(!s.Duration.HasValue){continue;}
                    double st = (s.StartTime ?? srcInfo.StartTime) - srcInfo.StartTime;
                    if(Math.Min(cutEnd, st + s.Duration.Value) - Math.Max(seekRef, st) < 0.05){
                        emptyTracks.Add(i);
                        Log($"Audio track {i + 1} has no audio inside the cut; it's written as silence.");
                    }
                }
                if(plan == null && emptyTracks.Count > 0){plan = AudioPlan.passthrough(srcInfo.AudioTrackCount);}
                (string? audioGraph, List<OutputAudioTrack> audioOut) = AudioGraph.buildExport(plan, srcInfo.AudioTrackCount, emptyTracks);

                ct.ThrowIfCancellationRequested();
                if(File.Exists(tmp)){await FileOps.deleteAsync(tmp, false, ct).ConfigureAwait(false);}
                tempIsOurs = true;

                //filtered audio gets padded w/ silence so it needs a real end even when cutting to the end
                List<string> args = buildTrimArgs(src, tmp, seekRef, toEnd && audioGraph == null ? (double?)null : expectedDur,
                    req.PreciseCut, audioGraph, audioOut, srcInfo, expVideo > 0);
                Log("ffmpeg " + string.Join(" ", args.Select(quote)));
                progress?.Invoke("Exporting", 0);

                ProcessResult res = await runner.runFfmpegAsync(args, expectedDur,
                    p => progress?.Invoke("Exporting", p), null, line => Log("  " + line), ct).ConfigureAwait(false);
                Log($"ffmpeg exit={res.ExitCode}");
                if(res.ExitCode != 0 || !File.Exists(tmp)){
                    throw new Exception($"Export failed (ffmpeg exit {res.ExitCode}): {lastLines(res.StdErr, 6)}");
                }

                AfterExportHook?.Invoke(tmp);

                //make sure the temp actually plays all the way
                progress?.Invoke("Verifying", 0);
                MediaInfo outInfo = await verifyAsync(tmp, src, srcInfo, seekRef, cutEnd, expVideo, audioOut,
                    warnings, Log, p => progress?.Invoke("Verifying", p), ct).ConfigureAwait(false);
                long tmpSize = new FileInfo(tmp).Length;
                FileOps.flushToDisk(tmp);//verified bytes have to be on disk before the original goes anywhere

                //swap, original -> backup then temp -> original name
                ct.ThrowIfCancellationRequested();
                progress?.Invoke("Replacing", 0);
                string backup = FileOps.uniquePath(dir, name + BackupMarker, ext);
                FileInfo srcFi = new FileInfo(src);
                DateTime created = srcFi.CreationTimeUtc, modified = srcFi.LastWriteTimeUtc;

                if(ReleaseFileAsync != null){await ReleaseFileAsync(src).ConfigureAwait(false);}
                BeforeReplaceHook?.Invoke(src);

                try{
                    await FileOps.retryAsync(() => File.Move(src, backup), ct).ConfigureAwait(false);
                }catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException){
                    throw new Exception($"Original is in use by another program, left unchanged: {ex.Message}");
                }
                Log($"Original moved to backup: {backup}");

                //original is at backup now, dont let cancel happen mid swap
                try{
                    await FileOps.retryAsync(() => File.Move(tmp, src), CancellationToken.None).ConfigureAwait(false);
                    tempIsOurs = false;
                }catch(Exception ex){
                    Log($"Moving temp into place failed: {ex.Message}. Restoring original.");
                    await restoreBackupAsync(backup, src, Log).ConfigureAwait(false);
                    throw new Exception($"Couldn't swap in the trimmed file, original restored. ({ex.Message})");
                }

                try{
                    File.SetCreationTimeUtc(src, created);
                    File.SetLastWriteTimeUtc(src, modified);
                }catch(Exception ex){warnings.Add($"Couldn't preserve file dates: {ex.Message}");}

                //check the file thats now at the original path
                try{
                    FileInfo finalFi = new FileInfo(src);
                    if(!finalFi.Exists || finalFi.Length != tmpSize){
                        throw new VerificationException("Replaced file size doesn't match the verified export.");
                    }
                    MediaInfo finalInfo = await runner.probeAsync(src, CancellationToken.None).ConfigureAwait(false);
                    if(!isTrimTag(finalInfo.getTag("comment")) || Math.Abs(finalInfo.DurationSeconds - outInfo.DurationSeconds) > 0.05){
                        throw new VerificationException("Replaced file doesn't match the verified export.");
                    }
                }catch(Exception ex){
                    Log($"Post-replace check failed: {ex.Message}. Rolling back.");
                    string failed = FileOps.uniquePath(dir, name + TempMarker, ext);
                    try{
                        await FileOps.retryAsync(() => File.Move(src, failed), CancellationToken.None).ConfigureAwait(false);
                        await restoreBackupAsync(backup, src, Log).ConfigureAwait(false);
                        try{await FileOps.deleteAsync(failed, false).ConfigureAwait(false);}catch{}
                    }catch(Exception rex){
                        throw new Exception($"Post-replace check and rollback both failed. Original is safe at: {backup} ({rex.Message})");
                    }
                    throw new Exception($"Post-replace check failed, original restored. ({ex.Message})");
                }

                progress?.Invoke("Removing original", 0);
                try{
                    try{
                        await FileOps.deleteAsync(backup, req.UseRecycleBin, CancellationToken.None).ConfigureAwait(false);
                        Log(req.UseRecycleBin ? "Original sent to Recycle Bin." : "Original deleted.");
                    }catch(NoRecycleBinException){
                        //trimmed file is verified + in place and they asked for a replace, so just delete it
                        await FileOps.deleteAsync(backup, false, CancellationToken.None).ConfigureAwait(false);
                        warnings.Add("No Recycle Bin on this drive, original deleted permanently.");
                        Log("No Recycle Bin on this drive; original deleted permanently.");
                    }
                }catch(Exception ex){
                    warnings.Add($"Trimmed, but couldn't remove the original. It's still at {backup} ({ex.Message})");
                    Log("Backup removal failed: " + ex.Message);
                }

                foreach(string w in warnings){Log("WARNING: " + w);}
                Log("SUCCESS");
                progress?.Invoke("Done", 1);

                return new TrimResult
                {
                    CutStartSec = cutStart,
                    CutEndSec = cutEnd,
                    OutputDurationSec = outInfo.DurationSeconds,
                    OutputSizeBytes = tmpSize,
                    Warnings = warnings,
                    LogPath = logPath,
                };
            }catch(Exception ex){
                Log("FAILED: " + ex);
                throw new ClipJobException(ex is OperationCanceledException ? "Cancelled." : ex.Message, logPath, ex);
            }finally{
                if(tempIsOurs){
                    try{await FileOps.deleteAsync(tmp, false, CancellationToken.None).ConfigureAwait(false);}catch(Exception ex){Log("Temp cleanup failed: " + ex.Message);}
                }
            }
        }

        private static async Task restoreBackupAsync(string backup, string src, Action<string> log){
            try{
                //File.Move wont overwrite so this cant clobber whatevers at src
                await FileOps.retryAsync(() => File.Move(backup, src), CancellationToken.None, attempts: 40).ConfigureAwait(false);
                log("Original restored.");
            }catch(Exception ex){
                log($"RESTORE FAILED: {ex.Message}");
                throw new Exception($"Couldn't move the original back. It's safe at: {backup} ({ex.Message})");
            }
        }

        //start = output side seek in source time (0 = beginning), duration null = copy to the end
        public List<string> buildTrimArgs(string src, string dst, double start, double? duration, bool preciseCut,
            string? audioGraph, IReadOnlyList<OutputAudioTrack> audioOut, MediaInfo srcInfo, bool hasVideo){
            List<string> a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-progress", "pipe:1", "-nostats" };

            //fast input seek to a few sec before, then output seek drops everything before start. input seek alone lets copied audio start before the video
            double coarse = start > 0 ? Math.Max(0, start - 5) : 0;
            if(coarse > 0){a.AddRange(new[] { "-ss", FfmpegRunner.sec(coarse) });}
            a.AddRange(new[] { "-i", src });
            if(start > 0){a.AddRange(new[] { "-ss", FfmpegRunner.sec(start - coarse) });}
            if(duration.HasValue){a.AddRange(new[] { "-t", FfmpegRunner.sec(duration.Value) });}

            if(hasVideo){
                a.AddRange(new[] { "-map", "0:V" });
                if(preciseCut){
                    a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p" });
                }else{
                    a.AddRange(new[] { "-c:v", "copy" });
                }
            }

            //track names set by hand because the mp4 muxer drops them even on a plain copy
            if(audioGraph != null){a.AddRange(new[] { "-filter_complex", audioGraph });}
            List<StreamInfo> srcAudio = srcInfo.Streams.Where(s => s.CodecType == "audio").ToList();
            for(int k = 0; k < audioOut.Count; k++){
                OutputAudioTrack o = audioOut[k];
                if(o.IsCopy){
                    a.AddRange(new[] { "-map", $"0:a:{o.Sources[0]}", $"-c:a:{k}", "copy" });
                }else{
                    a.AddRange(new[] { "-map", $"[{o.FilterLabel}]", $"-c:a:{k}", "aac", $"-b:a:{k}", "256k" });
                }
                //mp4 "title" is what ffprobe/nvidia show as "name"
                StreamInfo? first = srcAudio.ElementAtOrDefault(o.Sources[0]);
                string? title = o.Sources.Length > 1
                    ? "Mix: " + string.Join(" + ", o.Sources.Select(i => srcAudio.ElementAtOrDefault(i)?.DisplayName ?? $"Track {i + 1}"))
                    : first?.DisplayName;
                if(title != null){a.AddRange(new[] { $"-metadata:s:a:{k}", "title=" + title });}
                if(first?.getTag("language") is {} lang){a.AddRange(new[] { $"-metadata:s:a:{k}", "language=" + lang });}
            }

            //todo: shift chapters instead of just dropping them
            a.AddRange(new[]
            {
                "-map_metadata", "0",
                "-map_chapters", "-1",
                "-metadata", "comment=" + TrimTagValue,
                "-avoid_negative_ts", "make_zero",
            });
            if(isMp4Family(dst)){a.AddRange(new[] { "-movflags", "+faststart" });}
            a.Add(dst);
            return a;
        }

        //checks tracks, length, every stream covers the cut, then a full decode of all video + audio
        public async Task<MediaInfo> verifyAsync(string file, string src, MediaInfo srcInfo, double cutStart, double cutEnd,
            int expVideo, IReadOnlyList<OutputAudioTrack> audioOut, List<string> warnings, Action<string> log,
            Action<double>? progress, CancellationToken ct){
            double sourceStartSpread = startSpread(srcInfo);
            int expAudio = audioOut.Count;
            double expected = cutEnd - cutStart;

            MediaInfo info;
            try{info = await runner.probeAsync(file, ct).ConfigureAwait(false);}catch(Exception ex){throw new VerificationException($"Exported file can't be read: {ex.Message}");}

            log($"Verify: duration={info.DurationSeconds:0.###} (expected {expected:0.###}) video={info.VideoTrackCount} audio={info.AudioTrackCount}");

            if(info.VideoTrackCount != expVideo){
                throw new VerificationException($"Export has {info.VideoTrackCount} video track(s), expected {expVideo}.");
            }
            if(info.AudioTrackCount != expAudio){
                throw new VerificationException($"Export has {info.AudioTrackCount} audio track(s), expected {expAudio}.");
            }
            if(!isTrimTag(info.getTag("comment"))){
                throw new VerificationException("Export is missing the trimmed marker tag.");
            }

            double frame = info.Fps > 1 ? 1.0 / info.Fps : 1.0 / 30;
            //double tol = 0.5;
            double tol = Math.Max(0.5, 3 * frame);
            if(Math.Abs(info.DurationSeconds - expected) > tol){
                throw new VerificationException($"Export is {info.DurationSeconds:0.00}s long, expected {expected:0.00}s.");
            }

            //each track only has to cover as much of the cut as its source track did (mic that starts late etc)
            List<StreamInfo> srcV = srcInfo.Streams.Where(s => s.CodecType == "video" && !s.IsAttachedPicture).ToList();
            List<StreamInfo> srcA = srcInfo.Streams.Where(s => s.CodecType == "audio").ToList();
            List<StreamInfo> outV = info.Streams.Where(s => s.CodecType == "video" && !s.IsAttachedPicture).ToList();
            List<StreamInfo> outA = info.Streams.Where(s => s.CodecType == "audio").ToList();

            double Coverage(StreamInfo? s){
                if(s?.Duration == null){return expected;}
                double st = (s.StartTime ?? srcInfo.StartTime) - srcInfo.StartTime;
                return Math.Clamp(Math.Min(cutEnd, st + s.Duration.Value) - Math.Max(cutStart, st), 0, expected);
            }
            void CheckStream(StreamInfo o, double need, string what){
                if(o.Duration.HasValue && o.Duration.Value < need - tol){
                    throw new VerificationException($"{what} is only {o.Duration.Value:0.00}s long, original has {need:0.00}s in this section.");
                }
            }

            //one decode pass per track type, each w/ the length it has to reach
            List<(string Map, double Need, string What)> passes = new List<(string Map, double Need, string What)>();
            double videoNeed = 0;
            for(int k = 0; k < outV.Count; k++){
                double need = k < srcV.Count ? Coverage(srcV[k]) : expected;
                CheckStream(outV[k], need, $"Video track {k + 1}");
                videoNeed = Math.Max(videoNeed, need);
            }
            if(outV.Count > 0){passes.Add(("0:V", videoNeed, "video"));}
            for(int k = 0; k < outA.Count; k++){
                double need = audioOut[k].Sources.Select(i => Coverage(srcA.ElementAtOrDefault(i))).DefaultIfEmpty(expected).Max();//mix has to last as long as its longest input
                CheckStream(outA[k], need, $"Audio track {k + 1}");
                passes.Add(($"0:a:{k}", need, $"audio track {k + 1}"));
            }

            //video + audio have to start together unless the original was already offset like that
            double spread = startSpread(info);
            double allowed = Math.Max(0.35, sourceStartSpread + 0.1);
            log($"Verify: stream start spread={spread:0.###}s (allowed {allowed:0.###}s)");
            if(spread > allowed){
                throw new VerificationException($"Video and audio start {spread:0.00}s apart in the export.");
            }

            //seperate passes because ffmpegs progress follows the shortest stream, a mic that ends early would hide a short video
            List<string> allErrors = new List<string>();
            for(int i = 0; i < passes.Count; i++){
                (string map, double need, string what) = passes[i];
                int idx = i;
                DecodeResult d = await decodeCheckAsync(file, null, null, new[] { map }, Math.Max(need, 0.001),
                    p => progress?.Invoke((idx + p) / passes.Count), log, ct).ConfigureAwait(false);
                if(d.ExitCode != 0){
                    throw new VerificationException($"Export's {what} doesn't decode cleanly (exit {d.ExitCode}): {firstErrors(d.Errors)}");
                }
                if(d.DecodedSeconds < need - tol){
                    throw new VerificationException($"Decoding the {what} stopped at {d.DecodedSeconds:0.00}s of {need:0.00}s.");
                }
                allErrors.AddRange(d.Errors);
            }

            if(allErrors.Count > 0){
                //some recordings are glitchy already, only ok if the original has at least as many errors there
                log("Export had decode errors; checking the same section of the original for comparison.");
                DecodeResult srcDecode = await decodeCheckAsync(src, cutStart, cutEnd, new[] { "0:V?", "0:a?" }, expected, null, log, ct).ConfigureAwait(false);
                if(srcDecode.Errors.Count < allErrors.Count){
                    throw new VerificationException($"Export has decode errors that aren't in the original: {firstErrors(allErrors)}");
                }
                warnings.Add($"Original has {srcDecode.Errors.Count} decode warning(s) in this section too, export matches it.");
            }

            return info;
        }

        private sealed class DecodeResult{
            public int ExitCode;
            public double DecodedSeconds;
            public List<string> Errors = new();
        }

        private async Task<DecodeResult> decodeCheckAsync(string file, double? start, double? end, string[] maps, double expected,
            Action<double>? progress, Action<string> log, CancellationToken ct){
            List<string> args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
            if(start.HasValue && start.Value > 0){args.AddRange(new[] { "-ss", FfmpegRunner.sec(start.Value + 0.0005) });}
            if(end.HasValue){args.AddRange(new[] { "-to", FfmpegRunner.sec(end.Value) });}
            args.AddRange(new[] { "-i", file });
            foreach(string m in maps){args.AddRange(new[] { "-map", m });}
            args.AddRange(new[] { "-f", "null", "-" });

            DecodeResult result = new DecodeResult();
            List<string> errors = new List<string>();
            double maxT = 0;
            ProcessResult res = await runner.runFfmpegAsync(args, expected, progress, t => {if(t > maxT){maxT = t;}},
                line => {lock(errors){errors.Add(line);}}, ct).ConfigureAwait(false);

            result.ExitCode = res.ExitCode;
            result.DecodedSeconds = maxT;
            result.Errors = errors.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            log($"Decode check {Path.GetFileName(file)} [{string.Join(" ", maps)}]: exit={res.ExitCode} decoded={maxT:0.###}s errors={result.Errors.Count}");
            foreach(string e in result.Errors.Take(20)){log("  " + e);}
            return result;
        }

        public static double startSpread(MediaInfo info){
            List<double> starts = info.Streams
                .Where(s => s.CodecType is "video" or "audio" && !s.IsAttachedPicture && s.StartTime.HasValue)
                .Select(s => s.StartTime!.Value).ToList();
            return starts.Count < 2 ? 0 : starts.Max() - starts.Min();
        }

        private static string firstErrors(List<string> errors) =>
            errors.Count == 0 ? "(no details)" : string.Join(" | ", errors.Take(3));

        private static string lastLines(string text, int n){
            List<string> lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count == 0 ? "(no output)" : string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - n)));
        }

        private static string quote(string s) => s.Contains(' ') || s.Contains(';') ? $"\"{s}\"" : s;

        private static string safeName(string s){
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach(char c in s){sb.Append(bad.Contains(c) ? '_' : c);}
            return sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
        }
    }

    public sealed class ClipJobException : Exception{
        public string LogPath { get; }
        public ClipJobException(string message, string logPath, Exception inner) : base(message, inner) => LogPath = logPath;
    }
}
