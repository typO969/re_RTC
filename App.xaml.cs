using System;
using System.Windows;

namespace rtc;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Silent global exception containment
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            // Log or handle gracefully without hard crashes
        };
    }
}
