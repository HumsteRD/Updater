using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Updater.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => RoundCorners();
        StateChanged += (_, _) => OnStateChanged();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        // Развёрнутое окно с собственной рамкой вылезает за экран на толщину рамки — компенсируем.
        RootFrame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        RootFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Скруглённые углы окна в Windows 11, как у системных окон.</summary>
    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE — тёмная тень и рамка
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }
}
