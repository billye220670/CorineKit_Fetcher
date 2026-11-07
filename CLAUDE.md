# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CorineKit Fetcher is a WPF-based media random selection tool for Windows. Users can drag folders into the application to randomly browse images, GIFs and videos with caching support, keyboard shortcuts, multi-language UI (Chinese/English/Japanese), and theme customization.

**Tech Stack**: C# 9.0, .NET 9.0 Windows, WPF, XML serialization

## Build Commands

```bash
# Build the project
dotnet build

# Build release version
dotnet build --configuration Release

# Publish standalone executable
dotnet publish Fetcher/Fetcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# Clean build artifacts
dotnet clean
```

**Build Output**: `Fetcher/bin/Release/net9.0-windows/`

**Release Output**: `dist/Fetcher.exe` — designated release folder (gitignored). When the user says 发版 / release, publish here.

## Architecture

### Core Components

1. **MainWindow.xaml.cs** - Main application logic
   - Image loading and caching system
   - Keyboard/mouse navigation handlers
   - Path navigation with breadcrumb buttons
   - Directory traversal with sibling directory detection
   - Language and theme switching

2. **Settings.cs** - Persistent settings management
   - XML serialization with backup mechanism
   - Settings stored at `%APPDATA%/CorineKitFetcher/settings.xml`
   - Atomic write (temp file + replace), previous version kept in `settings_backup.xml`
   - One-time read-only migration from the legacy `%APPDATA%/ImageViewer` directory, which is shared with CorineKit_Gazer
   - Color preset system (6 themes: white, dark, purple, blue, green, brown)

3. **LanguageResources.cs** - Multi-language support
   - Dictionary-based resource system
   - Supports Chinese, English, Japanese
   - All UI text retrieved via `LanguageResources.GetText(key, language)`

4. **RandomTraversalEngine.cs** - Full-coverage random traversal (no WPF dependency)
   - Owns the "which image comes next" decision; see Random Traversal below

5. **CanvasViewState.cs** - Canvas zoom/pan state and math (no WPF dependency)
   - `ZoomAt` / `PanBy` / `Clamp` / `ToContainer` / `ToLayout`; see Canvas Zoom/Pan below

6. **PathBreadcrumb.cs** - Directory path ↔ breadcrumb segments (no WPF dependency)
   - `PathBreadcrumb.Split(path)` returns `PathSegment { Label, FullPath, Clickable }`

7. **MediaFormats.cs** - The single list of supported extensions (no WPF dependency)
   - `ImageExtensions` / `VideoExtensions` / `AllExtensions`, plus `IsSupported(path)` and
     `Classify(path)` → `MediaKind { None, Image, Gif, Video }`
   - Directory scanning and the breadcrumb sibling menu both filter through `IsSupported`, so the
     two can no longer disagree (they used to: `.tiff` was only in the sibling check)

8. **GifPlayer.cs** - Frame-by-frame GIF playback
   - `Open(path)` keeps a `FileStream(FileShare.Read)` open and decodes with
     `BitmapCacheOption.OnDemand`, then a `DispatcherTimer` at `DispatcherPriority.Render` steps
     frames and raises `FrameReady`
   - Frame delay comes from each frame's `/grctlext/Delay` metadata (centiseconds, clamped to
     20..10000ms, 0 → 100ms); loop count from the NETSCAPE2.0 `/appext/Data` block (0 = infinite)
   - Measured on a 369-frame 1228x753 GIF: sequential stepping stays flat (last quarter only 1.75x
     the first, not ~7x), a cold jump to the final frame costs 6.5ms, and two full passes leave the
     process at 77MB — versus ~1.3GB if every frame were decoded and held. That is why there is no
     look-ahead cache and no full-frame cache
   - `Stop()` closes the stream, which invalidates every frame already handed out, so the caller
     must clear `Image.Source` (done in `TeardownMedia`)

