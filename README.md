# re_RTC: reMarkable 2 Template Converter (.NET 10 LTS)

Bidirectional desktop tool to convert SVG vector artwork into native reMarkable 2 `*.template` files, and vice versa (`*.template` => SVG).

## Key Capabilities & Updates
- **Native reMarkable 2 Screen Simulator**: Hardware-accelerated WPF canvas renderer rendering 1404×1872 / 1872×1404 vectors, e-ink Carta paper texture, signature aluminum left spine, zoom controls (-, +, Fit), and dashed slot guidelines.
- **Bidirectional Conversion**: Converts **SVG => *.template** and **.template => SVG**.
- **Native `*.template` File Extension**: reMarkable 2 tablets expect and read templates saved with the `*.template` extension in `/usr/share/remarkable/templates/`. The app now defaults to outputting `*.template` files.
- **Portrait & Landscape Orientation**: Switch freely between Portrait (1404 × 1872 px) and Landscape (1872 × 1404 px) template generation with automatic aspect-ratio scaling and slot centering.
- **Clean Minimal MVVM**: Pure .NET 10 LTS `INotifyPropertyChanged` in `ViewModelBase` and `MainViewModel` with expressive `RelayCommand` / `AsyncRelayCommand` bindings. Zero external MVVM package bloat.
- **1×Full Standard Master**: Author once at full tablet resolution; 3×4, 4×5, and 5×6 templates are procedurally derived on demand.
- **9 reMarkable Colors**: Fully preserves the 9 official reMarkable tablet pen and document colors for high-res vector PDF & SVG exports (Black, Gray, White, Blue, Red, Green, Yellow, Cyan, Magenta).

## How to Build & Run
1. Open `rtc.csproj` in **Visual Studio 2026 Community Edition** (with .NET Desktop Development workload).
2. Or run via command line:
   ```bash
   dotnet build
   dotnet run
   ```
