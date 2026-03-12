using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClipMixer{
    public sealed class ProcessResult{
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
    }

    //wrapper for ffmpeg/ffprobe, args go through ArgumentList (no hand quoting) + output read as utf8 so non ascii file names dont break
    public sealed class FfmpegRunner{
        public string FfmpegPath { get; }
        public string FfprobePath { get; }

        public FfmpegRunner(string ffmpegPath, string ffprobePath){
            FfmpegPath = ffmpegPath;
            FfprobePath = ffprobePath;
        }

        //bundled tools next to the exe first, then whatevers on PATH
        public static FfmpegRunner createDefault(){
            string tools = Path.Combine(AppContext.BaseDirectory, "tools");
            return new FfmpegRunner(
                resolveTool(Path.Combine(tools, "ffmpeg.exe"), "ffmpeg.exe"),
                resolveTool(Path.Combine(tools, "ffprobe.exe"), "ffprobe.exe"));
        }

        private static string resolveTool(string bundled, string exeName){
            if(File.Exists(bundled)){return bundled;}
            foreach(string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)){
                try{
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if(File.Exists(candidate)){return candidate;}
                }catch{}//junk PATH entry
            }
            return bundled;//gives a clear not found error later
        }

        //todo: cache probe results, this gets called a lot for the same file
        public async Task<MediaInfo> probeAsync(string path, CancellationToken ct = default){
            ProcessResult res = await runAsync(FfprobePath, new[]
            {
                "-hide_banner", "-v", "error",
                "-print_format", "json",
                "-show_format", "-show_streams",
                "-i", path
            }, null, null, ct, TimeSpan.FromSeconds(60));

            if(res.ExitCode != 0){
                throw new Exception($"ffprobe failed (exit {res.ExitCode}): {firstLines(res.StdErr, 5)}");
            }

            MediaInfo info = MediaInfo.fromFfprobeJson(res.StdOut);
            if(info.DurationSeconds <= 0){
                //unfinished recordings (like crash recovered mkv) dont have a duration in the header
                //so measure it by stream copying the whole thing to nowhere
                double maxT = 0;
                ProcessResult scan = await runAsync(FfmpegPath, new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
                    "-i", path, "-map", "0:V?", "-map", "0:a?", "-c", "copy", "-f", "null", "-"
                }, line => {
                    if(line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us > 0){
                        maxT = Math.Max(maxT, us / 1_000_000.0);
                    }
                }, null, ct, TimeSpan.FromMinutes(5));
                if(scan.ExitCode == 0 && maxT > 0){info.DurationSeconds = maxT;}
            }
            return info;
        }

        //last keyframe at/before sec, lossless cut can only start clean on one. times relative to startTime like mpv/-ss, ffprobe packet times are absolute
        public async Task<double> getKeyframeAtOrBeforeAsync(string path, double sec, double startTime = 0, CancellationToken ct = default) =>
            (await getKeyframeInfoAsync(path, sec, startTime, ct).ConfigureAwait(false)).Pts;

        //same thing but also gives the dts, w/ b-frames dts is earlier than pts and ffmpegs output seek drops packets by dts
        public async Task<(double Pts, double? Dts)> getKeyframeInfoAsync(string path, double sec, double startTime = 0, CancellationToken ct = default){
            if(sec <= 0){return (0, 0);}
            double abs = sec + startTime;

            //try a window before the time first (cheap), then scan from the start if nothing found
            foreach(double windowStart in new[] { sec - 30, double.NegativeInfinity }){
                double absStart = windowStart + startTime;
                string intervals = windowStart > 0 && absStart > 0
                    ? $"{FfmpegRunner.sec(absStart)}%{FfmpegRunner.sec(abs + 0.5)}"
                    : $"%{FfmpegRunner.sec(abs + 0.5)}";

                ProcessResult res = await runAsync(FfprobePath, new[]
                {
                    "-hide_banner", "-v", "error",
                    "-select_streams", "v:0",
                    "-read_intervals", intervals,
                    "-show_entries", "packet=pts_time,dts_time,flags",
                    "-of", "csv=p=0",
                    "-i", path
                }, null, null, ct, TimeSpan.FromSeconds(120));

                if(res.ExitCode != 0){
                    throw new Exception($"ffprobe keyframe scan failed (exit {res.ExitCode}): {firstLines(res.StdErr, 5)}");
                }

                //lines look like "3.236567,3.236567,K__", dts can be N/A
                double best = -1;
                double? bestDts = null;
                foreach(string raw in res.StdOut.Split('\n')){
                    string[] parts = raw.Trim().Split(',');
                    if(parts.Length < 3 || !parts[2].Contains('K')){continue;}
                    if(!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsAbs)){continue;}
                    double pts = ptsAbs - startTime;
                    if(pts <= sec + 0.0005 && pts > best){//little bit of slack, ffprobe rounds pts to microseconds
                        best = pts;
                        bestDts = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dts) ? dts - startTime : null;
                    }
                }

                if(best >= 0){return (best, bestDts);}
                if(double.IsNegativeInfinity(windowStart)){break;}
            }

            return (0, 0);
        }

        //caller has to pass -progress pipe:1 already, progress is 0..1 off expectedDuration. doesnt throw on non zero exit
        public Task<ProcessResult> runFfmpegAsync(
            IReadOnlyList<string> args,
            double expectedDuration,
            Action<double>? onProgress,
            Action<double>? onOutTime,
            Action<string>? onStderrLine,
            CancellationToken ct){
            return runAsync(FfmpegPath, args, line => {
                //-progress gives key=value lines, out_time_us is microseconds (can be N/A)
                if(line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0){
                    double t = us / 1_000_000.0;
                    onOutTime?.Invoke(t);
                    if(onProgress != null && expectedDuration > 0){
                        onProgress(Math.Clamp(t / expectedDuration, 0, 1));
                    }
                }
            }, onStderrLine, ct, null);
        }

        public static async Task<ProcessResult> runAsync(
            string exe,
            IEnumerable<string> args,
            Action<string>? onStdoutLine,
            Action<string>? onStderrLine,
            CancellationToken ct,
            TimeSpan? timeout){
            if(!File.Exists(exe)){
                throw new FileNotFoundException($"{Path.GetFileName(exe)} not found. Expected at: {exe}", exe);
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach(string a in args){psi.ArgumentList.Add(a);}

            using Process p = new Process { StartInfo = psi };
            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            p.Start();

            //read both pipes to EOF not just till HasExited, otherwise the end of the output gets lost
            Task readOut = Task.Run(async () => {
                string? line;
                while((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null){
                    lock(stdout){stdout.AppendLine(line);}
                    try{onStdoutLine?.Invoke(line);}catch{}//callback blowing up shouldnt kill the read
                }
            });
            Task readErr = Task.Run(async () => {
                string? line;
                while((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null){
                    lock(stderr){stderr.AppendLine(line);}
                    try{onStderrLine?.Invoke(line);}catch{}
                }
            });

            using CancellationTokenSource timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try{
                await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }catch(OperationCanceledException){
                try{if(!p.HasExited){p.Kill(entireProcessTree: true);}}catch{}
                try{await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);}catch{}
                if(ct.IsCancellationRequested){throw new OperationCanceledException(ct);}
                throw new TimeoutException($"{Path.GetFileName(exe)} timed out");
            }

            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);

            return new ProcessResult
            {
                ExitCode = p.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString(),
            };
        }

        //peak 0..1 per slice for every audio track in one pass, lined up on the clip timeline (late mic gets silence in front)
        public async Task<float[][]> readAudioPeaksAsync(string path, int trackCount, double duration, int bucketsPerSecond, CancellationToken ct){
            if(trackCount <= 0 || duration <= 0){return Array.Empty<float[]>();}
            //const int rate = 4000;
            const int rate = 8000;
            int perBucket = Math.Max(1, rate / bucketsPerSecond);
            int buckets = (int)Math.Ceiling(duration * bucketsPerSecond) + 1;

            StringBuilder graph = new StringBuilder();
            for(int i = 0; i < trackCount; i++){
                graph.Append($"[0:a:{i}]aresample={rate}:async=1:first_pts=0,aformat=sample_fmts=s16:channel_layouts=stereo,apad[w{i}];");
            }
            if(trackCount == 1){graph.Append("[w0]anull[wout]");}else{
                for(int i = 0; i < trackCount; i++){graph.Append($"[w{i}]");}
                graph.Append($"amerge=inputs={trackCount}[wout]");
            }

            List<string> args = new List<string>
            {
                "-hide_banner", "-nostdin", "-v", "error", "-vn", "-sn", "-dn", "-i", path,
                "-filter_complex", graph.ToString(), "-map", "[wout]", "-t", sec(duration),
                "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1",
            };

            float[][] peaks = new float[trackCount][];
            for(int i = 0; i < trackCount; i++){peaks[i] = new float[buckets];}
            //every track is stereo (mono gets upmixed) so a mic thats only on one side still shows full level
            long frame = 0;//sample frames so far (L+R per track)
            int carry = 0;//leftover bytes from a partial frame
            int frameBytes = trackCount * 4;
            byte[] partial = new byte[frameBytes];

            //reads dont line up w/ frames so partial ones get stitched together, idk why this was such a pain
            void Consume(ReadOnlySpan<byte> data){
                int pos = 0;
                if(carry > 0){
                    int need = frameBytes - carry;
                    if(data.Length < need){data.CopyTo(partial.AsSpan(carry)); carry += data.Length; return;}
                    data[..need].CopyTo(partial.AsSpan(carry));
                    AddFrame(partial);
                    pos = need;
                    carry = 0;
                }
                while(data.Length - pos >= frameBytes){
                    AddFrame(data.Slice(pos, frameBytes));
                    pos += frameBytes;
                }
                if(pos < data.Length){data[pos..].CopyTo(partial); carry = data.Length - pos;}
            }

            void AddFrame(ReadOnlySpan<byte> f){
                long b = frame++ / perBucket;
                if(b >= buckets){return;}
                for(int t = 0; t < trackCount; t++){
                    int l = Math.Abs((int)(short)(f[4 * t] | (f[4 * t + 1] << 8)));
                    int r = Math.Abs((int)(short)(f[4 * t + 2] | (f[4 * t + 3] << 8)));
                    float a = Math.Max(l, r) / 32768f;
                    if(a > peaks[t][b]){peaks[t][b] = a;}
                }
            }

            ProcessResult res = await runRawAsync(FfmpegPath, args, Consume, ct).ConfigureAwait(false);
            if(res.ExitCode != 0){
                throw new Exception($"waveform decode failed (exit {res.ExitCode}): {firstLines(res.StdErr, 3)}");
            }
            return peaks;
        }

        //like runAsync but stdout comes back as raw bytes (for pcm)
        public static async Task<ProcessResult> runRawAsync(string exe, IEnumerable<string> args, SpanAction onStdout, CancellationToken ct){
            if(!File.Exists(exe)){
                throw new FileNotFoundException($"{Path.GetFileName(exe)} not found. Expected at: {exe}", exe);
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach(string a in args){psi.ArgumentList.Add(a);}

            using Process p = new Process { StartInfo = psi };
            StringBuilder stderr = new StringBuilder();
            p.Start();

            Task readErr = Task.Run(async () => {
                string? line;
                while((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null){
                    lock(stderr){stderr.AppendLine(line);}
                }
            });

            //pipe reads dont always notice the token, killing the process forces EOF
            using CancellationTokenRegistration reg = ct.Register(() => {try{if(!p.HasExited){p.Kill(entireProcessTree: true);}}catch{}});
            try{
                Stream stream = p.StandardOutput.BaseStream;
                byte[] buf = new byte[64 * 1024];
                int n;
                while((n = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0){
                    onStdout(buf.AsSpan(0, n));
                }
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }catch(OperationCanceledException){
                try{if(!p.HasExited){p.Kill(entireProcessTree: true);}}catch{}
                try{await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);}catch{}
                throw;
            }

            await readErr.ConfigureAwait(false);
            return new ProcessResult { ExitCode = p.ExitCode, StdErr = stderr.ToString() };
        }

        public delegate void SpanAction(ReadOnlySpan<byte> data);

        //seconds for ffmpeg args, always invariant culture
        public static string sec(double seconds) =>
            Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);

        private static readonly Regex tsRegex = new(
            @"^\s*(?:(?:(?<h>[0-9]{1,4}):)?(?<m>[0-9]{1,2}):)?(?<s>[0-9]{1,7}(?:[.,][0-9]{1,9})?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        //takes hh:mm:ss.fff, mm:ss, m:ss.f or plain seconds like 75.5, never throws
        public static bool tryParseTs(string? s, out TimeSpan ts){
            ts = TimeSpan.Zero;
            if(string.IsNullOrWhiteSpace(s)){return false;}

            Match m = tsRegex.Match(s);
            if(!m.Success){return false;}

            int h = 0, min = 0;
            if(m.Groups["h"].Success && !int.TryParse(m.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h)){return false;}
            if(m.Groups["m"].Success && !int.TryParse(m.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out min)){return false;}
            if(!double.TryParse(m.Groups["s"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)){
                return false;
            }

            if(m.Groups["m"].Success && sec >= 60){return false;}//if theres minutes, seconds has to be under 60

            double total = h * 3600.0 + min * 60.0 + sec;
            if(!double.IsFinite(total) || total < 0 || total > 100 * 3600){return false;}

            ts = TimeSpan.FromSeconds(total);
            return true;
        }

        public static string formatTs(TimeSpan ts){
            if(ts < TimeSpan.Zero){ts = TimeSpan.Zero;}
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        public static string formatTs(double seconds) => formatTs(TimeSpan.FromSeconds(Math.Max(0, seconds)));

        public static string firstLines(string text, int n) =>
            string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(n));
    }
}
