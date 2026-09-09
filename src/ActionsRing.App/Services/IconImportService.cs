using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace ActionsRing.App.Services;

public sealed class IconImportService
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private readonly string _directory;
    public IconImportService(string? cacheDirectory = null) => _directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActionsRing", "icons", "custom");
    public string Import(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaximumFileBytes) throw new FormatException("Выберите иконку размером до 8 МБ.");
        var extension = file.Extension.ToLowerInvariant();
        byte[] bytes;
        if (extension == ".svg")
        {
            var svg = SafeSvgIcon.Parse(File.ReadAllText(path));
            _ = svg.CreateImage(System.Windows.Media.Brushes.Black);
            bytes = Encoding.UTF8.GetBytes(svg.ToSanitizedString());
        }
        else if (extension is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
        {
            using var input = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 256; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.PixelHeight > 4096) throw new FormatException("Неподходящие размеры иконки.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream(); encoder.Save(memory); bytes = memory.ToArray(); extension = ".png";
        }
        else throw new FormatException("Выберите SVG, PNG, ICO или JPG.");
        var name = Convert.ToHexString(SHA256.HashData(bytes)) + extension;
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, name);
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, destination, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return "image:" + name;
    }

    public string? ResolvePath(string reference)
    {
        if (!reference.StartsWith("image:", StringComparison.OrdinalIgnoreCase)) return null;
        var name = reference[6..];
        if (!Regex.IsMatch(name, @"^[a-fA-F0-9]{64}\.(png|svg)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return null;
        return Path.Combine(_directory, name);
    }
}
