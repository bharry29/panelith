using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Panelith.Media;

namespace Panelith.App.Services;

/// <summary>
/// Runs video work in a child copy of Panelith, so a file that crashes a
/// codec takes down that child and not the app driving the panel.
/// </summary>
/// <remarks>
/// The child writes one line per update to stdout: <c>progress fraction frames</c>,
/// then <c>done frames</c> or <c>probe width height durationTicks</c>, or
/// <c>error message</c>. Closing its stdin asks it to stop.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class MediaJobs
{
    internal const string BakeArgument = "--bake-video";
    internal const string ProbeArgument = "--probe-video";
    internal const string ExportArgument = "--export-video";

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Bakes a clip in a child process. Cancelling stops the child and removes any partial file.</summary>
    public static async Task<int> BakeAsync(BakeRequest request, IProgress<BakeProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var crop = request.Crop ?? new CropRect(-1d, -1d, -1d, -1d);
        string[] arguments =
        [
            BakeArgument,
            request.Source,
            request.Output,
            request.PanelWidth.ToString(C),
            request.PanelHeight.ToString(C),
            crop.X.ToString("R", C),
            crop.Y.ToString("R", C),
            crop.Width.ToString("R", C),
            crop.Height.ToString("R", C),
            request.Start.Ticks.ToString(C),
            request.End is { } end ? end.Ticks.ToString(C) : "-",
            request.LoopCrossfade ? "1" : "0",
            request.Fps.ToString(C),
            request.Quality.ToString(C),
        ];

        try
        {
            var result = await RunAsync(arguments, progress, cancellationToken).ConfigureAwait(false);
            return int.Parse(result[0], C);
        }
        catch
        {
            TryDelete(request.Output + ".partial");
            throw;
        }
    }

    /// <summary>Reads a video's size and length and saves the frame at <paramref name="at"/> as a JPEG.</summary>
    public static async Task<VideoProbeResult> ProbeAsync(string source, TimeSpan at, string previewJpeg, CancellationToken cancellationToken)
    {
        var result = await RunAsync([ProbeArgument, source, at.Ticks.ToString(C), previewJpeg], null, cancellationToken).ConfigureAwait(false);
        var duration = long.Parse(result[2], C);
        return new VideoProbeResult(int.Parse(result[0], C), int.Parse(result[1], C), duration > 0 ? TimeSpan.FromTicks(duration) : null);
    }

    /// <summary>Writes a baked clip out as an MP4 in a child process.</summary>
    public static async Task ExportAsync(string clip, string mp4, double speed, CancellationToken cancellationToken)
    {
        await RunAsync([ExportArgument, clip, mp4, speed.ToString("R", C)], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a <see cref="BakeArgument"/>, <see cref="ProbeArgument"/> or
    /// <see cref="ExportArgument"/> command line in the child. Returns false
    /// when the arguments are none of them.
    /// </summary>
    public static bool TryRunChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || (args[0] != BakeArgument && args[0] != ProbeArgument && args[0] != ExportArgument))
        {
            return false;
        }

        using var cancel = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                var buffer = new byte[64];
                while (input.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
            catch (IOException)
            {
            }

            cancel.Cancel();
        })
        { IsBackground = true, Name = "Panelith media stdin" };
        watcher.Start();

        var output = Console.Out;
        try
        {
            if (args[0] == ProbeArgument && args.Length == 4)
            {
                var result = VideoProbe.Probe(args[1], TimeSpan.FromTicks(long.Parse(args[2], C)), args[3]);
                output.WriteLine($"probe {result.Width.ToString(C)} {result.Height.ToString(C)} {(result.Duration?.Ticks ?? 0).ToString(C)}");
            }
            else if (args[0] == BakeArgument && args.Length == 14)
            {
                var crop = new CropRect(double.Parse(args[5], C), double.Parse(args[6], C), double.Parse(args[7], C), double.Parse(args[8], C));
                var request = new BakeRequest(
                    args[1],
                    args[2],
                    int.Parse(args[3], C),
                    int.Parse(args[4], C),
                    crop.X < 0d ? null : crop,
                    TimeSpan.FromTicks(long.Parse(args[9], C)),
                    args[10] == "-" ? null : TimeSpan.FromTicks(long.Parse(args[10], C)),
                    int.Parse(args[12], C),
                    args[11] == "1",
                    int.Parse(args[13], C));
                var last = Stopwatch.StartNew();
                var info = VideoBaker.Bake(
                    request,
                    new Reporter(p =>
                    {
                        if (last.ElapsedMilliseconds >= 100)
                        {
                            last.Restart();
                            output.WriteLine($"progress {(p.Fraction ?? -1d).ToString("0.0000", C)} {p.Frames.ToString(C)}");
                            output.Flush();
                        }
                    }),
                    cancel.Token);
                output.WriteLine($"done {info.FrameCount.ToString(C)}");
            }
            else if (args[0] == ExportArgument && args.Length == 4)
            {
                var frames = ClipExporter.Export(args[1], args[2], double.Parse(args[3], C), cancellationToken: cancel.Token);
                output.WriteLine($"done {frames.ToString(C)}");
            }
            else
            {
                output.WriteLine("error The command line was not understood.");
                exitCode = 3;
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("error Cancelled.");
            exitCode = 2;
        }
        catch (Exception ex)
        {
            output.WriteLine("error " + ex.Message.ReplaceLineEndings(" "));
            exitCode = 1;
        }

        output.Flush();
        return true;
    }

    private static async Task<string[]> RunAsync(string[] arguments, IProgress<BakeProgress>? progress, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Panelith cannot find its own executable.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The video helper did not start.");
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await using var stop = cancellationToken.Register(() =>
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        });

        string[]? result = null;
        string? error = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            var parts = line.Split(' ', 2);
            switch (parts[0])
            {
                case "progress" when parts.Length == 2:
                    var values = parts[1].Split(' ');
                    if (values.Length == 2
                        && double.TryParse(values[0], NumberStyles.Float, C, out var fraction)
                        && int.TryParse(values[1], NumberStyles.Integer, C, out var frames))
                    {
                        progress?.Report(new BakeProgress(fraction < 0d ? null : fraction, frames));
                    }

                    break;
                case "done" or "probe" when parts.Length == 2:
                    result = parts[1].Split(' ');
                    break;
                case "error":
                    error = parts.Length == 2 ? parts[1] : "The video could not be read.";
                    break;
            }
        }

        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            process.Kill(entireProcessTree: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            throw new InvalidDataException(error ?? $"The video helper stopped unexpectedly (exit code {(process.HasExited ? process.ExitCode : -1)}).");
        }

        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Reporter(Action<BakeProgress> report) : IProgress<BakeProgress>
    {
        public void Report(BakeProgress value) => report(value);
    }
}
