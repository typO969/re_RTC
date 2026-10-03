using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using rtc.Commands;
using rtc.Models;
using rtc.Services;

namespace rtc.ViewModels;

/// <summary>
/// Minimal MVVM MainViewModel for re_RTC.
/// Houses observable properties and command bindings with a clean, flat model footprint.
/// Features 1×Full as Canonical Standard Master, procedural 3x4, 4x5, 5x6 generation,
/// native WPF reMarkable 2 screen simulator with live vector graphics,
/// and bidirectional conversion between SVG vector files and native reMarkable *.template files.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private string _templateName = "pictograph_1xFull_master";
    private string _author = "Douglas";
    private int _columns = 1;
    private int _rows = 1;
    private string _selectedPreset = "1xFull";
    private string _orientation = "portrait"; // "portrait" or "landscape"
    private bool _useParametricScaling = true;
    private bool _preserveColors = true;
    private bool _snapGrayscale = false;
    private string _selectedFilePath = string.Empty;
    private string _jsonOutput = string.Empty;
    private string _svgOutput = string.Empty;
    private bool _isSvgViewActive = false;
    private string _statsText = string.Empty;
    private string _statusMessage = "Ready. 1xFull Master selected. Drop an SVG or *.template file.";
    private bool _isBusy;

    private ImageSource? _previewImage;
    private bool _showGridGuidelines = true;
    private double _zoomScale = 0.38;
    private string _activeRightTab = "simulator"; // "simulator", "template", "svg"

    private RemarkableTemplate? _masterTemplate;
    private RemarkableTemplate? _currentTemplate;

    public MainViewModel()
    {
        SelectPresetCommand = new RelayCommand(ExecuteSelectPreset);
        ConvertSvgCommand = new AsyncRelayCommand(ExecuteConvertSvgAsync);
        BrowseFileCommand = new AsyncRelayCommand(ExecuteBrowseFileAsync);
        CopyJsonCommand = new RelayCommand(ExecuteCopyJson);
        CopySvgCommand = new RelayCommand(ExecuteCopySvg);
        SaveTemplateCommand = new AsyncRelayCommand(ExecuteSaveTemplateAsync);
        SaveJsonCommand = new AsyncRelayCommand(ExecuteSaveJsonAsync);
        ExportSvgCommand = new AsyncRelayCommand(ExecuteExportSvgAsync);
        ExportAllSizesZipCommand = new AsyncRelayCommand(ExecuteExportAllSizesZipAsync);
        ToggleOutputViewCommand = new RelayCommand(ExecuteToggleOutputView);
        SelectRightTabCommand = new RelayCommand(p => ActiveRightTab = p?.ToString() ?? "simulator");
        ZoomInCommand = new RelayCommand(() => ZoomScale = Math.Min(1.5, ZoomScale + 0.05));
        ZoomOutCommand = new RelayCommand(() => ZoomScale = Math.Max(0.15, ZoomScale - 0.05));
        ResetZoomCommand = new RelayCommand(() => ZoomScale = 0.38);
    }

    #region Properties
    public string TemplateName
    {
        get => _templateName;
        set => SetField(ref _templateName, value);
    }

    public string Author
    {
        get => _author;
        set => SetField(ref _author, value);
    }

    public string Orientation
    {
        get => _orientation;
        set
        {
            if (SetField(ref _orientation, value))
            {
                OnPropertyChanged(nameof(SlotDimensionsText));
                OnPropertyChanged(nameof(IsPortrait));
                OnPropertyChanged(nameof(IsLandscape));
                if (_masterTemplate != null)
                {
                    _masterTemplate.Orientation = value;
                    CurrentTemplate = TemplateGridResizer.TransformGrid(_masterTemplate, Columns, Rows);
                    CurrentTemplate.Orientation = value;
                    UpdateOutputs(CurrentTemplate);
                }
            }
        }
    }

    public bool IsPortrait
    {
        get => Orientation != "landscape";
        set
        {
            if (value) Orientation = "portrait";
        }
    }

    public bool IsLandscape
    {
        get => Orientation == "landscape";
        set
        {
            if (value) Orientation = "landscape";
        }
    }

    public int Columns
    {
        get => _columns;
        set
        {
            if (SetField(ref _columns, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(SlotDimensionsText));
                OnPropertyChanged(nameof(Is1xFullMaster));
            }
        }
    }

    public int Rows
    {
        get => _rows;
        set
        {
            if (SetField(ref _rows, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(SlotDimensionsText));
                OnPropertyChanged(nameof(Is1xFullMaster));
            }
        }
    }

    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value))
            {
                OnPropertyChanged(nameof(Is1xFullMaster));
            }
        }
    }

    public bool Is1xFullMaster => Columns == 1 && Rows == 1;

    public string SlotDimensionsText
    {
        get
        {
            int screenW = IsLandscape ? 1872 : 1404;
            int screenH = IsLandscape ? 1404 : 1872;
            int slotW = screenW / Math.Max(1, Columns);
            int slotH = screenH / Math.Max(1, Rows);
            return $"{slotW} × {slotH} px ({Columns * Rows} {(Columns * Rows == 1 ? "slot" : "slots")}) [{Orientation}]";
        }
    }

    public bool UseParametricScaling
    {
        get => _useParametricScaling;
        set => SetField(ref _useParametricScaling, value);
    }

    public bool PreserveColors
    {
        get => _preserveColors;
        set => SetField(ref _preserveColors, value);
    }

    public bool SnapGrayscale
    {
        get => _snapGrayscale;
        set
        {
            if (SetField(ref _snapGrayscale, value) && CurrentTemplate != null)
            {
                UpdateOutputs(CurrentTemplate);
            }
        }
    }

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set => SetField(ref _previewImage, value);
    }

    public bool ShowGridGuidelines
    {
        get => _showGridGuidelines;
        set
        {
            if (SetField(ref _showGridGuidelines, value) && CurrentTemplate != null)
            {
                UpdateOutputs(CurrentTemplate);
            }
        }
    }

    public double ZoomScale
    {
        get => _zoomScale;
        set
        {
            if (SetField(ref _zoomScale, Math.Clamp(value, 0.15, 1.5)))
            {
                OnPropertyChanged(nameof(ZoomPercentText));
            }
        }
    }

    public string ZoomPercentText => $"{Math.Round(ZoomScale * 100)}%";

    public string ActiveRightTab
    {
        get => _activeRightTab;
        set
        {
            if (SetField(ref _activeRightTab, value))
            {
                OnPropertyChanged(nameof(IsSimulatorTabActive));
                OnPropertyChanged(nameof(IsTemplateTabActive));
                OnPropertyChanged(nameof(IsSvgTabActive));
            }
        }
    }

    public bool IsSimulatorTabActive => ActiveRightTab == "simulator";
    public bool IsTemplateTabActive => ActiveRightTab == "template";
    public bool IsSvgTabActive => ActiveRightTab == "svg";

    public double TabletNativeWidth => IsLandscape ? 1872.0 : 1404.0;
    public double TabletNativeHeight => IsLandscape ? 1404.0 : 1872.0;

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set => SetField(ref _selectedFilePath, value);
    }

    public string JsonOutput
    {
        get => _jsonOutput;
        set => SetField(ref _jsonOutput, value);
    }

    public string SvgOutput
    {
        get => _svgOutput;
        set => SetField(ref _svgOutput, value);
    }

    public bool IsSvgViewActive
    {
        get => _isSvgViewActive;
        set
        {
            if (SetField(ref _isSvgViewActive, value))
            {
                OnPropertyChanged(nameof(CurrentOutputText));
                OnPropertyChanged(nameof(CurrentOutputHeader));
            }
        }
    }

    public string CurrentOutputText => IsSvgViewActive ? SvgOutput : JsonOutput;

    public string CurrentOutputHeader => IsSvgViewActive ? "Reconstructed SVG Vector Output (.svg)" : "Generated reMarkable Template (.template / JSON)";

    public string StatsText
    {
        get => _statsText;
        set => SetField(ref _statsText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public RemarkableTemplate? CurrentTemplate
    {
        get => _currentTemplate;
        private set => SetField(ref _currentTemplate, value);
    }
    #endregion

    #region Commands
    public ICommand SelectPresetCommand { get; }
    public ICommand ConvertSvgCommand { get; }
    public ICommand BrowseFileCommand { get; }
    public ICommand CopyJsonCommand { get; }
    public ICommand CopySvgCommand { get; }
    public ICommand SaveTemplateCommand { get; }
    public ICommand SaveJsonCommand { get; }
    public ICommand ExportSvgCommand { get; }
    public ICommand ExportAllSizesZipCommand { get; }
    public ICommand ToggleOutputViewCommand { get; }
    public ICommand SelectRightTabCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    private void ExecuteSelectPreset(object? parameter)
    {
        string preset = parameter?.ToString() ?? "1xFull";
        SelectedPreset = preset;

        switch (preset)
        {
            case "1xFull":
                Columns = 1;
                Rows = 1;
                TemplateName = TemplateGridResizer.GetResizedTemplateName(TemplateName, 1, 1);
                StatusMessage = "Selected 1×Full Standard Master (1404 × 1872 px). Draw once here; grids are procedurally derived.";
                break;
            case "3x4":
                Columns = 3;
                Rows = 4;
                TemplateName = TemplateGridResizer.GetResizedTemplateName(TemplateName, 3, 4);
                StatusMessage = "Selected 3 × 4 Grid (12 slots, 468 × 468 px).";
                break;
            case "4x5":
                Columns = 4;
                Rows = 5;
                TemplateName = TemplateGridResizer.GetResizedTemplateName(TemplateName, 4, 5);
                StatusMessage = "Selected 4 × 5 Grid (20 slots, 351 × 374 px).";
                break;
            case "5x6":
                Columns = 5;
                Rows = 6;
                TemplateName = TemplateGridResizer.GetResizedTemplateName(TemplateName, 5, 6);
                StatusMessage = "Selected 5 × 6 Grid (30 slots, 280 × 312 px).";
                break;
        }

        if (_masterTemplate != null)
        {
            CurrentTemplate = TemplateGridResizer.TransformGrid(_masterTemplate, Columns, Rows);
            UpdateOutputs(CurrentTemplate);
        }
    }

    public async Task LoadFileAndConvertAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        SelectedFilePath = filePath;
        await ExecuteConvertSvgAsync();
    }

    private async Task ExecuteBrowseFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Supported files (*.svg;*.template;*.json)|*.svg;*.template;*.json|SVG Vector Files (*.svg)|*.svg|reMarkable Template (*.template)|*.template|JSON Templates (*.json)|*.json|All Files (*.*)|*.*",
            Title = "Select SVG or reMarkable Template (*.svg, *.template, *.json)"
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadFileAndConvertAsync(dialog.FileName);
        }
    }

    private async Task ExecuteConvertSvgAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
        {
            StatusMessage = "Please select or drop an SVG or *.template file first.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Processing...";

            var ext = Path.GetExtension(SelectedFilePath).ToLowerInvariant();
            if (ext == ".template" || ext == ".json" || SelectedFilePath.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase))
            {
                var json = await File.ReadAllTextAsync(SelectedFilePath);
                var loaded = JsonSerializer.Deserialize<RemarkableTemplate>(json);
                if (loaded != null)
                {
                    _masterTemplate = loaded;
                    Orientation = loaded.Orientation ?? Orientation;
                    CurrentTemplate = TemplateGridResizer.TransformGrid(_masterTemplate, Columns, Rows);
                    TemplateName = CurrentTemplate.Name;
                    Author = CurrentTemplate.Author ?? Author;

                    UpdateOutputs(CurrentTemplate);
                    StatusMessage = $"Loaded *.template file! Bidirectionally converted to SVG and rendered at {Columns}×{Rows} [{Orientation}].";
                }
            }
            else
            {
                var settings = new ConversionSettings
                {
                    TemplateName = TemplateName,
                    Author = Author,
                    Orientation = Orientation,
                    GridCols = Columns,
                    GridRows = Rows,
                    UseParametricScaling = UseParametricScaling,
                    DecimalPrecision = 4,
                    SnapToEinkGrayscale = SnapGrayscale,
                    ExportFullColor = PreserveColors
                };

                // Convert the SVG file to template model
                var template = await SvgToRemarkableConverter.ConvertSvgFileToTemplateAsync(SelectedFilePath, settings);
                if (template != null)
                {
                    _masterTemplate = (Columns == 1 && Rows == 1) ? template : TemplateGridResizer.TransformGrid(template, 1, 1);
                    CurrentTemplate = template;

                    UpdateOutputs(template);
                    StatusMessage = $"Successfully converted SVG to {Columns}×{Rows} *.template [{Orientation}]! (1xFull Master cached).";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateOutputs(RemarkableTemplate template)
    {
        JsonOutput = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
        SvgOutput = RemarkableToSvgConverter.ConvertTemplateToSvgString(template);
        PreviewImage = TabletPreviewRenderer.RenderToDrawingImage(template, ShowGridGuidelines, SnapGrayscale);
        OnPropertyChanged(nameof(CurrentOutputText));
        OnPropertyChanged(nameof(TabletNativeWidth));
        OnPropertyChanged(nameof(TabletNativeHeight));
    }

    private void ExecuteCopyJson()
    {
        if (string.IsNullOrWhiteSpace(JsonOutput)) return;
        Clipboard.SetText(JsonOutput);
        StatusMessage = "Template JSON / *.template copied to clipboard!";
    }

    private void ExecuteCopySvg()
    {
        if (string.IsNullOrWhiteSpace(SvgOutput)) return;
        Clipboard.SetText(SvgOutput);
        StatusMessage = "Reconstructed SVG XML copied to clipboard!";
    }

    private async Task ExecuteSaveTemplateAsync()
    {
        if (string.IsNullOrWhiteSpace(JsonOutput)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "reMarkable Template (*.template)|*.template|JSON Template (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".template",
            FileName = $"{TemplateName}.template"
        };
        if (dialog.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonOutput);
            StatusMessage = $"Saved tablet template to {Path.GetFileName(dialog.FileName)}.";
        }
    }

    private async Task ExecuteSaveJsonAsync()
    {
        await ExecuteSaveTemplateAsync();
    }

    private async Task ExecuteExportSvgAsync()
    {
        if (string.IsNullOrWhiteSpace(SvgOutput))
        {
            if (CurrentTemplate != null)
            {
                SvgOutput = RemarkableToSvgConverter.ConvertTemplateToSvgString(CurrentTemplate);
            }
            else
            {
                StatusMessage = "Convert or load a template before exporting SVG.";
                return;
            }
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Scalable Vector Graphics (*.svg)|*.svg|All Files (*.*)|*.*",
            DefaultExt = ".svg",
            FileName = $"{TemplateName}.svg"
        };
        if (dialog.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, SvgOutput);
            StatusMessage = $"Exported template to vector SVG file: {Path.GetFileName(dialog.FileName)}.";
        }
    }

    private void ExecuteToggleOutputView(object? param)
    {
        if (param is string mode)
        {
            IsSvgViewActive = mode.Equals("svg", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            IsSvgViewActive = !IsSvgViewActive;
        }
    }

    private async Task ExecuteExportAllSizesZipAsync()
    {
        if (CurrentTemplate == null && _masterTemplate == null)
        {
            StatusMessage = "Convert or load a template first before exporting.";
            return;
        }

        var source = _masterTemplate ?? CurrentTemplate!;
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP Archive (*.zip)|*.zip",
            FileName = $"{TemplateName.Replace("_1xFull", "").Replace("_3x4", "").Replace("_4x5", "").Replace("_5x6", "")}_AllSizes_Package.zip"
        };

        if (dialog.ShowDialog() == true)
        {
            IsBusy = true;
            try
            {
                using var zipStream = new FileStream(dialog.FileName, FileMode.Create);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                var allSizes = TemplateGridResizer.GenerateAllStandardSizes(source);

                foreach (var (sizeName, tpl) in allSizes)
                {
                    // 1. Output native *.template file
                    var templateEntry = archive.CreateEntry($"{tpl.Name}.template");
                    using (var writer = new StreamWriter(templateEntry.Open()))
                    {
                        await writer.WriteAsync(JsonSerializer.Serialize(tpl, new JsonSerializerOptions { WriteIndented = true }));
                    }

                    // 2. Output reconstructed SVG file (bidirectional verification)
                    var svgEntry = archive.CreateEntry($"{tpl.Name}.svg");
                    using (var writer = new StreamWriter(svgEntry.Open()))
                    {
                        await writer.WriteAsync(RemarkableToSvgConverter.ConvertTemplateToSvgString(tpl));
                    }
                }

                // 3. Add templates.json registry entry
                var registryEntry = archive.CreateEntry("templates_registry_entry.json");
                using (var writer = new StreamWriter(registryEntry.Open()))
                {
                    var snippet = new
                    {
                        templates = new[]
                        {
                            new { name = allSizes["1xFull"].Name, filename = allSizes["1xFull"].Name, iconCode = "\ue9df", landscape = allSizes["1xFull"].Orientation == "landscape", categories = new[] { "Custom", "Master" } },
                            new { name = allSizes["3x4"].Name, filename = allSizes["3x4"].Name, iconCode = "\ue9df", landscape = allSizes["3x4"].Orientation == "landscape", categories = new[] { "Custom", "Grids" } },
                            new { name = allSizes["4x5"].Name, filename = allSizes["4x5"].Name, iconCode = "\ue9df", landscape = allSizes["4x5"].Orientation == "landscape", categories = new[] { "Custom", "Grids" } },
                            new { name = allSizes["5x6"].Name, filename = allSizes["5x6"].Name, iconCode = "\ue9df", landscape = allSizes["5x6"].Orientation == "landscape", categories = new[] { "Custom", "Grids" } }
                        }
                    };
                    await writer.WriteAsync(JsonSerializer.Serialize(snippet, new JsonSerializerOptions { WriteIndented = true }));
                }

                StatusMessage = "Successfully exported all 4 sizes (.template & .svg) into ZIP package!";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
    #endregion
}
