using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace rtc.ViewModels;

/// <summary>
/// Minimal MVVM Base class providing INotifyPropertyChanged support.
/// Pure standard library (.NET 10 LTS) with zero third-party dependencies.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
