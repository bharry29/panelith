# Video screens

A video screen loops a part of a video on the panel, full screen or behind sensor readings. Panelith prepares the video once, cropped and sized for the panel, so playing it afterwards costs almost nothing.

## Making one

On the **Screens** page, open **New video screen** and choose:

- **From a video on this PC…**: an MP4, M4V, MOV, WMV, AVI, MKV or WebM file that Windows can play.
- **From YouTube…**: paste a link. The first time, Panelith explains that you may only download videos you have the right to use. Downloading is done by the bundled [yt-dlp](https://github.com/yt-dlp/yt-dlp), which picks the sharpest version Windows can decode (AV1 up to 4K when supported, otherwise 1440p or H.264).

The import window then lets you:

1. **Choose the loop.** A filmstrip shows pictures from across the video. Drag the start and end handles; the chosen part plays inside the crop box.
2. **Frame it.** Drag and resize the crop box. It keeps the panel's shape, and the strip below shows exactly what the panel will show.
3. **Make it.** Panelith prepares the clip, then plays the finished screen.
4. **Keep it.** **Show on panel** saves the screen and puts it on the panel. **Back to editing**, or closing the window, undoes everything it wrote.

## What is kept, and where

- The screen itself, with its prepared `.ptclip` clip, is in your screens folder (`%APPDATA%\Panelith\themes`).
- Each loop is also saved as an ordinary MP4 in `Videos\Panelith\Screens`, to watch or share.
- Full YouTube downloads are kept in `Videos\Panelith\Downloads` and reused if you make another screen from the same video.

## Video wallpapers

To put a video behind an existing screen's readings, choose **Wallpaper…** from the screen's **⋯** menu. The same import window opens. **Darken wallpaper** in the [Designer](designer.md) dims it so text stays readable.

## Tips

- Short loops of 5 to 20 seconds look best and stay small. A clip is capped at a size that keeps memory use modest.
- Clips play at 15 frames per second, the Trofeo's limit.
- Videos with black bars or a different shape can be cropped to fill the panel; the crop box keeps the panel's proportions.
- If a video will not open, see [Videos will not open](troubleshooting.md#videos-will-not-open).
