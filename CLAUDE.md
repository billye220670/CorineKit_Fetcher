# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CorineKit Fetcher is a WPF-based image random selection tool for Windows. Users can drag folders into the application to randomly browse images with caching support, keyboard shortcuts, multi-language UI (Chinese/English/Japanese), and theme customization.

**Tech Stack**: C# 9.0, .NET 9.0 Windows, WPF, XML serialization

## Build Commands

```bash
# Build the project
dotnet build

# Build release version
dotnet build --configuration Release

# Publish standalone executable
dotnet publish -c Release -r win-x64 --self-contained false

# Clean build artifacts
dotnet clean
```

**Build Output**: `Fetcher/bin/Release/net9.0-windows/`

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
   - Settings stored at `%APPDATA%/ImageViewer/settings.xml`
   - Automatic backup to `settings_backup.xml` on save
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

> Components 4-6 are deliberately kept free of WPF types so their logic can be exercised
> from a plain console/xunit project without spinning up a Window.

### Key Architectural Patterns

- **Image Caching**: LRU cache using `List<string>` with configurable size (default 5)
  - `viewedImages` stores recently viewed image paths
  - `viewedIndex` tracks current position in cache
  - Cache cleared when size changes via slider

- **Directory Navigation**: Two navigation modes
  - Random mode (←/→): Navigate through cached images
  - Sequential mode (↑/↓, or wheel in the top half of the window): Navigate images within current
    directory in order
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
  - `Scale == 1` means "fit to window"; `ScaleImageToFit` fits to `RootGrid` (client area), not the
    window outer bounds, so the centering origin used by the zoom anchor is exact
  - `MinScale == 1`: fit is the floor, zooming out never shrinks below fit. Zoom-out stays purely
    cursor-anchored down to `FitEaseScale` (2x); only below that is the pan offset lerped toward
    (0,0) by `(newScale-1)/(FitEaseScale-1)`, so the last octave eases back to centered fit
    instead of snapping
  - The plain wheel never zooms: it switches images (top half = sequential, bottom half = cached).
    Zoom is right-button-held + wheel only; right or middle button held + drag = pan;
    right click without drag = reset; middle click without drag = legacy open action
  - `F` and right-click-without-drag both call `RefitCanvas()`, which re-runs `ScaleImageToFit`
    before resetting — resetting the transform alone is not enough, because the image is only
    re-fitted on image change, so after a window resize `Scale == 1` no longer means "fits the window"
  - `HintArea` (hint text + "restore session" button) sits *below* the Image in the XAML tree, so it
    must be collapsed as a whole once images load; collapsing only the TextBlock leaves the button
    visible and clickable once the user pans the image aside
  - Click vs drag is separated by `SystemParameters.MinimumHorizontalDragDistance`
  - `CanvasViewState` is pure math with no WPF dependency so the anchor invariant stays testable

- **Indicator + Wheel Halves** (`IndicatorPanel` in MainWindow.xaml, wheel logic in MainWindow.xaml.cs)
  - `IndicatorPanel` (cache-position dots) is persistent: hidden only until the first image loads,
    then always visible. The fade-in/out overlay and the black gradient scrim were removed
  - `Window_PreviewMouseWheel` owns all wheel semantics and always marks the event handled:
    right-button-held → `ZoomCanvasAt`; panning → swallow; otherwise top half of `RootGrid` →
    `show*ImageInOrder`, bottom half → `Show*Image` (cached). There is no bubbling
    `Window_MouseWheel` handler any more

- **Path Breadcrumbs** (`PathBreadcrumb.cs`): Dynamic button generation
  - Each directory level becomes a button; current directory highlighted in blue
  - Drive segment (`C:`) and UNC host segment (`\\server`) are display-only: `IsHitTestVisible=false`
  - Segments are built from the drive root (`C:\`) or UNC root (`\\server\share`), not by
    `Path.Combine("", "C:")` — the old approach produced drive-relative paths like `C:Users`
  - Right-click shows sibling directories (that contain images)
  - Middle-click opens folder in Windows Explorer (was Ctrl+click)

- **Session Persistence**: Last browsed folder path saved
  - Restored on app launch via "Restore Session" button
  - Saved immediately when directory changes

### Important Implementation Details

- **Settings Save Triggers**: Settings save happens immediately when:
  - Cache size changes via slider (MainWindow.xaml.cs:117)
  - Color preset changes (via SetColorPreset)
  - Language changes (via SetLanguage)
  - Directory path changes (MainWindow.xaml.cs:173)
  - Window closes (MainWindow.xaml.cs:53)

- **Supported Image Formats**: `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif` (hardcoded in MainWindow.xaml.cs:193 and Settings.cs:456)

- **Keyboard Shortcuts**:
  - Space: Random image
  - ←/→: Previous/next cached image
  - ↑/↓: Previous/next in directory order
  - Tab: Toggle settings panel
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