9. **VideoSourceResolver.cs** - Makes `.webm` playable by WPF's `MediaElement` (no WPF dependency)
   - `MediaElement` runs on the **legacy** Media Foundation pipeline, which resolves byte-stream
     handlers **by file extension**; Windows' built-in table has `.mkv` but not `.webm`. The modern
     WinRT `MediaPlayer` plays `.webm` fine, and in-process `MFRegisterLocalByteStreamHandler`
     returns S_OK but is never consulted (proved with a `.myvid` control file)
   - So `Resolve(path)` creates an NTFS **hard link** with an `.mkv` extension (metadata only, no
     data copy, native hardware playback) and returns that path; other extensions pass through
     unchanged. Hard links cannot cross volumes (`ERROR_NOT_SAME_DEVICE`), so the alias directory is
     per-volume: `%LOCALAPPDATA%\CorineKitFetcher\media` when that is on the same volume, otherwise
     `<root>\.corinekit_media` (marked Hidden), with a real copy as last-resort fallback
   - Alias names are `SHA1(path|length|lastWriteUtcTicks)`, so re-resolving the same file reuses the
     existing link instead of adding another
   - `Release()` and the static `PurgeAliases()` only ever delete paths inside an alias directory,
     so a source file can never be removed; `App.OnStartup` calls `PurgeAliases()` to clean up after
     a crash

> Components 4-7 and 9 are deliberately kept free of WPF types so their logic can be exercised
> from a plain console/xunit project without spinning up a Window. `GifPlayer` is the exception: it
> needs `BitmapDecoder` and `DispatcherTimer`, but a console harness can still drive it by pumping a
> `DispatcherFrame`.

### Key Architectural Patterns

- **Image Caching**: LRU cache using `List<string>` with configurable size (default 5)
  - `viewedImages` stores recently viewed image paths
  - `viewedIndex` tracks current position in cache
  - Cache cleared when size changes via slider

- **Directory Navigation**: Two navigation modes
  - Random mode (←/→): Navigate through cached images
  - Sequential mode (↑/↓, or wheel in whichever hover zone the user assigned to it): Navigate images
    within current directory in order
  - Directory indexing via `directoryImageIndices` dictionary maps directories to image indices

- **Random Traversal** (`RandomTraversalEngine.cs`): Replaced the old `Random.Next` pick
  - Epoch-based: one full pass covers every image exactly once (no repeats, no omissions)
  - Per-directory decks; each pick selects the "hungriest" directory (highest remaining ratio)
    with random jitter, plus a penalty for the directory just served, so subdirectories interleave
  - Intra-directory order comes from `SpacedShuffle`: coprime-stride circular interleave followed
    by constrained random swaps that never drop the gap below ~n/3, which keeps consecutive picks
    far apart in the original file order
  - `Rebuild(imageFiles)` must be called before `Next()` whenever the file list changes

- **Canvas Zoom/Pan** (`CanvasViewState.cs` + gesture handlers in MainWindow)
  - Convention: `container point = layoutOrigin + layoutPoint * Scale + Offset`, matching the XAML
    `TransformGroup` order (ScaleTransform then TranslateTransform) with `RenderTransformOrigin="0,0"`
  - `Scale == 1` means "fit to window"; `FitMediaToView` fits to `RootGrid` (client area), not the
    window outer bounds, so the centering origin used by the zoom anchor is exact
  - `MinScale == 1`: fit is the floor, zooming out never shrinks below fit. Zoom-out stays purely
    cursor-anchored down to `FitEaseScale` (2x); only below that is the pan offset lerped toward
    (0,0) by `(newScale-1)/(FitEaseScale-1)`, so the last octave eases back to centered fit
    instead of snapping
  - The plain wheel never zooms: it switches images (which kind depends on the hover zone, see
    Indicator + Wheel Zones below). Zoom is right-button-held + wheel only; right or middle button
    held + drag = pan; right click without drag = reset; middle click without drag = legacy open action
  - `F` and right-click-without-drag both call `RefitCanvas()`, which re-runs `FitMediaToView`
    before resetting — resetting the transform alone is not enough, because the media is only
    re-fitted on media change, so after a window resize `Scale == 1` no longer means "fits the window"
  - `HintArea` (hint text + "restore session" button) sits *below* the Image in the XAML tree, so it
    must be collapsed as a whole once images load; collapsing only the TextBlock leaves the button
    visible and clickable once the user pans the image aside
  - Click vs drag is separated by `SystemParameters.MinimumHorizontalDragDistance`
  - `CanvasViewState` is pure math with no WPF dependency so the anchor invariant stays testable

