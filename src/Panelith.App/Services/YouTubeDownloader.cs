using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Panelith.Media;

namespace Panelith.App.Services;

/// <summary>
/// Downloads one YouTube video with the bundled yt-dlp, for turning into a
/// video screen. Only single videos on youtube.com and youtu.be are accepted.
/// </summary>
public static partial class YouTubeDownloader
{
    /// <summary>The largest download allowed.</summary>
    public const string MaxFileSize = "2G";

    /// <summary>How long a download may take before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The sharpest MP4 Windows can decode, up to 4K. YouTube only offers H.264
    /// up to 1080p; above that it is AV1, which needs the AV1 Video Extension.
    /// </summary>
    private const string Av1First =
        "bv*[vcodec^=av01][ext=mp4][height<=2160]" + Fits + "/bv*[vcodec^=av01][ext=mp4][height<=1440]" + Fits + "/" + H264Only;

    /// <summary>H.264 in MP4, which every Windows can decode.</summary>
    private const string H264Only =
        "bv*[vcodec^=avc1][ext=mp4][height<=2160]" + Fits + "/bv*[vcodec^=avc1][ext=mp4][height<=720]/bv*[ext=mp4][vcodec!^=vp][vcodec!^=av01]";

    /// <summary>Skips streams that would go over <see cref="MaxFileSize"/>; streams of unknown size are tried.</summary>
    private const string Fits = "[filesize<?1900M][filesize_approx<?1900M]";

    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be"];

    /// <summary>The bundled yt-dlp, or null when this build has none.</summary>
    public static string? BundledExecutable => Existing(Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"));

    /// <summary>
    /// The copy yt-dlp updates itself into. Only used by a standard-user
    /// Panelith: an elevated process must not run a program from a folder the
    /// user can write to.
    /// </summary>
    public static string UpdatedExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Panelith", "tools", "yt-dlp.exe");

    /// <summary>Whether a downloader is available.</summary>
    public static bool IsAvailable => Executable is not null && QuickJs is not null;

    private static string? QuickJs => Existing(Path.Combine(AppContext.BaseDirectory, "tools", "qjs.exe"));

    private static string? Executable =>
        !WindowsIntegration.IsElevated && File.Exists(UpdatedExecutable) ? UpdatedExecutable : BundledExecutable;

    /// <summary>
    /// Turns what the user pasted into the address of a single video, or null
    /// when it is not a YouTube video link. Playlists and other parameters are dropped.
    /// </summary>
    public static Uri? ParseVideoUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.UserInfo)
            || !Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string? id = null;
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (url.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = segments[0];
        }
        else if (segments is ["watch"])
        {
            id = QueryValue(url.Query, "v");
        }
        else if (segments is ["shorts" or "live" or "embed" or "v", var value])
        {
            id = value;
        }

        return id is not null && VideoId().IsMatch(id) ? new Uri("https://www.youtube.com/watch?v=" + id) : null;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> and returns the file, kept in
    /// <see cref="MediaFolders.Downloads"/>, and the video's title. A video
    /// downloaded before is reused. <paramref name="folder"/> is scratch space
    /// for the download in progress. Progress is reported from 0 to 1.
    /// Cancelling stops yt-dlp.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<(string File, string? Title)> DownloadAsync(Uri url, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var safe = ParseVideoUrl(url.ToString()) ?? throw new ArgumentException("Only single YouTube videos can be downloaded.", nameof(url));
        var id = QueryValue(safe.Query, "v")!;
        var tag = $" [{id}]";
        if (Directory.Exists(MediaFolders.Downloads)
            && Directory.EnumerateFiles(MediaFolders.Downloads, "*" + tag + ".mp4").FirstOrDefault() is { } kept)
        {
            progress?.Report(1d);
            var name = Path.GetFileNameWithoutExtension(kept);
            return (kept, name[..^tag.Length]);
        }

