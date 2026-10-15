using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Panelith.App.Controls;
using Panelith.App.Services;
using Panelith.Hosting;
using Panelith.Imaging;
using Panelith.Media;
using Panelith.Rendering;
using Panelith.Themes;

namespace Panelith.App.ViewModels;

/// <summary>
/// Turns a video or picture into a new screen, or into the wallpaper of an
/// existing one: choose the part to keep, trim, then bake at the panel's size.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class MediaImportViewModel : ObservableObject, IDisposable
{
    /// <summary>Video file types offered in the file picker.</summary>
    public static readonly string[] VideoPatterns = ["*.mp4", "*.m4v", "*.mov", "*.wmv", "*.avi", "*.mkv", "*.webm"];

    /// <summary>Picture file types offered in the file picker.</summary>
    public static readonly string[] PicturePatterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif"];

    private static readonly TimeSpan DefaultLength = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ReviewFrameTime = TimeSpan.FromSeconds(1d / 15d);
    private static readonly TimeSpan RangeDelay = TimeSpan.FromMilliseconds(400);
    private const int FilmstripCount = 24;

    private readonly PanelithService _service;
    private readonly ScreenWorkshop _workshop;
    private readonly ILogger _logger;
    private readonly string? _wallpaperFor;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "Panelith", "preview");
    private readonly object _renderGate = new();
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _previewDelay;
    private Made? _made;
    private ThemeRenderer? _renderer;
    private DispatcherTimer? _reviewTimer;
    private bool _rendering;
    private readonly string _session;
    private readonly List<ExtractedFrame> _frames = [];
    private readonly DispatcherTimer _playTimer;
    private CancellationTokenSource? _filmstripJob;
    private CancellationTokenSource? _rangeJob;
    private int _rangeCount;
    private bool _rangeDone;
    private int _playIndex;
    private bool _disposed;

    /// <summary>Creates the importer for <paramref name="source"/>.</summary>
    /// <param name="service">The running engine.</param>
    /// <param name="source">The video or picture file.</param>
    /// <param name="wallpaperFor">The screen getting a wallpaper, or null to make a new screen.</param>
    public MediaImportViewModel(PanelithService service, string source, string? wallpaperFor)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _workshop = new ScreenWorkshop(service.Library, service.Runtime.Paths);
        _logger = service.Log.CreateLogger("Panelith.App.Import");
        _wallpaperFor = wallpaperFor;
        Source = source;
        (PanelWidth, PanelHeight) = service.PanelSize;
        IsVideo = VideoPatterns.Any(p => source.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase));
        Name = Path.GetFileNameWithoutExtension(source);
        Title = wallpaperFor is null ? "New video screen" : "Wallpaper";
        _session = Path.Combine(_previewFolder, Guid.NewGuid().ToString("N"));
        _playTimer = new DispatcherTimer(PlayInterval(), DispatcherPriority.Background, (_, _) => PlayTick());
    }

    /// <summary>Pictures from across the video, for choosing the part to loop; null until each is ready.</summary>
    public ObservableCollection<Bitmap?> Thumbnails { get; } = [];

    /// <summary>The longest loop a screen can hold, in seconds.</summary>
    public double MaxLengthSeconds { get; } = ClipLimits.MaxFrames / (double)BakeRequest.DefaultFps;

    /// <summary>Whether the preview of the chosen part is playing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon))]
    [NotifyPropertyChangedFor(nameof(PlayTip))]
    public partial bool IsPlaying { get; set; } = true;

    public Geometry PlayIcon => IsPlaying ? Icons.Pause : Icons.Play;

    public string PlayTip => IsPlaying ? "Pause the preview" : "Play the preview";

    /// <summary>The time of the preview picture showing, in seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    public partial double PlayheadSeconds { get; set; } = -1d;

    public string PlayheadText => PlayheadSeconds < 0d
        ? $"{Clock(StartSeconds)} / {Clock(DurationSeconds)}"
        : $"{Clock(PlayheadSeconds)} / {Clock(DurationSeconds)}";

    /// <summary>How far the preview of the chosen part has got, or null once it is all ready.</summary>
    [ObservableProperty]
    public partial string? PreviewProgressText { get; set; }

    /// <summary>Raised when the window should close; carries the screen id made or changed, or null.</summary>
    public event EventHandler<string?>? Finished;

    public string Source { get; }

    public string SourceName => Path.GetFileName(Source);

    public string Title { get; }

    public bool IsVideo { get; }

    public bool IsNewScreen => _wallpaperFor is null;

    public bool IsWallpaper => _wallpaperFor is not null;

    public string CreateLabel => IsNewScreen ? "Create screen" : "Set wallpaper";

    public int PanelWidth { get; }

    public int PanelHeight { get; }

    public double PanelAspect => PanelWidth / (double)PanelHeight;

    public string PanelText => $"{PanelWidth} × {PanelHeight}";

    public string[] OverlayChoices { get; } = ["Just the video", "Readings along the bottom", "Clock"];

    [ObservableProperty]
    public partial Bitmap? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoftWarning))]
    public partial CropRect Crop { get; set; } = CropRect.Full;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    public partial int SourceWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSizeText))]
    public partial int SourceHeight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    public partial double StartSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    public partial double EndSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double Speed { get; set; } = 1d;

    [ObservableProperty]
    public partial bool SmoothLoop { get; set; } = true;

    [ObservableProperty]
    public partial int Overlay { get; set; } = 1;

    [ObservableProperty]
    public partial double ScrimPercent { get; set; } = 35d;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ProgressKnown { get; set; } = true;

    [ObservableProperty]
    public partial string Status { get; set; } = "Reading the file...";

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>True while the finished screen plays for review.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsReviewing { get; set; }

    public bool IsEditing => !IsReviewing;

    /// <summary>The finished screen, playing as it will on the panel.</summary>
    public LiveFrame ResultFrame { get; } = new();

    /// <summary>Where the loop was saved as an ordinary video, or null.</summary>
    [ObservableProperty]
    public partial string? ExportedFile { get; set; }

    /// <summary>Where the downloaded video is kept, when it came from YouTube.</summary>
    public string? DownloadedFile => Source.StartsWith(MediaFolders.Downloads, StringComparison.OrdinalIgnoreCase) ? Source : null;

    public string LengthText
    {
        get
        {
            var length = Math.Max(0d, EndSeconds - StartSeconds);
            var c = CultureInfo.CurrentCulture;
            var megabytes = length * BakeRequest.DefaultFps * PanelWidth * PanelHeight * 0.11d / 1_000_000d;
            return $"{length.ToString("0.0", c)} s loop · about {Math.Max(1d, megabytes).ToString("0", c)} MB";
        }
    }

    public string SourceSizeText => SourceWidth > 0 ? $"{SourceWidth} × {SourceHeight}" : string.Empty;

    public string SpeedText => Speed.ToString("0.##", CultureInfo.CurrentCulture) + "×";

    /// <summary>A note when the chosen part has fewer pixels than the panel, so it will look soft.</summary>
    public string? SoftWarning =>
        SourceWidth > 0 && Crop.IsSmallerThan(SourceWidth, SourceHeight, PanelWidth, PanelHeight)
            ? $"The selected part is {(int)(Crop.Width * SourceWidth)} × {(int)(Crop.Height * SourceHeight)} pixels, smaller than the panel's {PanelText}, so it will look a little soft."
            : null;

    /// <summary>Reads the file and shows its first picture.</summary>
    public async Task LoadAsync()
    {
        try
        {
            Directory.CreateDirectory(_previewFolder);
            if (IsVideo)
            {
                var probe = await ProbeAsync(TimeSpan.Zero, CancellationToken.None);
                SourceWidth = probe.Width;
                SourceHeight = probe.Height;
                DurationSeconds = Math.Round((probe.Duration ?? DefaultLength).TotalSeconds, 2);
                (StartSeconds, EndSeconds) = Filmstrip.Limit(0d, Math.Min(DurationSeconds, DefaultLength.TotalSeconds), DurationSeconds, MaxLengthSeconds, FilmstripEdge.End);
            }
            else
            {
                if (new FileInfo(Source).Length > ImageBaker.MaxSourceBytes)
                {
                    throw new InvalidDataException("The picture is larger than 64 MB.");
                }

                Preview = new Bitmap(Source);
                SourceWidth = Preview.PixelSize.Width;
                SourceHeight = Preview.PixelSize.Height;
            }

            Crop = CropRect.Cover(SourceWidth, SourceHeight, PanelWidth, PanelHeight);
            Status = IsVideo
                ? "Drag the box to choose what the panel shows, and the handles below to choose the part that loops."
                : "Drag the box to choose the part of the picture to show. The wheel or the corner resizes it.";
            IsLoaded = true;
            if (IsVideo)
            {
                StartFilmstrip();
                StartRangePreview();
                _playTimer.Start();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("Could not open {File}: {Error}", Source, ex.Message);
            Problem = $"Panelith could not open this file. {ex.Message}";
            Status = string.Empty;
        }
    }

    partial void OnStartSecondsChanged(double value) => ScheduleRangePreview();

    partial void OnEndSecondsChanged(double value) => ScheduleRangePreview();

    partial void OnSpeedChanged(double value) => _playTimer.Interval = PlayInterval();

    /// <summary>Pauses or resumes the preview.</summary>
    [RelayCommand]
    private void TogglePlay() => IsPlaying = !IsPlaying;

    /// <summary>Shows the whole width or height of the picture again.</summary>
    [RelayCommand]
    private void ResetCrop()
    {
        if (SourceWidth > 0)
        {
            Crop = CropRect.Cover(SourceWidth, SourceHeight, PanelWidth, PanelHeight);
        }
    }

    private bool CanCreate() => IsLoaded && !IsBusy && !IsReviewing && (!IsNewScreen || !string.IsNullOrWhiteSpace(Name));

    /// <summary>Bakes the clip or picture, writes the screen, then plays it for review.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!_rangeDone)
        {
            _previewDelay?.Cancel();
            _rangeJob?.Cancel();
        }

        IsBusy = true;
        Problem = null;
        Progress = 0d;
        _work = new CancellationTokenSource();
        Made? made = null;
        try
        {
            string? clip = null;
            if (IsNewScreen)
            {
                var (id, folder) = _workshop.NewScreenFolder(Name.Trim());
                made = new Made(id, folder, DeleteFolder: true, OriginalTheme: null, NewAsset: null, null!);
                clip = Path.Combine(folder, "clip" + ClipFile.Extension);
                await BakeVideoAsync(clip, _work.Token);
                var theme = ScreenWorkshop.VideoScreen(id, Name.Trim(), PanelWidth, PanelHeight, "clip" + ClipFile.Extension, Speed, (VideoOverlay)Overlay);
                _workshop.Save(theme, folder);
                made = made with { Theme = theme with { BasePath = folder } };
            }
            else
            {
                Status = "Making an editable copy of the screen...";
                var (editId, folder, theme) = _workshop.Editable(_wallpaperFor!);
                var forked = !string.Equals(editId, _wallpaperFor, StringComparison.OrdinalIgnoreCase);
                var themeFile = Path.Combine(folder, "theme.json");
                var original = forked || !File.Exists(themeFile) ? null : await File.ReadAllTextAsync(themeFile, _work.Token);
                var file = ScreenWorkshop.FreshAssetName(folder, "wallpaper", IsVideo ? ClipFile.Extension : ".jpg");
                made = new Made(editId, folder, DeleteFolder: forked, original, Path.Combine(folder, file), null!);
                if (IsVideo)
                {
                    clip = Path.Combine(folder, file);
                    await BakeVideoAsync(clip, _work.Token);
                }
                else
                {
                    Status = "Fitting the picture to the panel...";
                    ProgressKnown = false;
                    var crop = Crop;
                    await Task.Run(() => ImageBaker.Bake(Source, Path.Combine(folder, file), PanelWidth, PanelHeight, crop), _work.Token);
                }

                var scrim = ScrimPercent < 1d ? null : "#000000" + ((int)Math.Round(ScrimPercent / 100d * 255d)).ToString("x2", CultureInfo.InvariantCulture);
                var updated = ScreenWorkshop.WithWallpaper(theme, IsVideo ? null : file, IsVideo ? file : null, Speed, scrim);
                _workshop.Save(updated, folder);
                made = made with { Theme = updated with { BasePath = folder } };
            }

            if (clip is not null)
            {
                made = made with { Exported = await ExportAsync(clip, made.Theme.Name, _work.Token) };
            }

            _logger.LogInformation("Made {Screen} from {File}.", made.Id, Source);
            _made = made;
            made = null;
            StartReview();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("Could not make a screen from {File}: {Error}", Source, ex.Message);
            Problem = ex.Message;
            Status = string.Empty;
        }
        finally
        {
            if (made is not null)
            {
                Discard(made);
            }

            ProgressKnown = true;
            IsBusy = false;
            _work.Dispose();
            _work = null;
            ResumeRangePreview();
        }
    }

    private void ResumeRangePreview()
    {
        if (IsVideo && IsLoaded && !IsReviewing && !_rangeDone && !_disposed)
        {
            StartRangePreview();
        }
    }

    /// <summary>Keeps what was made and puts it on the panel.</summary>
    [RelayCommand]
    private void Accept()
    {
        if (_made is not { } made)
        {
            return;
        }

        StopReview();
        _made = null;
        if (IsWallpaper)
        {
            ScreenWorkshop.RemoveUnusedWallpapers(made.Folder, made.Theme);
        }

        Finished?.Invoke(this, made.Id);
    }

    /// <summary>Throws away what was made and goes back to choosing the part and the length.</summary>
    [RelayCommand]
    private void Back()
    {
        StopReview();
        if (_made is { } made)
        {
            _made = null;
            Discard(made);
        }

        Status = "Change anything you like, then create it again.";
        ResumeRangePreview();
    }

    /// <summary>Opens the folder with the downloads and the loops made into screens.</summary>
    [RelayCommand]
    private static void OpenVideosFolder()
    {
        Directory.CreateDirectory(MediaFolders.Root);
        SettingsViewModel.OpenFolder(MediaFolders.Root);
    }

    /// <summary>Stops a bake in progress, or closes the window, throwing away anything not yet accepted.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_work is { } work)
        {
            work.Cancel();
            return;
        }

        Finished?.Invoke(this, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _work?.Cancel();
        _previewDelay?.Cancel();
        _playTimer.Stop();
        _filmstripJob?.Cancel();
        _rangeJob?.Cancel();
        foreach (var thumbnail in Thumbnails)
        {
            thumbnail?.Dispose();
        }

        Thumbnails.Clear();
        var session = _session;
        _ = Task.Run(async () =>
        {
            // The helpers stop within a moment of being cancelled; give them time to let go of their files.
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (Directory.Exists(session))
            {
                TryDeleteFolder(session);
            }
        });
        StopReview();
        if (_made is { } made)
        {
            _made = null;
            Discard(made);
        }

        lock (_renderGate)
        {
            _renderer?.Dispose();
            _renderer = null;
        }

        ResultFrame.Dispose();
        Preview?.Dispose();
        PreparedSource.Clear();
        try
        {
            if (Directory.Exists(_previewFolder))
            {
                foreach (var file in Directory.EnumerateFiles(_previewFolder, "*.jpg"))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task BakeVideoAsync(string output, CancellationToken cancellationToken)
    {
        Status = "Baking the clip at the panel's size. This takes about as long as the clip, often less.";
        var request = new BakeRequest(
            Source,
            output,
            PanelWidth,
            PanelHeight,
            Crop,
            TimeSpan.FromSeconds(StartSeconds),
            EndSeconds > StartSeconds ? TimeSpan.FromSeconds(EndSeconds) : null,
            LoopCrossfade: SmoothLoop);
        var progress = new Progress<BakeProgress>(p =>
        {
            ProgressKnown = p.Fraction is not null;
            Progress = (p.Fraction ?? 0d) * 100d;
            Status = $"Baking... {p.Frames.ToString(CultureInfo.CurrentCulture)} frames";
        });
        await MediaJobs.BakeAsync(request, progress, cancellationToken);
    }

    private async Task<VideoProbeResult> ProbeAsync(TimeSpan at, CancellationToken cancellationToken)
    {
        var jpeg = Path.Combine(_previewFolder, Guid.NewGuid().ToString("N") + ".jpg");
        var probe = await MediaJobs.ProbeAsync(Source, at, jpeg, cancellationToken);
        var previous = Preview;
        Preview = new Bitmap(jpeg);
        previous?.Dispose();
        try
        {
            File.Delete(jpeg);
        }
        catch (IOException)
        {
        }

        return probe;
    }

    private TimeSpan PlayInterval() => TimeSpan.FromSeconds(1d / (BakeRequest.DefaultFps * Math.Clamp(Speed, 0.1d, 4d)));

    private async void StartFilmstrip()
    {
        Thumbnails.Clear();
        for (var i = 0; i < FilmstripCount; i++)
        {
            Thumbnails.Add(null);
        }

        var work = _filmstripJob = new CancellationTokenSource();
        var frames = new Progress<ExtractedFrame>(f =>
        {
            if (work.IsCancellationRequested || f.Index is < 0 or >= FilmstripCount)
            {
                return;
            }

            try
            {
                var previous = Thumbnails[f.Index];
                Thumbnails[f.Index] = new Bitmap(f.Path);
                previous?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                _logger.LogDebug("Filmstrip picture {Index} could not be shown: {Error}", f.Index, ex.Message);
            }
        });

        try
        {
            await MediaJobs.FilmstripAsync(Source, FilmstripCount, Path.Combine(_session, "strip"), frames, work.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug("The filmstrip stopped: {Error}", ex.Message);
        }
    }

    private void ScheduleRangePreview()
    {
        if (!IsVideo || !IsLoaded)
        {
            return;
        }

        _previewDelay?.Cancel();
        var delay = _previewDelay = new CancellationTokenSource();
        _ = Task.Delay(RangeDelay, delay.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled && !_disposed)
                {
                    StartRangePreview();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async void StartRangePreview()
    {
        _rangeJob?.Cancel();
        var work = _rangeJob = new CancellationTokenSource();
        var folder = Path.Combine(_session, "range-" + (++_rangeCount).ToString(CultureInfo.InvariantCulture));
        var start = StartSeconds;
        var end = EndSeconds;
        var expected = Math.Max(1d, (end - start) * BakeRequest.DefaultFps);
        _frames.Clear();
        _rangeDone = false;
        _playIndex = 0;
        PreviewProgressText = "Preparing the preview...";
        var frames = new Progress<ExtractedFrame>(f =>
        {
            if (work.IsCancellationRequested)
            {
                return;
            }

            _frames.Add(f);
            PreviewProgressText = $"Preparing the preview... {Math.Min(99d, _frames.Count / expected * 100d).ToString("0", CultureInfo.CurrentCulture)}%";
        });

        try
        {
            await MediaJobs.PreviewFramesAsync(Source, folder, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), frames, work.Token);
            if (!work.IsCancellationRequested)
            {
                _rangeDone = true;
                PreviewProgressText = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogDebug("The preview stopped: {Error}", ex.Message);
            if (!work.IsCancellationRequested)
            {
                _rangeDone = true;
                PreviewProgressText = _frames.Count == 0 ? "The preview could not be made; the screen can still be created." : null;
            }
        }
        finally
        {
            if (work.IsCancellationRequested)
            {
                _ = Task.Run(() => TryDeleteFolder(folder));
            }
        }
    }

    private void PlayTick()
    {
        if (!IsPlaying || IsBusy || IsReviewing || _frames.Count == 0)
        {
            return;
        }

        if (_playIndex >= _frames.Count)
        {
            if (!_rangeDone)
            {
                return;
            }

            _playIndex = 0;
        }

        var frame = _frames[_playIndex++];
        try
        {
            var previous = Preview;
            Preview = new Bitmap(frame.Path);
            previous?.Dispose();
            PlayheadSeconds = frame.Time.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug("Preview frame {Index} could not be shown: {Error}", frame.Index, ex.Message);
        }
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return time.TotalHours >= 1d ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    private async Task<string?> ExportAsync(string clip, string name, CancellationToken cancellationToken)
    {
        Status = "Saving the loop as a video you can play anywhere...";
        ProgressKnown = false;
        var mp4 = MediaFolders.FreshFile(MediaFolders.Screens, name, ".mp4");
        try
        {
            await MediaJobs.ExportAsync(clip, mp4, Speed, cancellationToken);
            return mp4;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save {Clip} as a video: {Error}", clip, ex.Message);
            TryDeleteFile(mp4);
            return null;
        }
    }

    private void StartReview()
    {
        var made = _made!;
        ExportedFile = made.Exported;
        lock (_renderGate)
        {
            _renderer?.Dispose();
            _renderer = new ThemeRenderer(made.Theme, new ThemeRenderOptions(PanelWidth, PanelHeight, ThemeFolder: made.Folder));
        }

        IsReviewing = true;
        Status = IsNewScreen
            ? "This is the new screen as it will play on the panel."
            : "This is the screen with its new wallpaper, as it will play on the panel.";
        _reviewTimer = new DispatcherTimer(ReviewFrameTime, DispatcherPriority.Background, (_, _) => RenderReviewFrame());
        _reviewTimer.Start();
        RenderReviewFrame();
    }

    private void StopReview()
    {
        _reviewTimer?.Stop();
        _reviewTimer = null;
        IsReviewing = false;
        ExportedFile = null;
        lock (_renderGate)
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    private void RenderReviewFrame()
    {
        if (_rendering || !IsReviewing)
        {
            return;
        }

        _rendering = true;
        var bindings = _service.Runtime.Bindings;
        Task.Run(() =>
        {
            lock (_renderGate)
            {
                return _renderer?.Render(bindings);
            }
        }).ContinueWith(
            t =>
            {
                _rendering = false;
                if (t.IsCompletedSuccessfully && t.Result is { } frame && IsReviewing)
                {
                    ResultFrame.Update(frame);
                }
                else if (t.Exception is { } ex)
                {
                    _logger.LogDebug("Review frame failed: {Error}", ex.InnerException?.Message);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Removes everything a create made, or puts back the screen as it was.</summary>
    private void Discard(Made made)
    {
        if (made.Exported is not null)
        {
            TryDeleteFile(made.Exported);
        }

        if (made.DeleteFolder)
        {
            try
            {
                ScreenWorkshop.DeleteFolder(made.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not remove {Screen}: {Error}", made.Id, ex.Message);
            }

            return;
        }

        try
        {
            if (made.OriginalTheme is not null)
            {
                File.WriteAllText(Path.Combine(made.Folder, "theme.json"), made.OriginalTheme);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not put back {Screen}: {Error}", made.Id, ex.Message);
        }

        if (made.NewAsset is not null)
        {
            TryDeleteFile(made.NewAsset);
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What a create wrote, so it can be kept or undone.</summary>
    /// <param name="Id">The screen.</param>
    /// <param name="Folder">The screen's folder.</param>
    /// <param name="DeleteFolder">Whether undoing deletes the whole folder, for a new screen or a fresh copy.</param>
    /// <param name="OriginalTheme">The screen's <c>theme.json</c> before the change, to put back.</param>
    /// <param name="NewAsset">The wallpaper file added.</param>
    /// <param name="Theme">The screen as saved.</param>
    /// <param name="Exported">The loop saved as an MP4.</param>
    private sealed record Made(string Id, string Folder, bool DeleteFolder, string? OriginalTheme, string? NewAsset, Theme Theme, string? Exported = null);
}
