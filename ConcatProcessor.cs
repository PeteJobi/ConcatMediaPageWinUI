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
    public class ConcatProcessor(string ffmpegPath): Processor(ffmpegPath)
    {
        string concatFileName;

        public async Task Concat(string[] fileNames)
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

            //-ignore_unknown specified to ignore streams whose codec was unrecognized by ffmpeg
            await StartFfmpegProcess($"-f concat -safe 0 -i \"{concatFileName}\" -c copy -ignore_unknown -map 0 \"{outputFileName}\"", (_, currentTime, _, _) =>
            {
                if (currentTime > elapsedSegmentDurationSum && currentSegment < segmentDurations.Count - 1)
                {
                    currentSegment++;
                    elapsedSegmentDurationSum += segmentDurations[currentSegment];
                    leftTextPrimary.Report($"{currentSegment}/{total}");
                    rightTextPrimary.Report(Path.GetFileName(fileNames[currentSegment]));
                }
                IncrementMergeProgress(currentTime, segmentDurations, totalDuration, currentSegment);
            });
            if (HasBeenKilled()) return;
            AllDone(segmentDurations.Count);
            File.Delete(concatFileName);
        }

        void IncrementMergeProgress(TimeSpan currentTime, List<TimeSpan> segmentDurations, TimeSpan totalDuration, int currentSegment)
        {
            var segmentDuration = segmentDurations[currentSegment];
            var totalSegments = segmentDurations.Count;
            var currentSegmentDuration = currentSegment < totalSegments - 1 ? segmentDuration : totalDuration - (currentSegment * segmentDuration);
            var fraction = (currentTime - (currentSegment * segmentDuration)) / currentSegmentDuration;
            progressPrimary.Report(currentTime / totalDuration * ProgressMax);
            progressSecondary.Report(Math.Max(0, Math.Min(fraction * ProgressMax, ProgressMax)));
            centerTextSecondary.Report($"{Math.Round(fraction * 100, 2)} %");
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
