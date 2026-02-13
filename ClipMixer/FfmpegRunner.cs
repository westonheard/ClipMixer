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
