using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClipMixer{
    //libmpv wrapper, no wpf in here. events fire on the mpv thread so ui has to BeginInvoke (never a blocking Invoke)
    public sealed class MpvPlayer : IDisposable{
        public event Action<double>? DurationChanged;
        public event Action<double>? TimePosChanged;
        public event Action<bool>? PauseChanged;
        public event Action? FileLoaded;
        public event Action? FileEnded;//todo: nothing listens to this yet, maybe auto play next clip
        public event Action<string>? LogMessage;
        public event Action? GraphRejected;//audio graph didnt work on this file, playing it plain

        private IntPtr mpv;
        private Thread? eventThread;
        private volatile bool running;
        private int disposed;

        private readonly object waitLock = new();
        private readonly List<TaskCompletionSource<bool>> endFileWaiters = new();

        public string? LoadedPath { get; private set; }

        public MpvPlayer(IntPtr? wid, IDictionary<string, string>? options = null){
            mpv = mpv_create();
            if(mpv == IntPtr.Zero){
                throw new Exception("mpv_create() failed. (mpv-1.dll not loaded or incompatible)");
            }

            try{
                if(wid.HasValue){
                    long w = wid.Value.ToInt64();
                    check(mpv_set_option(mpv, "wid", mpv_format.MPV_FORMAT_INT64, ref w), "mpv_set_option(wid)");
                }

                Dictionary<string, string> opts = new Dictionary<string, string>(StringComparer.Ordinal){
                    ["idle"] = "yes",
                    ["keep-open"] = "yes",//stop on the last frame instead of unloading
                    ["force-window"] = wid.HasValue ? "yes" : "no",
                    ["vo"] = wid.HasValue ? "gpu-next,gpu," : "null",
                    ["hwdec"] = "auto-safe",
                    //broken nvidia recordings fail hw decode, then the software fallback reads garbage frame sizes and crashes
                    //the whole app. took way too long to find this, so stay on hw (it just skips bad frames) + cap the size
                    ["vd-lavc-software-fallback"] = "no",
                    ["vd-lavc-o"] = "max_pixels=35389440",//8k (8192x4320)
                    ["hr-seek"] = "yes",
                    //mpv cant touch keys/mouse at all, q would just quit the player lol. wpf does all hotkeys
                    ["input-default-bindings"] = "no",
                    ["input-vo-keyboard"] = "no",
                    ["input-cursor"] = "no",
                    ["cursor-autohide"] = "no",
                    ["osc"] = "no",
                    ["config"] = "no",
                    ["terminal"] = "no",
                };
                if(options != null){
                    foreach(KeyValuePair<string, string> kv in options){opts[kv.Key] = kv.Value;}
                }

                foreach(KeyValuePair<string, string> kv in opts){
                    int err = mpv_set_option_string(mpv, kv.Key, kv.Value);
                    //unknown options on other mpv builds are fine, only vo actually matters
                    if(err < 0 && (kv.Key == "vo")){check(err, $"mpv option {kv.Key}");}
                }

                check(mpv_initialize(mpv), "mpv_initialize");
                mpv_request_log_messages(mpv, "warn");

                mpv_observe_property(mpv, 0, "duration", mpv_format.MPV_FORMAT_DOUBLE);
                mpv_observe_property(mpv, 0, "time-pos", mpv_format.MPV_FORMAT_DOUBLE);
                mpv_observe_property(mpv, 0, "pause", mpv_format.MPV_FORMAT_FLAG);
            }catch{
                mpv_terminate_destroy(mpv);
                mpv = IntPtr.Zero;
                throw;
            }

            running = true;
            eventThread = new Thread(eventLoop) { IsBackground = true, Name = "mpv-event-loop" };
            eventThread.Start();
        }

        public bool IsAlive => mpv != IntPtr.Zero && running;

        //if a load w/ a filter graph fails we reload w/o it, one record so path + options always match
        private sealed record Fallback(string Path, string Options);
        private volatile Fallback? fallback;

        //event thread only, matches FILE_LOADED/END_FILE to the right load
        private long startedEntryId = -1;
        private long loadedEntryId = -1;

        //lavfi-complex goes in as a per file option. changing it while playing restarts the decoder mid gop
        //and u get garbage frames till the next keyframe, so to change the graph just load again w/ startSec
        public void load(string path, string? lavfiComplex = null, double? startSec = null, bool? paused = null){
            if(mpv == IntPtr.Zero){return;}
            LoadedPath = path;

            List<string> opts = new List<string>();
            if(startSec.HasValue){
                opts.Add("start=" + Math.Max(0, startSec.Value).ToString("0.###", CultureInfo.InvariantCulture));
            }
            if(paused.HasValue){
                opts.Add("pause=" + (paused.Value ? "yes" : "no"));
            }
            string plainOpts = string.Join(",", opts);

            fallback = string.IsNullOrEmpty(lavfiComplex) ? null : new Fallback(path, plainOpts);
            if(!string.IsNullOrEmpty(lavfiComplex)){
                opts.Insert(0, $"lavfi-complex=%{System.Text.Encoding.UTF8.GetByteCount(lavfiComplex)}%{lavfiComplex}");
            }

            loadFile(path, string.Join(",", opts));
        }

        private void loadFile(string path, string options){
            //loadfile <url> <flags> <index> <options>, index arg only exists since mpv 0.38
            if(options.Length == 0){command("loadfile", path, "replace");}else{command("loadfile", path, "replace", "-1", options);}
        }

        //unload + wait for END_FILE so the file can actually be renamed/deleted after
        public async Task stopAsync(TimeSpan? timeout = null){
            if(mpv == IntPtr.Zero){return;}

            LoadedPath = null;
            fallback = null;
            if(tryGetFlag("idle-active", out int idle) && idle != 0){
                return;
            }

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(waitLock){endFileWaiters.Add(tcs);}

            command("stop");

            await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            lock(waitLock){endFileWaiters.Remove(tcs);}
        }

        public void play() => setFlag("pause", false);
        public void pause() => setFlag("pause", true);
        public void togglePause() => setFlag("pause", !IsPaused);
        public bool IsPaused => tryGetFlag("pause", out int v) && v != 0;

        public void seek(double seconds){
            if(mpv == IntPtr.Zero){return;}
            //command("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute");
            command("seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "absolute", "exact");
        }

        public void seekRelative(double seconds){
            if(mpv == IntPtr.Zero){return;}
            command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "relative", "exact");
        }

        public void frameStep() => command("frame-step");
        public void frameBackStep() => command("frame-back-step");

        public double getTimePos() => tryGetDouble("time-pos", out double v) ? v : 0;
        public double getDuration() => tryGetDouble("duration", out double v) ? v : 0;

        public bool tryGetFlagProperty(string name, out bool value){
            value = false;
            if(!tryGetFlag(name, out int v)){return false;}
            value = v != 0;
            return true;
        }

        public string? getStringProperty(string name){
            if(mpv == IntPtr.Zero){return null;}
            IntPtr p = mpv_get_property_string(mpv, name);
            if(p == IntPtr.Zero){return null;}
            try{return Marshal.PtrToStringUTF8(p);}finally{mpv_free(p);}
        }

        //type is "audio", "video" or "sub". these are mpv ids (start at 1 per type), NOT ffmpeg stream indexes
        public int[] getTrackIds(string type){
            if(mpv == IntPtr.Zero){return Array.Empty<int>();}

            mpv_node node = default;
            int err = mpv_get_property(mpv, "track-list", mpv_format.MPV_FORMAT_NODE, ref node);
            if(err < 0){return Array.Empty<int>();}

            try{return extractTrackIds(node, type);}finally{mpv_free_node_contents(ref node);}
        }

        //node parsing, what the fuck is this api. track-list is an array of maps
        private static int[] extractTrackIds(mpv_node node, string wantedType){
            if(node.format != mpv_format.MPV_FORMAT_NODE_ARRAY || node.u.list == IntPtr.Zero){
                return Array.Empty<int>();
            }

            mpv_node_list list = Marshal.PtrToStructure<mpv_node_list>(node.u.list);
            if(list.num <= 0 || list.values == IntPtr.Zero){return Array.Empty<int>();}

            int nodeSize = Marshal.SizeOf<mpv_node>();
            List<int> ids = new List<int>();

            for(int i = 0; i < list.num; i++){
                mpv_node item = Marshal.PtrToStructure<mpv_node>(IntPtr.Add(list.values, i * nodeSize));
                if(item.format != mpv_format.MPV_FORMAT_NODE_MAP || item.u.list == IntPtr.Zero){
                    continue;
                }

                mpv_node_list map = Marshal.PtrToStructure<mpv_node_list>(item.u.list);
                if(map.num <= 0 || map.values == IntPtr.Zero || map.keys == IntPtr.Zero){
                    continue;
                }

                string? type = null;
                long id = -1;
                for(int k = 0; k < map.num; k++){
                    string key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(map.keys, k * IntPtr.Size)) ?? "";
                    mpv_node val = Marshal.PtrToStructure<mpv_node>(IntPtr.Add(map.values, k * nodeSize));

                    if(key == "type" && val.format == mpv_format.MPV_FORMAT_STRING && val.u.str != IntPtr.Zero){
                        type = Marshal.PtrToStringUTF8(val.u.str);
                    }else if(key == "id" && val.format == mpv_format.MPV_FORMAT_INT64){
                        id = val.u.int64;
                    }
                }

                if(string.Equals(type, wantedType, StringComparison.OrdinalIgnoreCase) && id >= 0){
                    ids.Add((int)id);
                }
            }

            return ids.ToArray();
        }

        private void eventLoop(){
            while(running){
                IntPtr ctx = mpv;
                if(ctx == IntPtr.Zero){break;}

                IntPtr evPtr = mpv_wait_event(ctx, 0.25);
                if(evPtr == IntPtr.Zero || !running){continue;}

                mpv_event ev = Marshal.PtrToStructure<mpv_event>(evPtr);
                try{
                    switch(ev.event_id){
                        case mpv_event_id.MPV_EVENT_NONE:
                            break;

                        case mpv_event_id.MPV_EVENT_PROPERTY_CHANGE:
                            handlePropertyChange(ev.data);
                            break;

                        case mpv_event_id.MPV_EVENT_START_FILE:
                            if(ev.data != IntPtr.Zero){startedEntryId = Marshal.ReadInt64(ev.data, 0);}
                            break;

                        case mpv_event_id.MPV_EVENT_FILE_LOADED:
                            loadedEntryId = startedEntryId;
                            FileLoaded?.Invoke();
                            break;

                        case mpv_event_id.MPV_EVENT_END_FILE:{
                            //{ int reason; int error; int64 playlist_entry_id; ... } so id is at offset 8
                            int reason = ev.data != IntPtr.Zero ? Marshal.ReadInt32(ev.data, 0) : -1;
                            long endId = ev.data != IntPtr.Zero ? Marshal.ReadInt64(ev.data, 8) : -1;
                            Fallback? fb = fallback;
                            if(reason == MPV_END_FILE_REASON_ERROR && endId == startedEntryId && endId != loadedEntryId &&
                                fb != null && fb.Path == LoadedPath){
                                fallback = null;
                                LogMessage?.Invoke("[warn] audio mix graph rejected; loading without it");
                                loadFile(fb.Path, fb.Options);
                                GraphRejected?.Invoke();
                                break;
                            }
                            FileEnded?.Invoke();
                            completeEndFileWaiters();
                            break;
                        }

                        case mpv_event_id.MPV_EVENT_IDLE:
                            completeEndFileWaiters();
                            break;

                        case mpv_event_id.MPV_EVENT_LOG_MESSAGE:
                            if(ev.data != IntPtr.Zero && LogMessage != null){
                                mpv_event_log_message lm = Marshal.PtrToStructure<mpv_event_log_message>(ev.data);
                                string prefix = Marshal.PtrToStringUTF8(lm.prefix) ?? "";
                                string level = Marshal.PtrToStringUTF8(lm.level) ?? "";
                                string text = (Marshal.PtrToStringUTF8(lm.text) ?? "").TrimEnd();
                                LogMessage($"[{level}] {prefix}: {text}");
                            }
                            break;

                        case mpv_event_id.MPV_EVENT_SHUTDOWN:
                            running = false;
                            completeEndFileWaiters();
                            break;
                    }
                }catch{
                    //a bad subscriber cant be allowed to kill the loop
                }
            }
        }

        private void handlePropertyChange(IntPtr data){
            if(data == IntPtr.Zero){return;}
            mpv_event_property prop = Marshal.PtrToStructure<mpv_event_property>(data);
            if(prop.name == IntPtr.Zero || prop.data == IntPtr.Zero){return;}//MPV_FORMAT_NONE, prop not available

            string name = Marshal.PtrToStringUTF8(prop.name) ?? "";
            if(prop.format == mpv_format.MPV_FORMAT_DOUBLE){
                double val = Marshal.PtrToStructure<double>(prop.data);
                if(name == "duration"){DurationChanged?.Invoke(val);}else if(name == "time-pos"){TimePosChanged?.Invoke(val);}
            }else if(prop.format == mpv_format.MPV_FORMAT_FLAG && name == "pause"){
                PauseChanged?.Invoke(Marshal.ReadInt32(prop.data) != 0);
            }
        }

        private void completeEndFileWaiters(){
            TaskCompletionSource<bool>[] waiters;
            lock(waitLock){
                waiters = endFileWaiters.ToArray();
                endFileWaiters.Clear();
            }
            foreach(TaskCompletionSource<bool> w in waiters){w.TrySetResult(true);}
        }

        public void Dispose(){
            if(Interlocked.Exchange(ref disposed, 1) != 0){return;}

            running = false;
            IntPtr ctx = mpv;
            if(ctx != IntPtr.Zero){
                try{mpv_wakeup(ctx);}catch{}
            }

            //ok to join here since the event thread never waits on the ui
            try{eventThread?.Join(3000);}catch{}

            if(ctx != IntPtr.Zero){
                mpv = IntPtr.Zero;
                try{mpv_terminate_destroy(ctx);}catch{}
            }
            completeEndFileWaiters();
        }

        private void setFlag(string name, bool value){
            if(mpv == IntPtr.Zero){return;}
            int v = value ? 1 : 0;
            mpv_set_property(mpv, name, mpv_format.MPV_FORMAT_FLAG, ref v);
        }

        private bool tryGetDouble(string name, out double value){
            value = 0;
            if(mpv == IntPtr.Zero){return false;}
            double tmp = 0;
            if(mpv_get_property(mpv, name, mpv_format.MPV_FORMAT_DOUBLE, ref tmp) < 0){return false;}
            value = tmp;
            return true;
        }

        private bool tryGetFlag(string name, out int value){
            value = 0;
            if(mpv == IntPtr.Zero){return false;}
            int tmp = 0;
            if(mpv_get_property(mpv, name, mpv_format.MPV_FORMAT_FLAG, ref tmp) < 0){return false;}
            value = tmp;
            return true;
        }

        private int command(params string[] args){
            if(mpv == IntPtr.Zero){return -1;}

            //mpv wants a null terminated array of utf8 strings, last slot stays 0
            IntPtr[] ptrs = new IntPtr[args.Length + 1];
            try{
                for(int i = 0; i < args.Length; i++){
                    ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                }

                GCHandle h = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
                try{return mpv_command(mpv, h.AddrOfPinnedObject());}finally{h.Free();}
            }finally{
                for(int i = 0; i < args.Length; i++){
                    if(ptrs[i] != IntPtr.Zero){Marshal.FreeCoTaskMem(ptrs[i]);}
                }
            }
        }

        private static void check(int err, string what){
            if(err < 0){throw new Exception($"{what} failed: mpv error {err}");}
        }

        //all this is from mpv client.h (api 2.5), dont touch the numbers or layouts

        private enum mpv_event_id{
            MPV_EVENT_NONE = 0,
            MPV_EVENT_SHUTDOWN = 1,
            MPV_EVENT_LOG_MESSAGE = 2,
            MPV_EVENT_GET_PROPERTY_REPLY = 3,
            MPV_EVENT_SET_PROPERTY_REPLY = 4,
            MPV_EVENT_COMMAND_REPLY = 5,
            MPV_EVENT_START_FILE = 6,
            MPV_EVENT_END_FILE = 7,
            MPV_EVENT_FILE_LOADED = 8,
            MPV_EVENT_IDLE = 11,
            MPV_EVENT_TICK = 14,
            MPV_EVENT_CLIENT_MESSAGE = 16,
            MPV_EVENT_VIDEO_RECONFIG = 17,
            MPV_EVENT_AUDIO_RECONFIG = 18,
            MPV_EVENT_SEEK = 20,
            MPV_EVENT_PLAYBACK_RESTART = 21,
            MPV_EVENT_PROPERTY_CHANGE = 22,
            MPV_EVENT_QUEUE_OVERFLOW = 24,
            MPV_EVENT_HOOK = 25,
        }

        private enum mpv_format{
            MPV_FORMAT_NONE = 0,
            MPV_FORMAT_STRING = 1,
            MPV_FORMAT_OSD_STRING = 2,
            MPV_FORMAT_FLAG = 3,
            MPV_FORMAT_INT64 = 4,
            MPV_FORMAT_DOUBLE = 5,
            MPV_FORMAT_NODE = 6,
            MPV_FORMAT_NODE_ARRAY = 7,
            MPV_FORMAT_NODE_MAP = 8,
            MPV_FORMAT_BYTE_ARRAY = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct mpv_event{
            public mpv_event_id event_id;
            public int error;
            public ulong reply_userdata;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct mpv_event_property{
            public IntPtr name;
            public mpv_format format;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct mpv_event_log_message{
            public IntPtr prefix;
            public IntPtr level;
            public IntPtr text;
            public int log_level;
        }

        //union comes FIRST then format, order matters
        [StructLayout(LayoutKind.Sequential)]
        private struct mpv_node{
            public mpv_node_union u;
            public mpv_format format;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct mpv_node_union{
            [FieldOffset(0)] public IntPtr str;
            [FieldOffset(0)] public long int64;
            [FieldOffset(0)] public double dbl;
            [FieldOffset(0)] public int flag;
            [FieldOffset(0)] public IntPtr list;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct mpv_node_list{
            public int num;
            public IntPtr values;
            public IntPtr keys;//only for maps
        }

        private const int MPV_END_FILE_REASON_ERROR = 4;

        private const string Lib = "mpv-1.dll";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_create();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_initialize(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_set_option_string(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_set_option(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format, ref long data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_request_log_messages(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command(IntPtr ctx, IntPtr args);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format, ref double data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format, ref int data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format, ref mpv_node data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_get_property_string(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_free(IntPtr data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_free_node_contents(ref mpv_node node);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_set_property(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format, ref int data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_wakeup(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_observe_property(IntPtr ctx, ulong reply_userdata,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, mpv_format format);
    }
}
