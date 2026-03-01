using System;
using System.IO;
using System.Text.Json;

namespace ClipMixer{
    public sealed class AppSettings{
        public string? LastFolder { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool PreciseCut { get; set; }
        public bool UseRecycleBin { get; set; } = true;
        public bool ConfirmDelete { get; set; } = true;
        public bool AutoPlay { get; set; } = true;
        public bool AudioPanelOpen { get; set; } = true;

        //localappdata\ClipMixer unless CLIPMIXER_DATA env var is set
        public static string DataDir =>
            Environment.GetEnvironmentVariable("CLIPMIXER_DATA") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipMixer");

        public static string LogDir => Path.Combine(DataDir, "logs");
        private static string FilePath => Path.Combine(DataDir, "settings.json");

        public static AppSettings load(){
            try{
                if(File.Exists(FilePath)){
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                }
            }catch{}//broken json, just start fresh
            return new AppSettings();
        }

        public void save(){
            try{
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }catch{}
        }
    }
}
