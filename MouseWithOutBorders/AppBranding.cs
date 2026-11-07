using System.Reflection;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class AppBranding
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    internal static Icon LoadIcon(Size size)
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MouseWithOutBorders.AppIcon.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using Icon source = new(stream, size);
        return (Icon)source.Clone();
    }

    internal static Icon ToGrayscaleIcon(Icon source)
    {
        using Bitmap bitmap = source.ToBitmap();
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                int gray = (299 * pixel.R + 587 * pixel.G + 114 * pixel.B + 500) / 1000;
                bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, gray, gray, gray));
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }
}
