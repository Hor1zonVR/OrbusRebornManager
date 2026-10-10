using System;
using System.Windows;
using Velopack;

namespace OrbusRebornManager;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Update/install hooks must run before WPF creates its first window.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
