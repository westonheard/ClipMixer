using System;
using System.IO;
using System.Runtime.InteropServices;
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

        public static async Task deleteAsync(string path, bool useRecycleBin, CancellationToken ct = default){
            if(!File.Exists(path)){return;}

            if(!useRecycleBin){
                await retryAsync(() => {
                    if(!File.Exists(path)){return;}
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }, ct).ConfigureAwait(false);
                return;
            }

            Exception? last = null;
            for(int i = 0; i < 20; i++){
                try{
                    await runStaAsync(() => recycleNow(path)).ConfigureAwait(false);
                    if(!File.Exists(path)){return;}
                    last = new IOException("File still exists after sending it to the Recycle Bin.");
                }catch(Exception ex){
                    last = ex;
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            throw new IOException($"Could not move to Recycle Bin: {Path.GetFileName(path)} ({last?.Message})", last);
        }

        //force the export onto disk before the original gets removed, so a power cut cant lose both
        public static void flushToDisk(string path){
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.Flush(flushToDisk: true);
        }

        public static string uniquePath(string dir, string baseName, string ext){
            string p = Path.Combine(dir, baseName + ext);
            for(int n = 2; File.Exists(p); n++){
                p = Path.Combine(dir, $"{baseName}-{n}{ext}");
            }
            return p;
        }

        //shell stuff was flaky off the STA thread so it gets its own
        private static Task runStaAsync(Action action){
            TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread t = new Thread(() => {
                try{action(); tcs.SetResult();}catch(Exception ex){tcs.SetException(ex);}
            }){ IsBackground = true, Name = "recycle-bin" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }

        private static void recycleNow(string path){
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT{
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = Path.GetFullPath(path) + "\0\0",//needs double null at the end, dont ask
                pTo = null,
                //no ui at all since this runs in the background. too big for the bin = shell deletes it for real
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };

            int rc = SHFileOperation(ref op);
            if(rc != 0){
                throw new IOException($"SHFileOperation failed (code 0x{rc:X}).");
            }
            if(op.fAnyOperationsAborted){
                throw new IOException("Recycle operation was aborted.");
            }
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        //copied from stack overflow idk wtf half of this does, but it works
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT{
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}
