using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbusRebornManager;

/// <summary>
/// Loads and stores personal instance covers without changing the game folder.
/// Custom covers are re-encoded as small PNG files inside manager-owned AppData.
/// </summary>
public static class InstanceArtwork
{
    private static readonly string IconDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbusRebornManager", "instance-icons");

    private const long MaxSourceBytes = 12L * 1024 * 1024;

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 512;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null; // A broken cover must never prevent loading an instance.
        }
    }

    // Cropping happens only when displaying the cover. We always store the
    // entire chosen source PNG so players can reframe it later.
    public static BitmapSource? LoadCropped(string? path, System.Windows.Rect normalized)
    {
        if (Load(path) is not BitmapSource bitmap)
            return null;
        try
        {
            if (!double.IsFinite(normalized.X) || !double.IsFinite(normalized.Y) ||
                !double.IsFinite(normalized.Width) || !double.IsFinite(normalized.Height))
                return bitmap;

            double x = Math.Clamp(normalized.X, 0, 1);
            double y = Math.Clamp(normalized.Y, 0, 1);
            double w = Math.Clamp(normalized.Width, 0.001, 1 - x);
            double h = Math.Clamp(normalized.Height, 0.001, 1 - y);

            int left = Math.Clamp((int)Math.Round(x * bitmap.PixelWidth),
                0, bitmap.PixelWidth - 1);
            int top = Math.Clamp((int)Math.Round(y * bitmap.PixelHeight),
                0, bitmap.PixelHeight - 1);
            int width = Math.Clamp((int)Math.Round(w * bitmap.PixelWidth),
                1, bitmap.PixelWidth - left);
            int height = Math.Clamp((int)Math.Round(h * bitmap.PixelHeight),
                1, bitmap.PixelHeight - top);

            var cropped = new CroppedBitmap(bitmap,
                new System.Windows.Int32Rect(left, top, width, height));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return bitmap;
        }
    }

    public static System.Windows.Rect SavedCrop(GameInstance instance) =>
        new(instance.IconCropX, instance.IconCropY,
            instance.IconCropWidth, instance.IconCropHeight);

    public static ImageSource? Cover(GameInstance instance) =>
        instance.IconHasCrop
            ? LoadCropped(instance.CustomIconPath, SavedCrop(instance))
            : Load(instance.CustomIconPath);

    public static string Save(string sourcePath, string gamePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected icon image no longer exists.", sourcePath);
        if (new FileInfo(sourcePath).Length > MaxSourceBytes)
            throw new InvalidOperationException("Choose an image smaller than 12 MB.");

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            throw new InvalidOperationException("Choose a PNG, JPG or BMP image.");

        // A unique filename avoids stale WPF image caching after an edit.
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(gamePath).ToUpperInvariant())))[..20]
            .ToLowerInvariant();
        Directory.CreateDirectory(IconDirectory);
        string final = Path.Combine(IconDirectory,
            key + "-" + Guid.NewGuid().ToString("N") + ".png");
        string temporary = final + ".tmp";

        try
        {
            var bitmap = Load(sourcePath) as BitmapSource
                ?? throw new InvalidOperationException("The selected image could not be decoded.");

            using (var output = File.Create(temporary))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
            File.Move(temporary, final);
            return final;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
