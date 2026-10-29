using System.Drawing.Imaging;
using SkiaSharp;

namespace ScreenCutter;

public static class ImageSaver
{
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Jpg => ".jpg",
        OutputFormat.Webp => ".webp",
        _ => ".png",
    };

    public static void Save(Bitmap bitmap, string path, OutputFormat format, int quality)
    {
        var skFormat = format switch
        {
            OutputFormat.Jpg => SKEncodedImageFormat.Jpeg,
            OutputFormat.Webp => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // スクリーンショットは常に不透明なので、アルファは捨てて Opaque として扱う
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using SKImage image = SKImage.FromPixelCopy(info, data.Scan0, data.Stride);
            using SKData encoded = image.Encode(skFormat, Math.Clamp(quality, 1, 100))
                ?? throw new InvalidOperationException($"{format} へのエンコードに失敗しました。");
            using FileStream stream = File.Create(path);
            encoded.SaveTo(stream);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