- **Indicator + Wheel Zones** (`IndicatorPanel` in MainWindow.xaml, wheel logic in MainWindow.xaml.cs)
  - `IndicatorPanel` (cache-position dots) is persistent: hidden only until the first image loads,
    then always visible. The fade-in/out overlay and the black gradient scrim were removed
  - `Window_PreviewMouseWheel` owns all wheel semantics and is hit-zone driven, not geometry driven:
    right-button-held → `ZoomCanvasAt`; panning → swallow; hovering the open `SettingsPanel` →
    return **without** marking handled, so the panel's Slider/ComboBox still get the wheel;
    otherwise the zone is decided by whether the hit element is inside `PathButtonPanel`
    (breadcrumb) or not, and one zone does `Show*Image` (cached/random) while the other does
    `show*ImageInOrder` (sequential). Which one is random comes from `AppSettings.WheelRandomZone`
    (0 = breadcrumb, 1 = elsewhere), set by the `WheelZoneComboBox` in the settings panel and
    persisted like every other setting. There is no bubbling `Window_MouseWheel` handler any more
  - Zone detection uses `IsDescendantOf(e.OriginalSource, …)`, so a collapsed settings panel and the
    non-hit-testable drive breadcrumb segment both fall into "elsewhere"

- **Path Breadcrumbs** (`PathBreadcrumb.cs`): Dynamic button generation
  - Each directory level becomes a button; current directory highlighted in blue
  - Drive segment (`C:`) and UNC host segment (`\\server`) are display-only: `IsHitTestVisible=false`
  - Segments are built from the drive root (`C:\`) or UNC root (`\\server\share`), not by
    `Path.Combine("", "C:")` — the old approach produced drive-relative paths like `C:Users`
  - Right-click shows sibling directories (that contain supported media)
  - Middle-click opens folder in Windows Explorer (was Ctrl+click)

- **Session Persistence**: Last browsed folder path saved
  - Restored on app launch via "Restore Session" button
  - Saved immediately when directory changes

- **Media Dispatch** (`DisplayMedia` in MainWindow.xaml.cs, two rendering layers in MainWindow.xaml)
  - Every entry point (random pick, sequential step, cache step, session restore, drag-drop) funnels
    into `DisplayMedia(path)`, which sets `currentImagePath`, updates the breadcrumb and cache, then
    switches on `MediaFormats.Classify(path)`:
    `DisplayStillImage` (`BitmapImage`), `DisplayGif` (`GifPlayer` → the same `RandomImage` element),
    `DisplayVideo` (`MediaElement`)
  - The XAML has two sibling layers: `RandomImage` (Image) and `VideoPlayer` (MediaElement). The
    video layer is declared **after** the image so the settings panel, breadcrumb and indicator dots
    still composite above it — `MediaElement` is a HWND-backed surface, so anything declared before
    it would be hidden behind it
  - Both layers carry an identical `TransformGroup` (`VideoScaleTransform` + `VideoTranslateTransform`
    mirroring the image pair) and `ApplyCanvasTransform` writes both, so zoom/pan works on video
    without the gesture code knowing which layer is live; `ActiveMediaElement` reports whichever one
    is visible to the hit-testing and layout helpers
  - `TeardownMedia()` runs before any new media is shown and collapses **both** layers. Collapsing is
    not optional: a `MediaElement` whose `Source` was set to null still paints a black rectangle, so
    going from video back to an image would show black on top of the picture. `RandomImage.Source`
    must also be nulled because `OnDemand` GIF frames die together with the closed stream
  - `DisplayStillImage` decodes **before** tearing down, so a failed decode leaves the previous media
    on screen instead of blanking it
  - Still images use `UriSource` + `BitmapCacheOption.OnLoad` + `Freeze()`. `OnLoad` matters: with the
    default `OnDemand` the `BitmapImage` holds the file locked until the GC collects it (measured: not
    writable, not deletable while the reference is alive), whereas `OnLoad` + `Freeze` releases it
    immediately
  - `new Uri(path)` is safe for tricky filenames — .NET percent-escapes `#` and `%` when converting a
    Windows path to a file URI (`Fragment` stays empty, `LocalPath` round-trips). A hand-rolled
    escaping helper for this was written and then deleted after measurement disproved the premise;
    `UriProbe` in the throwaway harness pins that behaviour
  - Video natural size is unknown until `MediaOpened` fires, so `currentMediaNaturalSize` is left
    empty at dispatch time and `FitMediaToView` + `ReclampCanvas` run again from that handler
  - **Window close must call `TeardownMedia()` before the resolver is disposed.** `MediaElement` is
    `UnloadedBehavior="Manual"`, so WPF never closes the media session for us, and the `.mkv` alias
    is a hard link that Media Foundation still holds a handle on. `File.Delete` on it then blocks the
    UI thread until that handle is released — measured on a 3MB webm: Closing handler takes
    **4182ms** without teardown versus **11ms** with `Stop()` + `Close()` + `Source = null` first
    (repeatable: total close+shutdown 6732/6743ms vs 2286/2272ms). Past ~5s Windows marks the window
    "未响应", which is exactly the reported symptom. `StopVideo()` already has the right internal
    ordering (teardown, then `videoResolver.Release()`); the only mistake was never calling it from
    `Closing`

- **Failure Handling / Missing Codecs**: no per-file error dialogs
  - `DisplayMedia` treats a `false` return or a thrown decode as a failure, routes to
    `ReportMediaFailure`, which logs to console, shows a localized hint at most once per session
    (`ShowFormatHintOnce` distinguishes webp → `Microsoft.WebpImageExtension` from video →
    Web Media Extensions / VP9 / AV1), and re-deals from the traversal engine
  - `MaxConsecutiveMediaFailures` (30) breaks the recursion: a folder full of undecodable files would
    otherwise spin, so the user gets one message saying the codecs are probably missing
  - The counter resets on any successful display

### Important Implementation Details

- **Settings Save Triggers**: Settings save happens immediately when:
  - Cache size changes via slider (MainWindow.xaml.cs:117)
  - Color preset changes (via SetColorPreset)
  - Language changes (via SetLanguage)
  - Wheel random zone changes (via SetWheelRandomZone)
  - Directory path changes (MainWindow.xaml.cs:173)
  - Window closes (MainWindow.xaml.cs:53)

- **Supported Media Formats**: declared once in `MediaFormats.cs` — images `.jpg .jpeg .png .bmp .tif .tiff .webp`, animation `.gif`, video `.mp4 .m4v .mov .mkv .webm .avi .wmv .mpg .mpeg .3gp`. Do not re-list extensions elsewhere; both directory scanning and the breadcrumb sibling menu filter through `MediaFormats.IsSupported`

- **Codecs Are the OS's Job**: the app bundles no decoder. `.webp` needs the Store's
  `Microsoft.WebpImageExtension`; `.webm` needs Web Media Extensions (plus VP9/AV1 extensions for
  those codecs). Missing codecs degrade to "skip the file and hint once", never a modal error

- **Keyboard Shortcuts**:
  - Space: Random image
  - ←/→: Previous/next cached image
  - ↑/↓: Previous/next in directory order
  - Tab: Toggle settings panel
  - F: Re-fit to window and reset the canvas transform
  - P: Pause / resume the current GIF or video (`TogglePlayback`; no-op on a still image)
  - Double-click or middle-click: Open image in default app

- **Color System**: Presets 0-5 affect three color properties
  - Background color (main window)
  - Button color (path buttons, indicators)
  - Foreground color (text)

## Code Conventions

- **Namespace**: `ImageSelector` for most code, `Fetcher` for App.xaml.cs
- **Naming**: PascalCase for public members, camelCase for private fields
- **Path handling**: Use `Path = System.IO.Path` alias to avoid conflicts
- **Error handling**: Try-catch for file I/O with `MessageBox.Show` for user errors
- **Nullable**: Enabled in project (`<Nullable>enable</Nullable>`)
- **Implicit usings**: Enabled, common System namespaces auto-imported

## Common Modifications

### Adding New Language
1. Add value to `Language` enum in LanguageResources.cs:5-10
2. Add translations to `Resources` dictionary
3. Update `LanguageComboBox` in MainWindow.xaml

### Adding New Theme
1. Add case to switch statements in Settings.cs:
   - `GetBackgroundColorFromPresetID()` (line 42)
   - `GetButtonColorFromPresetID()` (line 64)
   - `GetForegroundColorFromPresetID()` (line 86)
2. Add corresponding button in MainWindow.xaml

### Adding Settings Property
1. Add property to `AppSettings` class in Settings.cs
2. Update UI binding in MainWindow.xaml.cs
3. Ensure `Save()` is called when property changes

## Known Issues

- Settings persistence recently fixed with backup mechanism (v1.0.1)
- Some UI strings may be hardcoded in XAML files
- No automated tests currently exist
