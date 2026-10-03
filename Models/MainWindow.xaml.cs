using System;
using System.IO;
using System.Windows;
using rtc.ViewModels;

namespace rtc;

/// <summary>
/// Minimal MVVM MainWindow for re_RTC.
/// Code-behind is strictly limited to view attachment and shell events.
/// All logic, property notifications, and command bindings live in MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = (DataContext as MainViewModel) ?? new MainViewModel();
        DataContext = ViewModel;
    }

    #region Drag & Drop Shell Events
    private void Border_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Border_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is { Length: > 0 })
                {
                    await ViewModel.LoadFileAndConvertAsync(files[0]);
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Drop error: {ex.Message}";
        }
    }
    #endregion
}
