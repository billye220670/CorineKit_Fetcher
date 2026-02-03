# AGENTS.md - CorineKit Fetcher Development Guide

## Project Overview

CorineKit Fetcher is a WPF-based image random selection tool built with .NET 9.0 Windows. The application allows users to browse folders of images randomly, navigate through cached images, and customize the interface with themes and languages.

**Tech Stack:** C# 9.0, .NET 9.0 Windows, WPF, XML serialization for settings

---

## Build Commands

### Restore Dependencies
```bash
dotnet restore
```

### Build Solution
```bash
dotnet build
```

### Build Release
```bash
dotnet build --configuration Release
```

### Build Single Project
```bash
dotnet build Fetcher/Fetcher.csproj
```

### Clean Build Artifacts
```bash
dotnet clean
```

### Rebuild
```bash
dotnet rebuild
```

### Publish Application
```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

**Output Location:** `Fetcher/bin/Release/net9.0-windows/`

---

## Testing

This project currently has no automated tests. When adding tests:

### Create Test Project
```bash
dotnet new xunit -n Fetcher.Tests
dotnet add Fetcher.Tests/Fetcher.Tests.csproj reference Fetcher/Fetcher.csproj
```

### Run All Tests
```bash
dotnet test
```

### Run Single Test (by filter)
```bash
dotnet test --filter "FullyQualifiedName~TestClassName"
```

### Run Tests with Verbose Output
```bash
dotnet test -v normal
```

---

## Code Style Guidelines

### Naming Conventions

- **Classes/Methods/Properties:** PascalCase (e.g., `MainWindow`, `LoadImagesFromDirectory`, `CurrentLanguage`)
- **Private Fields:** camelCase with underscore prefix optional (e.g., `imageFiles`, `cacheSize`)
- **Constants:** PascalCase (e.g., `DoubleClickInterval`)
- **Enums:** PascalCase (e.g., `Language.Chinese`)
- **Interface Names:** Prefix with `I` (e.g., `ISettings`)

### File Organization

- One public class per file, filename matches class name
- Source files go in `Fetcher/` directory
- XAML files alongside their code-behind (e.g., `MainWindow.xaml` and `MainWindow.xaml.cs`)

### Imports and Namespaces

- Use implicit usings (already enabled in `.csproj`)
- Namespace: `ImageSelector` for most code, `Fetcher` for App.xaml.cs
- Sort imports alphabetically when explicitly specified
- Use alias for path conflicts: `using Path = System.IO.Path`

### Types and Variables

- Enable nullable reference types (`<Nullable>enable</Nullable>` in `.csproj`)
- Use `var` when type is obvious from context
- Prefer `List<T>` over arrays for dynamic collections
- Use LINQ for collection operations (`Where`, `Any`, `FirstOrDefault`)
- Use explicit types when clarity is needed

### Method Design

- Keep methods focused and small (under 50 lines when possible)
- Use parameters for input, return values for output
- Use async/await for I/O operations
- Document public methods with XML comments

### Error Handling

- Use try-catch for file system and I/O operations
- Show user-friendly error messages via `MessageBox.Show`
- Log errors to console for debugging: `Console.WriteLine($"Error: {ex.Message}")`
- Avoid empty catch blocks; at minimum log the error
- Use specific exceptions (`UnauthorizedAccessException`, `DirectoryNotFoundException`)

### Control Flow

- Use switch statements for multiple conditions
- Use ternary operators for simple conditions
- Prefer expression-bodied members for simple getters
- Add braces even for single-line if statements for consistency

### UI/WPF Specific

- Use MVVM patterns for complex UIs
- Handle nullable types from UI events (e.g., `button.Tag?.ToString()`)
- Use event handlers with sender/e args pattern
- Restore settings on window load, save on closing
- Use `string.IsNullOrEmpty()` for string validation

### Code Comments

- Write comments in the language matching the project context
- Explain "why", not "what"
- Remove commented-out code before committing
- Use TODO comments sparingly: `// TODO: [description]`

### Formatting

- Indent with 4 spaces (VS default)
- Opening brace on new line for methods, same line for object initializers
- Line length: target under 120 characters
- No extra whitespace at line ends
- Use `new Thickness(value)` for margins and padding

---

## Project Structure

```
CorineKit_Fetcher/
├── Fetcher.sln              # Solution file
├── Fetcher/
│   ├── Fetcher.csproj       # Project configuration
│   ├── App.xaml             # Application entry point
│   ├── App.xaml.cs
│   ├── MainWindow.xaml      # Main UI window
│   ├── MainWindow.xaml.cs   # Main window logic
│   ├── Settings.cs          # Settings management (XML serialization)
│   ├── LanguageResources.cs # Multi-language support
│   └── FetcherIcon.ico      # Application icon
└── README.md                # Project documentation
```

---

## Common Development Tasks

### Add New Settings
1. Add property to `AppSettings` class in `Settings.cs`
2. Update `Save()` and `Load()` methods if needed
3. Add UI binding in `MainWindow.xaml.cs`

### Add New Language
1. Add enum value to `Language` enum
2. Add translation entries to `LanguageResources.Resources` dictionary
3. Update `LanguageComboBox_SelectionChanged` handler if needed

### Add New Theme
1. Add case to color preset switch statements in `Settings.cs`
2. Update `ColorPreset` range validation

---

## Git Workflow

- Create feature branches for new features
- Commit frequently with descriptive messages
- Push to remote for backup
- No force pushes to main branch

---

## Additional Notes

- Settings are stored in `%APPDATA%/ImageViewer/settings.xml`
- Supported image formats: .jpg, .jpeg, .png, .bmp, .gif, .tiff
- Application uses `ImplicitUsings` - no manual using statements needed for common namespaces
