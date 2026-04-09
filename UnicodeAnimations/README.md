# unicode-animations — WinUI 3

A faithful WinUI 3 / C# port of [gunnargray-dev/unicode-animations](https://github.com/gunnargray-dev/unicode-animations).

All 18 braille spinners animate live in a dark-themed Fluent Design window.

## Preview

```
⡆⢰⢸⣸⣼⣾⣷⣾⣽⣻⣟⡿  braille     80 ms
⠁⠂⠄⡀⢀⠠⠐⠈        braillewave 100 ms
⠋⠉⠙⠚⠒⠂…          dna         80 ms
⣀⣄⣤⣦…             scan        70 ms
…and 14 more
```

## Requirements

| Tool | Minimum version |
|---|---|
| Windows | 10 version 1809 (build 17763) |
| .NET | 8.0 |
| Windows App SDK | 1.5 |
| Visual Studio | 2022 17.8+ (with "Windows application development" workload) |

## Build & Run

```
git clone <this-repo>
cd UnicodeAnimations
dotnet build
dotnet run
```

Or open `UnicodeAnimations.csproj` in Visual Studio 2022 and press **F5**.

> No MSIX packaging is required — the app runs unpackaged via
> `<WindowsPackageType>None</WindowsPackageType>`.

## Project structure

```
UnicodeAnimations/
├── Models/
│   ├── BrailleUtils.cs      ← gridToBraille / makeGrid (ported from TS)
│   ├── Spinner.cs           ← record Spinner(string[] Frames, int Interval)
│   └── SpinnerRegistry.cs   ← all 18 generators + registry
├── ViewModels/
│   └── SpinnerViewModel.cs  ← INotifyPropertyChanged + DispatcherQueueTimer
├── App.xaml / App.xaml.cs
├── MainWindow.xaml          ← dark Fluent UI, WrapPanel grid of cards
└── MainWindow.xaml.cs       ← custom title bar, window sizing
```

## License

MIT — same as the original project.