        var (file, title) = await DownloadToAsync(safe, folder, progress, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(MediaFolders.Downloads);
        var target = Path.Combine(MediaFolders.Downloads, MediaFolders.SafeName(title ?? id) + tag + ".mp4");
        File.Move(file, target, overwrite: true);
        return (target, title);
    }

    /// <summary>Whether downloads prefer AV1, for sizes above 1080p.</summary>
    [SupportedOSPlatform("windows")]
    public static bool PrefersAv1 => MediaFoundationDecoder.CanDecodeAv1;

    [SupportedOSPlatform("windows")]
    private static async Task<(string File, string? Title)> DownloadToAsync(Uri safe, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = Executable ?? throw new InvalidOperationException("This copy of Panelith has no video downloader. Reinstall Panelith to get it back.");
        var quickJs = QuickJs ?? throw new InvalidOperationException("The JavaScript runtime the downloader needs is missing. Reinstall Panelith to get it back.");
        Directory.CreateDirectory(folder);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (var argument in new[]
        {
            "--ignore-config", "--no-update", "--no-js-runtimes", "--js-runtimes", "quickjs:" + quickJs,
            "--no-playlist", "--no-part", "--no-mtime", "--no-cache-dir", "--max-filesize", MaxFileSize,
            "-f", PrefersAv1 ? Av1First : H264Only, "-P", folder, "-o", "video.%(ext)s",
            "--newline", "--progress", "--progress-template", "download:progress %(progress._percent_str)s",
            "--print", "after_move:title:%(title).80s", "--print", "after_move:filepath", "--", safe.ToString(),
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The downloader did not start.");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await using var stop = timeout.Token.Register(() => Kill(process));

        string? file = null;
        string? title = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("title:", StringComparison.Ordinal))
            {
                title = line[6..].Trim();
            }
            else if (line.StartsWith("progress ", StringComparison.Ordinal))
            {
                if (double.TryParse(line[9..].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    progress?.Report(Math.Clamp(percent / 100d, 0d, 1d));
                }
            }
            else if (line.Length > 0)
            {
                file = line.Trim();
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The download took longer than {Timeout.TotalMinutes} minutes and was stopped.");
        }

        var full = file is null ? null : Path.GetFullPath(file);
        if (process.ExitCode != 0 || full is null || !File.Exists(full)
            || !string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Explain(await errors.ConfigureAwait(false)));
        }

        return (full, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    /// <summary>
    /// Updates the downloader in the user's own folder, for when YouTube
    /// changed something and the bundled copy stopped working.
    /// </summary>
    public static async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        if (WindowsIntegration.IsElevated)
        {
            throw new InvalidOperationException("Panelith is running as administrator, which never runs an updated downloader. Update Panelith instead.");
        }

        var bundled = BundledExecutable ?? throw new InvalidOperationException("This copy of Panelith has no video downloader.");
        Directory.CreateDirectory(Path.GetDirectoryName(UpdatedExecutable)!);
        if (!File.Exists(UpdatedExecutable))
        {
            File.Copy(bundled, UpdatedExecutable);
        }

        var start = new ProcessStartInfo(UpdatedExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--update");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The downloader did not start.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var errors = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var last = (output + "\n" + errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last ?? "The downloader is up to date.";
    }

    private static string Explain(string errors)
    {
        var line = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? string.Empty;
        if (line.Contains("max-filesize", StringComparison.OrdinalIgnoreCase) || line.Contains("larger than", StringComparison.OrdinalIgnoreCase))
        {
            return "The video is larger than 2 GB. Pick a shorter one.";
        }

        if (line.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube offers no MP4 version of this video, which Windows needs to read it.";
        }

        if (line.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || line.Contains("Private video", StringComparison.OrdinalIgnoreCase))
        {
            return "This video needs a signed-in account (it may be private or age-restricted), so it cannot be downloaded.";
        }

        return line.Length > 0
            ? "The download failed: " + line["ERROR:".Length..].Trim() + " If this keeps happening, try \"Update downloader\"."
            : "The download failed. If this keeps happening, try \"Update downloader\".";
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && pair[..equals] == name)
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();
}
