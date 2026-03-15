using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinUIShared.Helpers;

namespace ConcatMediaPage
{
    public class ConcatProcessor(string ffmpegPath): Processor(ffmpegPath, new FileLogger.FileLogger($"{nameof(ReelBox)}/Concat"))
    {
        string concatFileName;

        public async Task Concat(string[] fileNames, bool reEncode)
        {
            List<TimeSpan> segmentDurations = new();
            await StartFfmpegProcess(string.Join(" ", fileNames.Select(name => $"-i \"{name}\"")), (sender, args) =>
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

            var outputFileName = GetOutputAndConcatFileNames(fileNames[0]);
            await using (StreamWriter writer = new(File.Create(concatFileName)))
            {
                foreach (var fileName in fileNames) await writer.WriteLineAsync($"file '{fileName}'");
            }

            var currentSegment = 0;
            var elapsedSegmentDurationSum = segmentDurations[currentSegment];
            var totalDuration = segmentDurations.Aggregate((curr, prev) => curr + prev);
            var total = fileNames.Length;
            leftTextPrimary.Report($"{currentSegment}/{total}");
            rightTextPrimary.Report(Path.GetFileName(fileNames[currentSegment]));

            if(!reEncode)
            {
                //-ignore_unknown specified to ignore streams whose codec was unrecognized by ffmpeg
                await StartFfmpegProcess($"-f concat -safe 0 -i \"{concatFileName}\" -c copy -ignore_unknown -map 0:v? -map 0:a? -map 0:s? \"{outputFileName}\"", ReceivedEventHandler);
            }
            else
            {
                EnableHardwareDecoding(false);
                var inputArgs = new StringBuilder();
                var va = new StringBuilder();
                for (var i = 0; i < fileNames.Length; i++)
                {
                    inputArgs.Append($"-i \"{fileNames[i]}\" ");
                    va.Append($"[{i+1}:v][{i+1}:a]");
                }

                await StartFfmpegTranscodingProcessDefaultQuality(fileNames, outputFileName, $"-f concat -safe 0 -i \"{concatFileName}\"",
                    $"-filter_complex \"{va} concat=n={fileNames.Length}:v=1:a=1 [v][a]\" -map \"[v]\" -map \"[a]\" -map 0:s? -map_chapters -1 -c:a aac -c:s copy",
                    ReceivedEventHandler);
            }

            if (HasBeenKilled()) return;
            AllDone(segmentDurations.Count);
            File.Delete(concatFileName);

            int GetCurrentSegment(TimeSpan currentTime)
            {
                var sum = TimeSpan.Zero;
                for (var i = 0; i < segmentDurations.Count; i++)
                {
                    sum += segmentDurations[i];
                    if (currentTime <= sum) return i;
                }
                return -1;
            }

            void ReceivedEventHandler(object _, DataReceivedEventArgs args)
            {
                if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) return;
                Debug.WriteLine(args.Data);
                logger.Log(args.Data);
                if (CheckFailureStrings(args.Data)) return;
                if (CheckCannotBeMerged(args.Data)) return;
                if (!args.Data.StartsWith("frame")) return;
                if (CheckNoSpaceDuringProcess(args.Data)) return;
                var matchCollection = Regex.Matches(args.Data, @"^frame=\s*\d+\s.+?time=(\d{2}:\d{2}:\d{2}\.\d{2}).+");
                if (matchCollection.Count == 0) return;
                var currentTime = TimeSpan.Parse(matchCollection[0].Groups[1].Value);
                currentSegment = GetCurrentSegment(currentTime);
                elapsedSegmentDurationSum += segmentDurations[currentSegment];
                leftTextPrimary.Report($"{currentSegment}/{total}");
                rightTextPrimary.Report(Path.GetFileName(fileNames[currentSegment]));
                IncrementMergeProgress(currentTime, segmentDurations, totalDuration, currentSegment);
            }
        }

        void IncrementMergeProgress(TimeSpan currentTime, List<TimeSpan> segmentDurations, TimeSpan totalDuration, int currentSegment)
        {
            var segmentDuration = segmentDurations[currentSegment];
            var fraction = (currentTime - (currentSegment == 0 ? TimeSpan.Zero : segmentDurations[..currentSegment].Aggregate((curr, prev) => curr + prev))) / segmentDuration;
            progressPrimary.Report(currentTime / totalDuration * ProgressMax);
            progressSecondary.Report(Math.Max(0, Math.Min(fraction * ProgressMax, ProgressMax)));
            centerTextSecondary.Report($"{Math.Round(fraction * 100, 2)} %");
        }

        private bool CheckCannotBeMerged(string line)
        {
            if(currentProcess == null) return false;
            if (!line.EndsWith("Bitstream filter not found") && !line.EndsWith("out of order")) return false;
            if (!currentProcess.HasExited) currentProcess.Kill();
            error("Process failed.\nThese files cannot be merged");
            return true;
        }

        string GetOutputAndConcatFileNames(string firstFileName)
        {
            var fileNameNoExt = Path.GetFileNameWithoutExtension(firstFileName);
            var num = fileNameNoExt.Contains("000") ? "000" : fileNameNoExt.Contains("001") ? "001" : null;
            outputFile = num != null ? fileNameNoExt.Remove(fileNameNoExt.LastIndexOf(num), num.Length) : fileNameNoExt + "_MERGED";
            var folder = Path.GetDirectoryName(firstFileName) ?? throw new NullReferenceException("The specified path is null");
            outputFile = Path.Combine(folder, outputFile + Path.GetExtension(firstFileName));
            concatFileName = Path.Combine(folder, Path.GetFileNameWithoutExtension(outputFile) + "_Concat.txt");
            File.Delete(outputFile);
            return outputFile;
        }

        void AllDone(int totalSegments)
        {
            currentProcess = null;
            leftTextPrimary.Report($"{totalSegments}/{totalSegments}");
            progressPrimary.Report(ProgressMax);
            progressSecondary.Report(ProgressMax);
            centerTextSecondary.Report("100 %");
        }

        public override async Task Cancel()
        {
            await base.Cancel();
            if (File.Exists(concatFileName)) File.Delete(concatFileName);
        }
    }
}
