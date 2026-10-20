using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Automation;

namespace TokenPulse.Windows;

internal static class Ui
{
    public static readonly SolidColorBrush Ink = Brush("#25263D"), Muted = Brush("#73778C"), Accent = Brush("#7162DF"),
        Green = Brush("#228A6A"), Blue = Brush("#478BCC"), Amber = Brush("#B9792E");
    public static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    public static TextBlock Text(string value, double size = 12, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, Foreground = color ?? Ink, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Glyph(string code, double size = 14, Brush? color = null) => new()
    {
        Text = code, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
        Foreground = color ?? Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    public static Border Card(UIElement content, Thickness? padding = null) => new()
    {
        Child = content, Padding = padding ?? new Thickness(13), Background = Brushes.White,
        BorderBrush = Brush("#E9EAF2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11)
    };
    public static Border Badge(string text, Brush? foreground = null, string background = "#F0EEFC") => new()
    {
        Child = Text(text, 10, foreground ?? Accent, FontWeights.Medium), Background = Brush(background),
        Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center
    };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.FindResource(primary ? "PrimaryButton" : "SoftButton") };
        button.Click += (_, _) => action(); return button;
    }
    public static Button IconButton(string glyph, string label, Action action)
    {
        var button = new Button { Content = Glyph(glyph), ToolTip = label, Style = (Style)Application.Current.FindResource("IconButton") };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    public static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid();
        foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }
    public static void Put(Grid grid, UIElement child, int column) { Grid.SetColumn(child, column); grid.Children.Add(child); }
    public static void Gap(Panel panel, double height = 12) => panel.Children.Add(new Border { Height = height });
    public static Grid Header(Window window, string title, string subtitle, params Button[] actions)
    {
        var header = Columns(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        header.Background = Brushes.Transparent;
        header.MouseLeftButtonDown += (_, e) =>
        {
            var target = e.OriginalSource as DependencyObject;
            while (target is not null && target is not System.Windows.Controls.Button)
                target = target is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(target);
            if (target is null && e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        var logo = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(Color.FromRgb(143, 126, 237), Color.FromRgb(103, 84, 206), 45), Margin = new Thickness(0, 0, 10, 0) };
        logo.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 10,0 L 1,13 L 8,13 L 6,23 L 18,8 L 11,8 Z"),
            Fill = Brushes.White, Stretch = Stretch.Uniform, Width = 15, Height = 23, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Put(header, logo, 0);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 17, Ink, FontWeights.SemiBold));
        labels.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0) });
        Put(header, labels, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in actions) buttons.Children.Add(button);
        buttons.Children.Add(IconButton("\uE711", "关闭窗口", window.Close));
        Put(header, buttons, 2); return header;
    }
    public static void ConfigureWindow(Window window)
    {
        window.Style = (Style)Application.Current.FindResource(typeof(Window));
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14), UseAeroCaptionButtons = false });
        window.SourceInitialized += (_, _) =>
        {
            var preference = 2; // DWMWCP_ROUND; unsupported Windows versions safely ignore the request.
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 33, ref preference, sizeof(int));
        };
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static Border Shell(UIElement content) => new()
    {
        Child = content, Background = Brush("#F7F8FC"), BorderBrush = Brush("#DFE2ED"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(0)
    };
}
