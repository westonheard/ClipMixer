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
    }

    public sealed class TrimResult{
        public double CutStartSec { get; init; }
        public double CutEndSec { get; init; }
        public double OutputDurationSec { get; init; }
        public long OutputSizeBytes { get; init; }
        public List<string> Warnings { get; init; } = new();
        public string LogPath { get; init; } = "";
    }

    //exports In..Out to name_trimmed.ext next to the original, the original isnt touched
    public sealed class ClipProcessor{
        private readonly FfmpegRunner runner;
        private readonly string logDir;

        public ClipProcessor(FfmpegRunner runner, string logDir){
            this.runner = runner;
            this.logDir = logDir;
        }

        public static bool isMp4Family(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

        public async Task<TrimResult> trimAsync(TrimRequest req, Action<string, double>? progress, CancellationToken ct){
            string src = Path.GetFullPath(req.SourcePath);
            if(!File.Exists(src)){throw new FileNotFoundException("Clip no longer exists.", src);}

            string dir = Path.GetDirectoryName(src)!;
            string name = Path.GetFileNameWithoutExtension(src);
            string ext = Path.GetExtension(src);
            string dst = FileOps.uniquePath(dir, name + "_trimmed", ext);

            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName(name)}.log");
            using StreamWriter log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            void Log(string s){try{log.WriteLine(s);}catch{}}

            List<string> warnings = new List<string>();
            Log($"Source: {src}");
            Log($"Output: {dst}");
            Log($"Request: in={req.InSec:0.###} out={req.OutSec:0.###}");

            bool dstIsOurs = false;
            try{
                progress?.Invoke("Analyzing", 0);
                MediaInfo srcInfo = await runner.probeAsync(src, ct).ConfigureAwait(false);
                double srcDur = srcInfo.DurationSeconds;
                Log($"Source duration={srcDur:0.###} start={srcInfo.StartTime:0.###} video={srcInfo.VideoTrackCount} audio={srcInfo.AudioTrackCount} codec={srcInfo.VideoCodec} fps={srcInfo.Fps:0.###}");
                if(srcDur <= 0){throw new Exception("Could not read the clip's duration (file may be damaged).");}

                double inSec = Math.Clamp(req.InSec, 0, srcDur);
                double outSec = Math.Clamp(req.OutSec, 0, srcDur);
                if(outSec - inSec < 0.1){throw new Exception("Out must be at least 0.1s after In.");}
                bool toEnd = outSec >= srcDur - 0.01;

                //ffmpeg drops packets w/ dts before seekRef, so w/ b-frames it has to sit on the keyframes dts. this dts shit took forever to figure out
                double cutStart = inSec, seekRef = inSec;
                if(inSec > 0){
                    (double kPts, double? kDts) = await runner.getKeyframeInfoAsync(src, inSec, srcInfo.StartTime, ct).ConfigureAwait(false);
                    cutStart = kPts;
                    seekRef = kPts <= 0 ? 0 : Math.Max(0, Math.Min(kPts, kDts ?? kPts - 0.25) - 0.0005);
                    Log($"Keyframe at/before In: pts={kPts:0.######} dts={(kDts.HasValue ? kDts.Value.ToString("0.######") : "N/A")} seek={seekRef:0.######}");
                }
                double cutEnd = toEnd ? srcDur : outSec;
                double expectedDur = cutEnd - seekRef;

                int expVideo = srcInfo.VideoTrackCount;//all video tracks get kept

                ct.ThrowIfCancellationRequested();
                dstIsOurs = true;

                List<string> args = buildTrimArgs(src, dst, seekRef, toEnd ? (double?)null : expectedDur, srcInfo, expVideo > 0);
                Log("ffmpeg " + string.Join(" ", args.Select(quote)));
                progress?.Invoke("Exporting", 0);

                ProcessResult res = await runner.runFfmpegAsync(args, expectedDur,
                    p => progress?.Invoke("Exporting", p), null, line => Log("  " + line), ct).ConfigureAwait(false);
                Log($"ffmpeg exit={res.ExitCode}");
                if(res.ExitCode != 0 || !File.Exists(dst)){
                    throw new Exception($"Export failed (ffmpeg exit {res.ExitCode}): {lastLines(res.StdErr, 6)}");
                }

                MediaInfo outInfo = await runner.probeAsync(dst, ct).ConfigureAwait(false);
                long outSize = new FileInfo(dst).Length;
                dstIsOurs = false;

                foreach(string w in warnings){Log("WARNING: " + w);}
                Log("SUCCESS");
                progress?.Invoke("Done", 1);

                return new TrimResult
                {
                    CutStartSec = cutStart,
                    CutEndSec = cutEnd,
                    OutputDurationSec = outInfo.DurationSeconds,
                    OutputSizeBytes = outSize,
                    Warnings = warnings,
                    LogPath = logPath,
                };
            }catch(Exception ex){
                Log("FAILED: " + ex);
                throw new ClipJobException(ex is OperationCanceledException ? "Cancelled." : ex.Message, logPath, ex);
            }finally{
                if(dstIsOurs){
                    try{await FileOps.deleteAsync(dst, CancellationToken.None).ConfigureAwait(false);}catch(Exception ex){Log("Cleanup failed: " + ex.Message);}
                }
            }
        }

        //start = output side seek in source time (0 = beginning), duration null = copy to the end
        public List<string> buildTrimArgs(string src, string dst, double start, double? duration, MediaInfo srcInfo, bool hasVideo){
            List<string> a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-progress", "pipe:1", "-nostats" };

            //fast input seek to a few sec before, then output seek drops everything before start. input seek alone lets copied audio start before the video
            double coarse = start > 0 ? Math.Max(0, start - 5) : 0;
            if(coarse > 0){a.AddRange(new[] { "-ss", FfmpegRunner.sec(coarse) });}
            a.AddRange(new[] { "-i", src });
            if(start > 0){a.AddRange(new[] { "-ss", FfmpegRunner.sec(start - coarse) });}
            if(duration.HasValue){a.AddRange(new[] { "-t", FfmpegRunner.sec(duration.Value) });}

            if(hasVideo){a.AddRange(new[] { "-map", "0:V", "-c:v", "copy" });}

            //every audio track copied as is. track names set by hand because the mp4 muxer drops them even on a plain copy
            List<StreamInfo> srcAudio = srcInfo.Streams.Where(s => s.CodecType == "audio").ToList();
            for(int k = 0; k < srcAudio.Count; k++){
                a.AddRange(new[] { "-map", $"0:a:{k}", $"-c:a:{k}", "copy" });
                //mp4 "title" is what ffprobe/nvidia show as "name"
                string? title = srcAudio[k].DisplayName;
                if(title != null){a.AddRange(new[] { $"-metadata:s:a:{k}", "title=" + title });}
                if(srcAudio[k].getTag("language") is {} lang){a.AddRange(new[] { $"-metadata:s:a:{k}", "language=" + lang });}
            }

            //todo: shift chapters instead of just dropping them
            a.AddRange(new[]
            {
                "-map_metadata", "0",
                "-map_chapters", "-1",
                "-avoid_negative_ts", "make_zero",
            });
            if(isMp4Family(dst)){a.AddRange(new[] { "-movflags", "+faststart" });}
            a.Add(dst);
            return a;
        }

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
