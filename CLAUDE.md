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

### Key Architectural Patterns

- **Image Caching**: LRU cache using `List<string>` with configurable size (default 5)
  - `viewedImages` stores recently viewed image paths
  - `viewedIndex` tracks current position in cache
  - Cache cleared when size changes via slider

- **Directory Navigation**: Two navigation modes
  - Random mode (←/→): Navigate through cached images
  - Sequential mode (↑/↓ or Ctrl+scroll): Navigate images within current directory in order
  - Directory indexing via `directoryImageIndices` dictionary maps directories to image indices

- **Path Breadcrumbs**: Dynamic button generation
  - Each directory level becomes a clickable button
  - Current directory highlighted in blue
  - Right-click shows sibling directories (that contain images)
  - Ctrl+click opens folder in Windows Explorer

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
