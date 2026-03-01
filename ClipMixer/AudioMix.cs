using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ClipMixer{
    public static class Effects{
        //filters go here, just number formatting for now
        internal static string n(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public sealed record TrackPlan(int Index, string Label, double Volume, bool Keep, int Channels = 2){
        public bool IsUntouched => Keep && Math.Abs(Volume - 1) < 1e-9;

        //mono goes to both sides at full level, aformat upmix made it 3db quieter
        //public string StereoFilter => "aformat=channel_layouts=stereo";
        public string StereoFilter => Channels == 1
            ? "pan=stereo|c0=c0|c1=c0" : "aformat=channel_layouts=stereo";

        public List<string> filters(){
            List<string> f = new List<string>();
            if(Math.Abs(Volume - 1) > 1e-9){f.Add($"volume={ClipMixer.Effects.n(Volume)}");}
            return f;
        }

        public string describe(){
            List<string> bits = new List<string>();
            if(Math.Abs(Volume - 1) > 1e-9){bits.Add($"{Volume * 100:0}%");}
            return bits.Count == 0 ? "unchanged" : string.Join(", ", bits);
        }
    }

    public sealed record OutputAudioTrack(int[] Sources, string? FilterLabel){
        public bool IsCopy => FilterLabel == null;
    }

    public sealed class AudioPlan{
        public IReadOnlyList<TrackPlan> Tracks { get; init; } = Array.Empty<TrackPlan>();

        public bool IsPassthrough => Tracks.All(t => t.IsUntouched);

        public int OutputTrackCount => Tracks.Count(t => t.Keep);

        public static AudioPlan passthrough(int trackCount) => new(){
            Tracks = Enumerable.Range(0, trackCount)
                .Select(i => new TrackPlan(i, $"Track {i + 1}", 1, true))
                .ToList(),
        };

        public string describe(){
            if(Tracks.Count == 0){return "no audio";}
            return string.Join("; ", Tracks.Select(t => t.Keep ? $"{t.Label}: {(t.IsUntouched ? "copied" : t.describe())}" : $"{t.Label}: REMOVED"));
        }
    }

    public static class AudioGraph{
        //game + mic both loud = clipping when summed, so limit it
        private const string MixLimiter = "alimiter=limit=0.98:level=0:latency=1";

        private static string chain(string input, string output, IEnumerable<string> filters){
            List<string> list = filters.Where(f => !string.IsNullOrEmpty(f)).ToList();
            return $"[{input}]{(list.Count == 0 ? "anull" : string.Join(",", list))}[{output}]";
        }

        public static (string? FilterComplex, List<OutputAudioTrack> Outputs) buildExport(AudioPlan? plan, int sourceAudioCount){
            plan ??= AudioPlan.passthrough(sourceAudioCount);
            if(plan.Tracks.Count != sourceAudioCount){
                throw new InvalidOperationException($"Audio settings are for {plan.Tracks.Count} track(s) but the clip has {sourceAudioCount}.");
            }

            List<string> chains = new List<string>();
            List<OutputAudioTrack> outputs = new List<OutputAudioTrack>();
            List<TrackPlan> kept = plan.Tracks.Where(t => t.Keep).ToList();

            foreach(TrackPlan t in kept){
                if(t.IsUntouched){
                    outputs.Add(new OutputAudioTrack(new[] { t.Index }, null));
                    continue;
                }
                string label = $"aout{t.Index}";
                chains.Add(chain($"0:a:{t.Index}", label, t.filters()));
                outputs.Add(new OutputAudioTrack(new[] { t.Index }, label));
            }
            return (chains.Count == 0 ? null : string.Join(";", chains), outputs);
        }

        //mpv lavfi-complex graph, [aid1].. in and [ao] out. mpv only plays 1 stream so everything gets mixed. null = play normal
        public static string? buildPreview(AudioPlan plan){
            if(plan.Tracks.Count == 0){return null;}
            if(plan.Tracks.Count == 1 && plan.IsPassthrough){return null;}

            List<TrackPlan> audible = plan.Tracks.Where(t => t.Keep).ToList();
            if(audible.Count == 0){return "[aid1]volume=0[ao]";}
            if(audible.Count == 1){
                TrackPlan t = audible[0];
                List<string> f = t.filters();
                return chain($"aid{t.Index + 1}", "ao", f);
            }

            List<string> chains = audible.Select(t => chain($"aid{t.Index + 1}", $"p{t.Index}",
                t.filters().Append(t.StereoFilter))).ToList();
            chains.Add(string.Concat(audible.Select(t => $"[p{t.Index}]")) +
                       $"amix=inputs={audible.Count}:normalize=0:duration=longest,{MixLimiter}[ao]");
            return string.Join(";", chains);
        }
    }

    public abstract class Observable : INotifyPropertyChanged{
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool set<T>(ref T field, T value, [CallerMemberName] string? name = null){
            if(EqualityComparer<T>.Default.Equals(field, value)){return false;}
            field = value;
            raise(name);
            return true;
        }
    }

    public sealed class TrackSettings : Observable{
        public const double MaxVolume = 3;

        private double volume = 1;
        private bool keep = true;

        public TrackSettings(int index, string label, string info, int channels){
            Index = index;
            Label = label;
            Info = info;
            Channels = channels;
        }

        public event Action? Changed;

        public int Index { get; }
        public string Label { get; }
        public string Info { get; }
        public int Channels { get; }

        public double Volume {
            get => volume;
            set{
                double v = Math.Clamp(Math.Round(value, 2), 0, MaxVolume);
                if(Math.Abs(v - 1) < 0.025){v = 1;}//snap to 100% so its easy to get back to unchanged
                if(set(ref volume, v)){raise(nameof(VolumeText)); notifyChanged();}
            }
        }

        public string VolumeText => volume <= 0 ? "0% (silent)"
            : $"{volume * 100:0}% ({(20 * Math.Log10(volume) >= 0 ? "+" : "")}{20 * Math.Log10(volume):0.0} dB)";

        public bool Keep {
            get => keep;
            set{if(set(ref keep, value)){notifyChanged();}}
        }

        public void notifyChanged() => Changed?.Invoke();

        public bool IsDefault => Math.Abs(volume - 1) < 1e-9 && keep;

        public TrackPlan toPlan() => new(Index, Label, volume, keep, Channels);
    }
}
