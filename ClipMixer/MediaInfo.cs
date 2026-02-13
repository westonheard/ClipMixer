#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ClipMixer{
    public sealed class StreamInfo{
        public int Index { get; init; }
        public string CodecType { get; init; } = "";
        public string? CodecName { get; init; }
        public double? Duration { get; init; }//null when the container doesnt say (mkv)
        public double? StartTime { get; init; }
        public bool IsAttachedPicture { get; init; }
        public int Channels { get; init; }
        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

        public string? getTag(string key) => Tags.TryGetValue(key.ToLowerInvariant(), out string? v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public string? DisplayName => getTag("title") ?? getTag("name");//nvidia writes "name", everything else "title"
    }

    public sealed class MediaInfo{
        public double DurationSeconds { get; set; }//measured from StartTime

        public double StartTime { get; init; }//format start_time, player pos + ffmpeg -ss are relative to this

        public string? VideoCodec { get; init; }
        public double Fps { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public IReadOnlyList<StreamInfo> Streams { get; init; } = Array.Empty<StreamInfo>();

        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();//lowercased because mkv writes them uppercase

        public int AudioTrackCount => Streams.Count(s => s.CodecType == "audio");
        public int VideoTrackCount => Streams.Count(s => s.CodecType == "video" && !s.IsAttachedPicture);

        public string? getTag(string key) => Tags.TryGetValue(key.ToLowerInvariant(), out string? v) ? v : null;

        public static MediaInfo fromFfprobeJson(string json){
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            double duration = 0, start = 0;
            Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if(root.TryGetProperty("format", out JsonElement fmt)){
                if(fmt.TryGetProperty("duration", out JsonElement durEl) && tryGetDouble(durEl, out double d)){
                    duration = d;
                }
                if(fmt.TryGetProperty("start_time", out JsonElement stEl) && tryGetDouble(stEl, out double st0)){
                    start = st0;
                }
                readTags(fmt, tags);
            }

            string? vcodec = null;
            double fps = 0;
            int w = 0, h = 0;
            List<StreamInfo> streams = new List<StreamInfo>();

            if(root.TryGetProperty("streams", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array){
                foreach(JsonElement s in arr.EnumerateArray()){
                    string type = getString(s, "codec_type") ?? "";
                    bool attached = s.TryGetProperty("disposition", out JsonElement disp) &&
                                    disp.TryGetProperty("attached_pic", out JsonElement ap) &&
                                    ap.ValueKind == JsonValueKind.Number && ap.GetInt32() != 0;

                    double? sdur = null, sstart = null;
                    if(s.TryGetProperty("duration", out JsonElement sd) && tryGetDouble(sd, out double sdv)){
                        sdur = sdv;
                    }
                    if(s.TryGetProperty("start_time", out JsonElement st) && tryGetDouble(st, out double stv)){
                        sstart = stv;
                    }

                    Dictionary<string, string> stags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    readTags(s, stags);
                    streams.Add(new StreamInfo
                    {
                        Channels = s.TryGetProperty("channels", out JsonElement chEl) && chEl.ValueKind == JsonValueKind.Number ? chEl.GetInt32() : 0,
                        Tags = stags,
                        Index = s.TryGetProperty("index", out JsonElement idx) && idx.ValueKind == JsonValueKind.Number ? idx.GetInt32() : streams.Count,
                        CodecType = type,
                        CodecName = getString(s, "codec_name"),
                        Duration = sdur,
                        StartTime = sstart,
                        IsAttachedPicture = attached,
                    });

                    if(type == "video" && !attached && vcodec == null){
                        vcodec = getString(s, "codec_name");
                        if(s.TryGetProperty("width", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.Number){w = wEl.GetInt32();}
                        if(s.TryGetProperty("height", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.Number){h = hEl.GetInt32();}

                        //avg_frame_rate is the real one for vfr captures, r_frame_rate is just a fallback
                        //fps = parseFraction(getString(s, "r_frame_rate"));
                        fps = parseFraction(getString(s, "avg_frame_rate"));
                        if(fps <= 0){fps = parseFraction(getString(s, "r_frame_rate"));}
                    }
                }
            }

            //length relative to start_time. mp4 has per stream durations (use the furthest end),
            //mkv only has format.duration which is the absolute end time
            List<double> ends = streams.Where(s => s.Duration.HasValue && (s.CodecType is "video" or "audio"))
                .Select(s => (s.StartTime ?? start) + s.Duration!.Value).ToList();
            if(ends.Count > 0){duration = ends.Max() - start;}else if(duration > 0 && start > 0){duration -= start;}

            return new MediaInfo
            {
                DurationSeconds = Math.Max(0, duration),
                StartTime = start,
                VideoCodec = vcodec,
                Fps = fps,
                Width = w,
                Height = h,
                Streams = streams,
                Tags = tags,
            };
        }

        private static void readTags(JsonElement owner, Dictionary<string, string> into){
            if(!owner.TryGetProperty("tags", out JsonElement t) || t.ValueKind != JsonValueKind.Object){return;}
            foreach(JsonProperty p in t.EnumerateObject()){
                if(p.Value.ValueKind == JsonValueKind.String){
                    into[p.Name.ToLowerInvariant()] = p.Value.GetString() ?? "";
                }
            }
        }

        private static string? getString(JsonElement el, string name) =>
            el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double parseFraction(string? frac){
            if(string.IsNullOrWhiteSpace(frac)){return 0;}
            string[] parts = frac.Split('/');
            if(parts.Length != 2){return 0;}
            if(!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)){return 0;}
            if(!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)){return 0;}
            if(Math.Abs(b) < 1e-9){return 0;}
            return a / b;
        }

        private static bool tryGetDouble(JsonElement el, out double value){
            value = 0;
            //ffprobe always uses '.' so never parse w/ the users culture
            if(el.ValueKind == JsonValueKind.String){
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            if(el.ValueKind == JsonValueKind.Number){
                return el.TryGetDouble(out value);
            }
            return false;
        }
    }
}
