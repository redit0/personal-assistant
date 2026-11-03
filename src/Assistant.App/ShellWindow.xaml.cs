using System.ComponentModel;
using System.Windows;
using Assistant.Core;
using Microsoft.AspNetCore.Components.WebView;

namespace Assistant.App;

/// <summary>The main window. Closing it hides it back to the tray.</summary>
public partial class ShellWindow : Window
{
    private bool _exiting;

    public ShellWindow(IServiceProvider services, string startPath)
    {
        InitializeComponent();
        TitleBarIcon.Attach(this);
        WebView.Services = services;
        WebView.StartPath = "/" + startPath.TrimStart('/');
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        // Keep WebView2's profile with the rest of the app data instead of next to the exe.
        e.UserDataFolder = System.IO.Path.Combine(AppPaths.DataDirectory, "WebView2");
    }
}
