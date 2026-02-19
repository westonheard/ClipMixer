using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipMixer{
    //one trim job in the processing list
    public sealed class ClipJob : INotifyPropertyChanged{
        public ClipEntry Entry { get; }
        public TrimRequest Request { get; }
        public string FileName => Entry.FileName;

        public ClipJob(ClipEntry entry, TrimRequest request){
            Entry = entry;
            Request = request;
            status = "Waiting...";
        }

        private string status;
        public string Status { get => status; private set{status = value; onChanged();} }

        private double progress;
        public double Progress { get => progress; private set{progress = value; onChanged();} }

        private bool isRunning;
        public bool IsRunning { get => isRunning; private set{isRunning = value; onChanged();} }

        private bool isFinished;
        public bool IsFinished { get => isFinished; private set{isFinished = value; onChanged();} }

        private bool failed;
        public bool Failed { get => failed; private set{failed = value; onChanged();} }

        public string? LogPath { get; private set; }

        public void report(string stage, double fraction){
            IsRunning = true;
            Progress = fraction;
            Status = fraction > 0 && fraction < 1 ? $"{stage} {fraction:P0}" : $"{stage}...";//only show % while its actually going
        }

        public void succeed(TrimResult r){
            IsRunning = false;
            IsFinished = true;
            Progress = 1;
            LogPath = r.LogPath;
            Status = r.Warnings.Count == 0
                ? $"Trimmed to {r.OutputDurationSec:0.0}s"
                : $"Trimmed to {r.OutputDurationSec:0.0}s - {string.Join(" ", r.Warnings)}";
        }

        public void fail(string message, string? logPath){
            IsRunning = false;
            IsFinished = true;
            Failed = true;
            LogPath = logPath;
            Status = "FAILED (original kept): " + message;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void onChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
