using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DosboxEnhanced;
public sealed partial class MainWindow
{
    readonly TextBlock updateNotice = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
    readonly CancellationTokenSource updateCancellation = new();
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(24) };
    bool updateChecking;
    void InitializeUpdateNotice()
    {
        menu.Children.Add(updateNotice);
        // Background automation and test windows do not need Internet discovery.
        if (BackgroundAutomation) return;
        Loaded += async (_, _) => { await CheckForRelease(); if (!disposed) updateTimer.Start(); };
        updateTimer.Tick += async (_, _) => await CheckForRelease();
    }
    async Task CheckForRelease()
    {
        if (disposed || updateChecking) return;
        updateChecking = true;
        try
        {
            var tag = await ReleaseUpdates.Check(updateCancellation.Token);
            if (disposed) return;
            updateNotice.Inlines.Clear();
            if (tag is null) { updateNotice.Visibility = Visibility.Collapsed; return; }
            var link = new Hyperlink(new Run($"Update available: {tag}")) { Foreground = Brushes.LightGoldenrodYellow, ToolTip = "Open the DOSBox Enhanced releases on GitHub" };
            link.Click += (_, _) => { ReleaseMouseCapture(); try { Process.Start(new ProcessStartInfo(ReleaseUpdates.Page) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
            updateNotice.Inlines.Add(link);
            System.Windows.Automation.AutomationProperties.SetName(updateNotice, $"DOSBox Enhanced update available: {tag}. Open GitHub.");
            updateNotice.Visibility = Visibility.Visible;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.IO.IOException)
        { /* Offline/limited discovery never interrupts emulation or opens a dialog. */ }
        finally { updateChecking = false; if (disposed) updateCancellation.Dispose(); }
    }
    void StopUpdateNotice() { updateTimer.Stop(); updateCancellation.Cancel(); if (!updateChecking) updateCancellation.Dispose(); }
}
