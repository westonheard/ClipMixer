using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ClipMixer{
    //all effects are plain ffmpeg filters, mpv uses the same libavfilter so preview + export sound the same

    public enum EffectKind{Overdrive, Bitcrusher, Waveshaper, Fuzz, VoiceIsolation, NoiseGate, Compressor}

    public enum ChannelMode{AsRecorded, MonoMix, LeftOnly, RightOnly}

    public sealed record ParamDef(string Key, string Label, double Min, double Max, double Default, double Step,
        string Unit = "", string[]? Choices = null){
        public bool IsChoice => Choices != null;

        public double clamp(double v){
            if(!double.IsFinite(v)){v = Default;}
            v = Math.Clamp(v, Min, Max);
            return Step > 0 ? Math.Clamp(Math.Round((v - Min) / Step) * Step + Min, Min, Max) : v;
        }

        public string format(double v){
            if(Choices != null){return Choices[Math.Clamp((int)Math.Round(v), 0, Choices.Length - 1)];}
            string num = v.ToString(Step >= 1 ? "0" : Step >= 0.1 ? "0.#" : "0.00", CultureInfo.CurrentCulture);
            return Unit.Length == 0 ? num : Unit.StartsWith(':') ? num + Unit : $"{num} {Unit}";
        }
    }

    public sealed record EffectDef(EffectKind Kind, string Name, string Description, ParamDef[] Params);

    public static class Effects{
        public static readonly IReadOnlyList<EffectDef> All = new[]{
            new EffectDef(EffectKind.Overdrive, "Overdrive", "Warm soft-clipping distortion.",
                new[] { new ParamDef("drive", "Drive", 0, 1, 0.5, 0.01) }),
            new EffectDef(EffectKind.Bitcrusher, "Bitcrusher", "Lo-fi: fewer bits and a lower sample rate.",
                new[]{
                    new ParamDef("bits", "Bits", 1, 16, 8, 1),
                    new ParamDef("downsample", "Downsample", 1, 50, 1, 1, "x"),
                }),
            new EffectDef(EffectKind.Waveshaper, "Waveshaper", "Shapes the waveform: soft, hard or sine curve.",
                new[]{
                    new ParamDef("amount", "Amount", 0, 1, 0.5, 0.01),
                    new ParamDef("type", "Curve", 0, 2, 0, 1, "", new[] { "Soft", "Hard", "Sine" }),
                }),
            new EffectDef(EffectKind.Fuzz, "Fuzz", "Heavy, lopsided clipping.",
                new[] { new ParamDef("gain", "Gain", 0, 1, 0.5, 0.01) }),
            new EffectDef(EffectKind.VoiceIsolation, "Voice isolation", "Cuts rumble and hiss outside the voice range and reduces steady background noise.",
                new[]{
                    new ParamDef("strength", "Strength", 0, 1, 0.5, 0.01),
                    new ParamDef("lowcut", "Low cut", 50, 1000, 120, 10, "Hz"),
                    new ParamDef("highcut", "High cut", 2000, 12000, 7000, 100, "Hz"),
                }),
            new EffectDef(EffectKind.NoiseGate, "Noise gate", "Silences the track while it's quieter than the threshold (keyboard, breathing, fans).",
                new[]{
                    new ParamDef("threshold", "Threshold", -70, -10, -40, 1, "dB"),
                    new ParamDef("attack", "Attack", 1, 100, 10, 1, "ms"),
                    new ParamDef("release", "Release", 10, 1000, 150, 10, "ms"),
                }),
            new EffectDef(EffectKind.Compressor, "Compressor", "Evens out the volume: turns down loud parts (shouting, explosions) above the threshold, then Make-up raises the whole track.",
                new[]{
                    new ParamDef("threshold", "Threshold", -60, 0, -24, 1, "dB"),
                    new ParamDef("ratio", "Ratio", 1, 20, 4, 0.5, ":1"),
                    new ParamDef("attack", "Attack", 1, 200, 10, 1, "ms"),
                    new ParamDef("release", "Release", 10, 1000, 200, 10, "ms"),
                    new ParamDef("makeup", "Make-up", 0, 24, 6, 0.5, "dB"),
                }),
        };

        public static EffectDef get(EffectKind kind) => All.First(d => d.Kind == kind);

        public static string filter(EffectKind kind, IReadOnlyDictionary<string, double> values){
            EffectDef def = get(kind);
            double P(string key){
                ParamDef pd = def.Params.First(p => p.Key == key);
                return pd.clamp(values.TryGetValue(key, out double v) ? v : pd.Default);
            }

            switch(kind){
                //all the curves are gain -> softclip -> makeup gain, scaled so full scale in = full scale out
                case EffectKind.Overdrive:{
                    //alg(x) = x / sqrt(1 + x^2), g 0.2 is basically clean, 15.8 is super hot
                    double g = 0.2 + 15.6 * Math.Pow(P("drive"), 1.5);
                    return shaped(g, "alg", Math.Sqrt(1 + g * g) / g);
                }
                case EffectKind.Bitcrusher:{
                    //acrushers own bits option didnt do anything u could hear so i round w/ aeval instead
                    //and only use acrusher for the downsample part. no , or : in the expr so no escaping needed
                    double q = Math.Pow(2, P("bits") - 1);
                    string f = $"aeval=exprs=round(val(ch)*{n(q)})/{n(q)}:channel_layout=same";
                    double ds = P("downsample");
                    return ds > 1 ? f + $",acrusher=bits=64:samples={n(ds)}:mix=1:aa=0:mode=lin" : f;
                }

                case EffectKind.Waveshaper:{
                    double a = P("amount");
                    switch((int)Math.Round(P("type"))){
                        case 1:
                            return shaped(1 / (1 - a * 0.9), "hard", 1);
                        case 2:{//sine, d stays under pi/2 or the curve folds back over
                            double d = 0.01 + a * 1.55;
                            return shaped(d, "sin", 1 / Math.Sin(d));
                        }
                        default:{
                            double k = a * 10 + 1;
                            return shaped(k, "tanh", 1 / Math.Tanh(k));
                        }
                    }
                }
                case EffectKind.Fuzz:{
                    //dc shift makes the clip lopsided so top + bottom clip different, then shift it back. took way too long to get this shit to sound right
                    double g = 1 + P("gain") * 20;
                    return $"volume={n(g)},dcshift=shift=0.2,asoftclip=type=hard,dcshift=shift=-0.2,volume=0.833";
                }
                case EffectKind.VoiceIsolation:{
                    double s = P("strength");
                    double lo = P("lowcut"), hi = Math.Max(P("highcut"), lo + 500);
                    return $"highpass=f={n(lo)},lowpass=f={n(hi)},afftdn=nr={n(6 + 34 * s)}:nf=-50:tn=1";
                }
                case EffectKind.Compressor:{
                    double thr = Math.Clamp(Math.Pow(10, P("threshold") / 20), 0.000976563, 1);
                    double makeup = Math.Clamp(Math.Pow(10, P("makeup") / 20), 1, 64);
                    return $"acompressor=threshold={n(thr)}:ratio={n(P("ratio"))}:attack={n(P("attack"))}:release={n(P("release"))}:makeup={n(makeup)}";
                }
                case EffectKind.NoiseGate:{
                    double thr = Math.Pow(10, P("threshold") / 20);
                    return $"agate=threshold={n(thr)}:ratio=10:range=0.001:attack={n(P("attack"))}:release={n(P("release"))}";
                }
            }
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        private static string shaped(double preGain, string curve, double postGain) =>
            $"volume={n(preGain)},asoftclip=type={curve},volume={n(postGain)}";

        public static string channelFilter(ChannelMode mode) => mode switch{
            ChannelMode.MonoMix => "pan=mono|c0=0.5*c0+0.5*c1",
            ChannelMode.LeftOnly => "pan=mono|c0=c0",
            ChannelMode.RightOnly => "pan=mono|c0=c1",
            _ => "",
        };

        public static string channelName(ChannelMode mode) => mode switch{
            ChannelMode.MonoMix => "Mono (left + right mixed)",
            ChannelMode.LeftOnly => "Mono (left side only)",
            ChannelMode.RightOnly => "Mono (right side only)",
            _ => "Stereo (as recorded)",
        };

        internal static string n(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public sealed record EffectPlan(EffectKind Kind, IReadOnlyDictionary<string, double> Values){
        public string describe() => Effects.get(Kind).Name;
    }

    //StartOffset = how late the track starts (mic started late), only the player uses it, export goes by timestamps
    public sealed record TrackPlan(int Index, string Label, double Volume, bool Keep, ChannelMode Channel, IReadOnlyList<EffectPlan> Effects,
        int Channels = 2, double StartOffset = 0){
        public bool IsUntouched => Keep && Math.Abs(Volume - 1) < 1e-9 && Channel == ChannelMode.AsRecorded && Effects.Count == 0;

        //mono goes to both sides at full level, aformat upmix made it 3db quieter
        //public string StereoFilter => "aformat=channel_layouts=stereo";
        public string StereoFilter => Channel != ChannelMode.AsRecorded || Channels == 1
            ? "pan=stereo|c0=c0|c1=c0" : "aformat=channel_layouts=stereo";

        public List<string> filters(){
            List<string> f = new List<string>();
            string ch = ClipMixer.Effects.channelFilter(Channel);
            if(ch.Length > 0){
                if(Channels > 2){f.Add("aformat=channel_layouts=stereo");}//pan only sees front l/r so fold 5.1 down first
                f.Add(ch);
            }
            foreach(EffectPlan e in Effects){f.Add(ClipMixer.Effects.filter(e.Kind, e.Values));}
            if(Math.Abs(Volume - 1) > 1e-9){f.Add($"volume={ClipMixer.Effects.n(Volume)}");}
            return f;
        }

        public string describe(){
            List<string> bits = new List<string>();
            if(Math.Abs(Volume - 1) > 1e-9){bits.Add($"{Volume * 100:0}%");}
            if(Channel != ChannelMode.AsRecorded){bits.Add(ClipMixer.Effects.channelName(Channel));}
            bits.AddRange(Effects.Select(e => e.describe()));
            return bits.Count == 0 ? "unchanged" : string.Join(", ", bits);
        }
    }

    public sealed record OutputAudioTrack(int[] Sources, string? FilterLabel){
        public bool IsCopy => FilterLabel == null;
    }

    public sealed class AudioPlan{
        public bool MixDown { get; init; }
        public IReadOnlyList<TrackPlan> Tracks { get; init; } = Array.Empty<TrackPlan>();

        public bool IsPassthrough => !MixDown && Tracks.All(t => t.IsUntouched);

        public int OutputTrackCount => MixDown ? (Tracks.Any(t => t.Keep) ? 1 : 0) : Tracks.Count(t => t.Keep);

        public static AudioPlan passthrough(int trackCount) => new(){
            Tracks = Enumerable.Range(0, trackCount)
                .Select(i => new TrackPlan(i, $"Track {i + 1}", 1, true, ChannelMode.AsRecorded, Array.Empty<EffectPlan>()))
                .ToList(),
        };

        public string describe(){
            if(Tracks.Count == 0){return "no audio";}
            List<TrackPlan> kept = Tracks.Where(t => t.Keep).ToList();
            if(MixDown){
                return kept.Count == 0 ? "no audio (every track removed)"
                    : "mixed into one track: " + string.Join(" + ", kept.Select(t => $"{t.Label} ({t.describe()})"));
            }
            return string.Join("; ", Tracks.Select(t => t.Keep ? $"{t.Label}: {(t.IsUntouched ? "copied" : t.describe())}" : $"{t.Label}: REMOVED"));
        }
    }

    public static class AudioGraph{
        //amix ignores timestamps so a late mic drifted out of sync, this pads the gaps w/ silence first
        //dont use it in the player tho, mpv would pad silence all the way up to wherever u seek
        private const string AlignToTimeline = "aresample=async=1:first_pts=0";

        //game + mic both loud = clipping when summed, so limit it
        private const string MixLimiter = "alimiter=limit=0.98:level=0:latency=1";

        //pads to the end so an empty track still exists in the file, caller always passes -t so it stops
        private const string PadToEnd = "apad";

        public const double LateStartThreshold = 0.03;//under 30ms u cant hear it

        private static string chain(string input, string output, IEnumerable<string> filters){
            List<string> list = filters.Where(f => !string.IsNullOrEmpty(f)).ToList();
            return $"[{input}]{(list.Count == 0 ? "anull" : string.Join(",", list))}[{output}]";
        }

        //caller has to pass -t if theres a graph (apad). emptyTracks get written as silence because ffmpeg just drops them
        public static (string? FilterComplex, List<OutputAudioTrack> Outputs) buildExport(AudioPlan? plan, int sourceAudioCount,
            ICollection<int>? emptyTracks = null){
            plan ??= AudioPlan.passthrough(sourceAudioCount);
            if(plan.Tracks.Count != sourceAudioCount){
                throw new InvalidOperationException($"Audio settings are for {plan.Tracks.Count} track(s) but the clip has {sourceAudioCount}.");
            }

            List<string> chains = new List<string>();
            List<OutputAudioTrack> outputs = new List<OutputAudioTrack>();
            List<TrackPlan> kept = plan.Tracks.Where(t => t.Keep).ToList();

            if(plan.MixDown){
                if(kept.Count == 0){return (null, outputs);}
                if(kept.Count == 1){
                    TrackPlan t = kept[0];
                    chains.Add(chain($"0:a:{t.Index}", "amixout", new[] { AlignToTimeline }.Concat(t.filters())
                        .Append(t.StereoFilter).Append(PadToEnd)));
                }else{
                    foreach(TrackPlan t in kept){
                        chains.Add(chain($"0:a:{t.Index}", $"m{t.Index}", new[] { AlignToTimeline }.Concat(t.filters()).Append(t.StereoFilter)));
                    }
                    chains.Add(string.Concat(kept.Select(t => $"[m{t.Index}]")) +
                               $"amix=inputs={kept.Count}:normalize=0:duration=longest,{MixLimiter},{PadToEnd}[amixout]");
                }
                outputs.Add(new OutputAudioTrack(kept.Select(t => t.Index).ToArray(), "amixout"));
            }else{
                foreach(TrackPlan t in kept){
                    //todo: skip the apad when the track already covers the whole cut
                    if(t.IsUntouched && emptyTracks?.Contains(t.Index) != true){
                        outputs.Add(new OutputAudioTrack(new[] { t.Index }, null));
                        continue;
                    }
                    string label = $"aout{t.Index}";
                    chains.Add(chain($"0:a:{t.Index}", label, new[] { AlignToTimeline }.Concat(t.filters()).Append(PadToEnd)));
                    outputs.Add(new OutputAudioTrack(new[] { t.Index }, label));
                }
            }
            return (chains.Count == 0 ? null : string.Join(";", chains), outputs);
        }

        //mpv lavfi-complex graph, [aid1].. in and [ao] out. mpv only plays 1 stream so everything gets mixed. null = play normal
        public static string? buildPreview(AudioPlan plan, ICollection<int>? soloTracks = null){
            if(plan.Tracks.Count == 0){return null;}
            bool anySolo = soloTracks != null && soloTracks.Count > 0;
            if(plan.Tracks.Count == 1 && plan.IsPassthrough && !anySolo){return null;}

            List<TrackPlan> audible = plan.Tracks.Where(t => t.Keep && (!anySolo || soloTracks!.Contains(t.Index))).ToList();
            if(audible.Count == 0){return "[aid1]volume=0[ao]";}
            if(audible.Count == 1){
                TrackPlan t = audible[0];
                List<string> f = t.filters();
                if(plan.MixDown){f.Add(t.StereoFilter);}
                return chain($"aid{t.Index + 1}", "ao", f);
            }

            //same as export. if a track starts late everything gets padded onto the timeline, but thats only right
            //from where it loaded so the window reloads the file after a seek. what a pain this was
            bool lateTrack = audible.Any(t => t.StartOffset > LateStartThreshold);
            List<string> chains = audible.Select(t => chain($"aid{t.Index + 1}", $"p{t.Index}",
                (lateTrack ? new[] { AlignToTimeline } : Array.Empty<string>())
                    .Concat(t.filters())
                    .Append(t.StereoFilter))).ToList();
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

    public sealed class EffectParam : Observable{
        private readonly Action changed;
        private double value;

        public EffectParam(ParamDef def, double value, Action changed){
            Def = def;
            this.value = def.clamp(value);
            this.changed = changed;
        }

        public ParamDef Def { get; }
        public string Label => Def.Label;
        public double Min => Def.Min;
        public double Max => Def.Max;
        public double Step => Def.Step;
        public bool IsChoice => Def.IsChoice;
        public bool IsSlider => !Def.IsChoice;

        public double Value {
            get => value;
            set{
                if(set(ref this.value, Def.clamp(value))){
                    raise(nameof(ValueText));
                    changed();
                }
            }
        }

        public string ValueText => Def.format(value);

        public void nextChoice(){
            if(Def.Choices != null){Value = ((int)Math.Round(value) + 1) % Def.Choices.Length;}
        }
    }

    public sealed class EffectSettings : Observable{
        private bool enabled = true;

        public EffectSettings(TrackSettings owner, EffectKind kind, IReadOnlyDictionary<string, double>? values = null){
            Owner = owner;
            Def = Effects.get(kind);
            Params = new ObservableCollection<EffectParam>(Def.Params.Select(p =>
                new EffectParam(p, values != null && values.TryGetValue(p.Key, out double v) ? v : p.Default, owner.notifyChanged)));
        }

        public TrackSettings Owner { get; }
        public EffectDef Def { get; }
        public EffectKind Kind => Def.Kind;
        public string Name => Def.Name;
        public string Description => Def.Description;
        public ObservableCollection<EffectParam> Params { get; }

        public bool Enabled {
            get => enabled;
            set{if(set(ref enabled, value)){Owner.notifyChanged();}}
        }

        public EffectPlan toPlan() => new(Kind, Params.ToDictionary(p => p.Def.Key, p => p.Value));
    }

    public sealed class TrackSettings : Observable{
        public const double MaxVolume = 3;

        private double volume = 1;
        private bool keep = true;
        private bool solo;
        private ChannelMode channel = ChannelMode.AsRecorded;

        public TrackSettings(int index, string label, string info, int channels){
            Index = index;
            Label = label;
            Info = info;
            Channels = channels;
            Effects.CollectionChanged += (_, __) => {raise(nameof(HasEffects)); notifyChanged();};
        }

        public event Action? Changed;

        public int Index { get; }
        public string Label { get; }
        public string Info { get; }
        public int Channels { get; }
        public bool CanChooseChannels => Channels >= 2;
        public ObservableCollection<EffectSettings> Effects { get; } = new();
        public bool HasEffects => Effects.Count > 0;

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

        public bool Solo {//preview only, never exported
            get => solo;
            set{if(set(ref solo, value)){notifyChanged();}}
        }

        public ChannelMode Channel {
            get => channel;
            set{if(set(ref channel, value)){raise(nameof(ChannelText)); notifyChanged();}}
        }

        public string ChannelText => ClipMixer.Effects.channelName(channel) + "  \u25BE";

        public void notifyChanged() => Changed?.Invoke();

        public void addEffect(EffectKind kind) => Effects.Add(new EffectSettings(this, kind));

        public void moveEffect(EffectSettings fx, int delta){
            int i = Effects.IndexOf(fx), j = i + delta;
            if(i < 0 || j < 0 || j >= Effects.Count){return;}
            Effects.Move(i, j);
        }

        public bool IsDefault => Math.Abs(volume - 1) < 1e-9 && keep && channel == ChannelMode.AsRecorded && Effects.Count == 0;

        public TrackPlan toPlan(double startOffset = 0) => new(Index, Label, volume, keep,
            CanChooseChannels ? channel : ChannelMode.AsRecorded,
            Effects.Where(e => e.Enabled).Select(e => e.toPlan()).ToList(),
            Channels, startOffset);

        //copies everything but solo
        public void copyFrom(TrackSettings other){
            Volume = other.Volume;
            Keep = other.Keep;
            if(CanChooseChannels){Channel = other.Channel;}
            Effects.Clear();
            foreach(EffectSettings fx in other.Effects){
                EffectSettings copy = new EffectSettings(this, fx.Kind, fx.Params.ToDictionary(p => p.Def.Key, p => p.Value)) { Enabled = fx.Enabled };
                Effects.Add(copy);
            }
        }
    }
}
