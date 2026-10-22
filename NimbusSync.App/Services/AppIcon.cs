using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimbusSync.App.ViewModels;

namespace NimbusSync.App.Services;

/// <summary>
/// The app's cloud mark, drawn in code so the tray icon can carry a status badge
/// without shipping one .ico per state. Icons are cached, so their handles live for the process.
/// </summary>
public static class AppIcon
{
    private static readonly Dictionary<StatusKind, System.Drawing.Icon> TrayIcons = new();

    private static readonly Color Brand = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0x8F, 0x98);

    /// <summary>Cloud silhouette in a 32×32 box.</summary>
    public static Geometry CloudGeometry { get; } = CreateCloud();

    public static ImageSource WindowIcon { get; } = Render(StatusKind.Ok, 64);

    public static System.Drawing.Icon TrayIcon(StatusKind kind)
    {
        lock (TrayIcons)
        {
            if (TrayIcons.TryGetValue(kind, out var icon)) return icon;

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(Render(kind, 32)));
            using var stream = new MemoryStream();
            png.Save(stream);
            stream.Position = 0;
            using var bitmap = new System.Drawing.Bitmap(stream);
            icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
            TrayIcons[kind] = icon;
            return icon;
        }
    }

    public static BitmapSource Render(StatusKind kind, int size)
    {
        var scale = size / 32.0;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            var cloudColor = kind is StatusKind.Paused or StatusKind.Offline ? Muted : Brand;
            dc.DrawGeometry(new SolidColorBrush(cloudColor), null, CloudGeometry);

            if (BadgeColor(kind) is { } badge)
            {
                var center = new Point(25, 24.5);
                dc.DrawEllipse(new SolidColorBrush(badge), new Pen(Brushes.White, 2), center, 6, 6);
            }
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color? BadgeColor(StatusKind kind) => kind switch
    {
        StatusKind.Busy => Color.FromRgb(0x0E, 0xA5, 0xE9),
        StatusKind.Attention => Color.FromRgb(0xF5, 0x9E, 0x0B),
        StatusKind.Error => Color.FromRgb(0xDC, 0x26, 0x26),
        _ => null
    };

    private static Geometry CreateCloud()
    {
        // Three bumps over a flat base; bottom edge at y = 26.5.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(new EllipseGeometry(new Point(9.5, 20.5), 6, 6));
        group.Children.Add(new EllipseGeometry(new Point(17, 15), 8, 8));
        group.Children.Add(new EllipseGeometry(new Point(24.5, 20.5), 6, 6));
        group.Children.Add(new RectangleGeometry(new Rect(9.5, 16.5, 15, 10)));
        var outline = group.GetOutlinedPathGeometry();
        outline.Freeze();
        return outline;
    }
}
