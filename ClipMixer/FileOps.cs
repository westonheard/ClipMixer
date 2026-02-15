using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipMixer{
    public static class FileOps{
        //retry because mpv, explorer thumbnails, antivirus or ffprobe keep grabbing the file for a sec
        public static async Task retryAsync(Action action, CancellationToken ct, int attempts = 20, int delayMs = 250){
            for(int i = 1; ; i++){
                try{
                    action();
                    return;
                }catch(Exception ex) when((ex is IOException || ex is UnauthorizedAccessException) && i < attempts){
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }

        public static async Task deleteAsync(string path, CancellationToken ct = default){
            if(!File.Exists(path)){return;}
            await retryAsync(() => {
                if(!File.Exists(path)){return;}
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }, ct).ConfigureAwait(false);
        }

        public static string uniquePath(string dir, string baseName, string ext){
            string p = Path.Combine(dir, baseName + ext);
            for(int n = 2; File.Exists(p); n++){
                p = Path.Combine(dir, $"{baseName}-{n}{ext}");
            }
            return p;
        }
    }
}
