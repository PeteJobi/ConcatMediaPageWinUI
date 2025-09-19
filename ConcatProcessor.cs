using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ConcatMediaPage
{
    public class ConcatProcessor
    {
        static readonly string[] allowedExts = { ".mkv", ".mp4", ".mp3" };
        private Process? currentProcess;
        string[] filesCreated = [];
        private bool hasBeenKilled;
        private const string FileNameLongError =
            "The source file name is too long. Shorten it to get the total number of characters in the destination directory lower than 256.\n\nDestination directory: ";

        public async Task Concat(string ffmpegPath, string[] fileNames, double progressMax, IProgress<FileProgress> fileProgress, IProgress<ValueProgress> valueProgress, Action<string> setOutputFile, Action<string> error)
        {
            List<TimeSpan> segmentDurations = new();
            await StartProcess(ffmpegPath, string.Join(" ", fileNames.Select(name => $"-i \"{name}\"")), null, (sender, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) return;
                var matchCollection = Regex.Matches(args.Data, @"\s*Duration:\s(\d{2}:\d{2}:\d{2}\.\d{2}),.+");
                if (matchCollection.Count == 0) return;
                segmentDurations.Add(TimeSpan.Parse(matchCollection[0].Groups[1].Value));
            });
            if (HasBeenKilled()) return;
            if(segmentDurations.Count < 2) // Less than 2 valid files found
            {
                error("No valid files found to merge.");
                return;
            }

            var (outputFileName, concatFileName) = GetOutputAndConcatFileNames(fileNames[0], setOutputFile);
            await using (StreamWriter writer = new(File.Create(concatFileName)))
            {
                foreach (var fileName in fileNames) await writer.WriteLineAsync($"file '{fileName}'");
            }
            filesCreated = [outputFileName, concatFileName];

            var currentSegment = 0;
            var elapsedSegmentDurationSum = segmentDurations[currentSegment];
            var totalDuration = segmentDurations.Aggregate((curr, prev) => curr + prev);
            var total = fileNames.Length;
            fileProgress.Report(new FileProgress
            {
                TotalRangeCount = $"{currentSegment}/{total}",
                CurrentRangeFileName = Path.GetFileName(fileNames[currentSegment])
            });

            await StartProcess(ffmpegPath, $"-f concat -safe 0 -i \"{concatFileName}\" -c copy -map 0 \"{outputFileName}\"", null, (sender, args) =>
            {
                Debug.WriteLine(args.Data);
                if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) return;
                if (CheckFileNameLongErrorSplit(args.Data, error)) return;
                if (CheckCannotBeMerged(args.Data, error)) return;
                if (!args.Data.StartsWith("frame")) return;
                if (CheckNoSpaceDuringMerge(args.Data, error)) return;
                var matchCollection = Regex.Matches(args.Data, @"^frame=\s*\d+\s.+?time=(\d{2}:\d{2}:\d{2}\.\d{2}).+");
                if (matchCollection.Count == 0) return;
                var currentTime = TimeSpan.Parse(matchCollection[0].Groups[1].Value);
                if (currentTime > elapsedSegmentDurationSum)
                {
                    currentSegment++;
                    elapsedSegmentDurationSum += segmentDurations[currentSegment];
                    fileProgress.Report(new FileProgress
                    {
                        TotalRangeCount = $"{currentSegment}/{total}",
                        CurrentRangeFileName = Path.GetFileName(fileNames[currentSegment])
                    });
                }
                IncrementMergeProgress(currentTime, segmentDurations, totalDuration, currentSegment, progressMax, valueProgress);
            });
            if (HasBeenKilled()) return;
            AllDone(segmentDurations.Count, progressMax, fileProgress, valueProgress);
            File.Delete(concatFileName);
        }

        void IncrementMergeProgress(TimeSpan currentTime, List<TimeSpan> segmentDurations, TimeSpan totalDuration, int currentSegment, double max, IProgress<ValueProgress> progress)
        {
            var segmentDuration = segmentDurations[currentSegment];
            var totalSegments = segmentDurations.Count;
            var currentSegmentDuration = currentSegment < totalSegments - 1 ? segmentDuration : totalDuration - (currentSegment * segmentDuration);
            var fraction = (currentTime - (currentSegment * segmentDuration)) / currentSegmentDuration;
            progress.Report(new ValueProgress
            {
                OverallProgress = currentTime / totalDuration * max,
                CurrentActionProgress = Math.Max(0, Math.Min(fraction * max, max)),
                CurrentActionProgressText = $"{Math.Round(fraction * 100, 2)} %"
            });
        }

        private bool CheckCannotBeMerged(string line, Action<string> error)
        {
            if (!line.EndsWith("Bitstream filter not found") && !line.EndsWith("out of order")) return false;
            if(!currentProcess.HasExited) currentProcess.Kill();
            error("Process failed.\nThese files cannot be merged");
            return true;
        }

        private bool CheckNoSpaceDuringMerge(string line, Action<string> error)
        {
            if (!line.EndsWith("No space left on device") && !line.EndsWith("I/O error")) return false;
            if(!currentProcess.HasExited) SuspendProcess(currentProcess);
            error($"Process failed.\nError message: {line}");
            return true;
        }

        private static bool CheckFileNameLongErrorSplit(string line, Action<string> error)
        {
            const string noSuchDirectory = ": No such file or directory";
            if (!line.EndsWith(noSuchDirectory)) return false;
            error(FileNameLongError + line[..^noSuchDirectory.Length]);
            return true;
        }

        (string fileName, string concatFolder) GetOutputAndConcatFileNames(string firstFileName, Action<string> setOutputFile)
        {
            var fileNameNoExt = Path.GetFileNameWithoutExtension(firstFileName);
            var num = fileNameNoExt.Contains("000") ? "000" : fileNameNoExt.Contains("001") ? "001" : null;
            var outputFileName = num != null ? fileNameNoExt.Remove(fileNameNoExt.LastIndexOf(num), num.Length) : fileNameNoExt + "_MERGED";
            var folder = Path.GetDirectoryName(firstFileName) ?? throw new NullReferenceException("The specified path is null");
            outputFileName = Path.Combine(folder, outputFileName + Path.GetExtension(firstFileName));
            var concatFileName = Path.Combine(folder, Path.GetFileNameWithoutExtension(outputFileName) + "_Concat.txt");
            File.Delete(outputFileName);
            setOutputFile(outputFileName);
            return (outputFileName, concatFileName);
        }

        void AllDone(int totalSegments, double max, IProgress<FileProgress> fileProgress, IProgress<ValueProgress> valueProgress)
        {
            currentProcess = null;
            fileProgress.Report(new FileProgress
            {
                TotalRangeCount = $"{totalSegments}/{totalSegments}"
            });
            valueProgress.Report(new ValueProgress
            {
                OverallProgress = max,
                CurrentActionProgress = max,
                CurrentActionProgressText = "100 %"
            });
        }

        bool HasBeenKilled()
        {
            if (!hasBeenKilled) return false;
            hasBeenKilled = false;
            return true;
        }

        public async Task Cancel()
        {
            if (currentProcess == null) return;
            currentProcess.Kill();
            await currentProcess.WaitForExitAsync();
            hasBeenKilled = true;
            currentProcess = null;
            foreach (string path in filesCreated)
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        public void Pause()
        {
            if (currentProcess == null) return;
            foreach (ProcessThread pT in currentProcess.Threads)
            {
                var pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                SuspendThread(pOpenThread);

                CloseHandle(pOpenThread);
            }
        }

        public void Resume()
        {
            if (currentProcess == null) return;
            if (currentProcess.ProcessName == string.Empty)
                return;

            foreach (ProcessThread pT in currentProcess.Threads)
            {
                var pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                int suspendCount;
                do
                {
                    suspendCount = ResumeThread(pOpenThread);
                } while (suspendCount > 0);

                CloseHandle(pOpenThread);
            }
        }

        public void ViewFiles(string file)
        {
            var info = new ProcessStartInfo();
            info.FileName = "explorer";
            info.Arguments = $"/e, /select, \"{file}\"";
            Process.Start(info);
        }

        async Task StartProcess(string processFileName, string arguments, DataReceivedEventHandler? outputEventHandler, DataReceivedEventHandler? errorEventHandler)
        {
            Process ffmpeg = new()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = processFileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
                EnableRaisingEvents = true
            };
            ffmpeg.OutputDataReceived += outputEventHandler;
            ffmpeg.ErrorDataReceived += errorEventHandler;
            ffmpeg.Start();
            ffmpeg.BeginErrorReadLine();
            ffmpeg.BeginOutputReadLine();
            currentProcess = ffmpeg;
            await ffmpeg.WaitForExitAsync();
            ffmpeg.Dispose();
            currentProcess = null;
        }

        [Flags]
        public enum ThreadAccess : int
        {
            SUSPEND_RESUME = (0x0002)
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll")]
        static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")]
        static extern int ResumeThread(IntPtr hThread);
        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        private static void SuspendProcess(Process process)
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                SuspendThread(pOpenThread);

                CloseHandle(pOpenThread);
            }
        }

        public static void ResumeProcess(Process process)
        {
            if (process.ProcessName == string.Empty)
                return;

            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                int suspendCount;
                do
                {
                    suspendCount = ResumeThread(pOpenThread);
                } while (suspendCount > 0);

                CloseHandle(pOpenThread);
            }
        }
    }

    public struct FileProgress
    {
        public string? TotalRangeCount { get; set; }
        public string? CurrentRangeFileName { get; set; }
    }

    public struct ValueProgress
    {
        public double OverallProgress { get; set; }
        public double CurrentActionProgress { get; set; }
        public string CurrentActionProgressText { get; set; }
    }
}
