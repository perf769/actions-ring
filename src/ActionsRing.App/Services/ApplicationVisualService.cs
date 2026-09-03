using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ActionsRing.App.Services;

public sealed class ApplicationVisualService
{
    private const int MaximumDownloadedIconBytes = 512 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _iconCacheGate = new(4, 4);

    public ApplicationVisualService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionsRing",
            "icons");
    }

    public ImageSource? TryLoadIcon(string? iconReference)
    {
        if (string.IsNullOrWhiteSpace(iconReference))
        {
            return null;
        }

        try
        {
            if (iconReference.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                return TryLoadAppsFolderIcon(iconReference);
            }

            if (!File.Exists(iconReference))
            {
                return null;
            }

            if (iconReference.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || iconReference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(iconReference);
                if (icon is null)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(64, 64));
                source.Freeze();
                return source;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.UriSource = new Uri(Path.GetFullPath(iconReference), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or FormatException
                                          or ArgumentException
                                          or System.Security.SecurityException
                                          or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Error("Could not load an application icon", exception);
            return null;
        }
    }

    public async Task<string?> CacheApplicationIconAsync(
        InstalledApplicationInfo application,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        var sourceReferences = GetApplicationIconReferences(application);

        var destination = GetCachePath("apps", application.AppUserModelId ?? application.LaunchTarget);
        if (IsUsableCachedIcon(destination))
        {
            return destination;
        }
        DeleteInvalidCacheEntry(destination);

        await _iconCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (IsUsableCachedIcon(destination))
            {
                return destination;
            }
            DeleteInvalidCacheEntry(destination);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = sourceReferences
                    .Select(TryLoadIcon)
                    .OfType<BitmapSource>()
                    .FirstOrDefault();
                if (bitmap is null)
                {
                    return null;
                }

                WriteBitmapAtomically(bitmap, destination);
                return destination;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or System.Security.SecurityException
                                          or ExternalException)
        {
            AppLog.Error("Could not cache an application icon", exception);
            return null;
        }
        finally
        {
            _iconCacheGate.Release();
        }
    }

    private static IReadOnlyList<string> GetApplicationIconReferences(
        InstalledApplicationInfo application)
    {
        var references = new List<string>(3);
        Add(application.IconPath);
        Add(application.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(application.AppUserModelId))
        {
            Add($@"shell:AppsFolder\{application.AppUserModelId}");
        }

        return references;

        void Add(string? reference)
        {
            if (!string.IsNullOrWhiteSpace(reference)
                && !references.Contains(reference, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(reference);
            }
        }
    }

    public async Task<string?> GetFaviconAsync(
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var origin = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
        var destination = GetCachePath("sites", origin.AbsoluteUri);
        if (IsUsableCachedIcon(destination))
        {
            return destination;
        }
        DeleteInvalidCacheEntry(destination);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/favicon.ico"));
            request.Headers.UserAgent.ParseAdd("ActionsRing/2.0");
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > MaximumDownloadedIconBytes)
            {
                return null;
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                if (memory.Length + read > MaximumDownloadedIconBytes)
                {
                    return null;
                }
                await memory.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }

            if (memory.Length == 0)
            {
                return null;
            }

            memory.Position = 0;
            var frame = new BitmapImage();
            frame.BeginInit();
            frame.CacheOption = BitmapCacheOption.OnLoad;
            frame.DecodePixelWidth = 96;
            frame.StreamSource = memory;
            frame.EndInit();
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                return null;
            }
            frame.Freeze();
            await Task.Run(() => WriteBitmapAtomically(frame, destination), cancellationToken);
            return destination;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or FormatException
                                          or ArgumentException
                                          or System.Security.SecurityException
                                          or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null;
        }
    }

    private string GetCachePath(string category, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, category, hash + ".png");
    }

    private bool IsUsableCachedIcon(string path) =>
        File.Exists(path) && TryLoadIcon(path) is not null;

    private static void DeleteInvalidCacheEntry(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Could not replace an invalid cached icon", exception);
        }
    }

    private static void WriteBitmapAtomically(BitmapSource bitmap, string destination)
    {
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("Icon cache path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static ImageSource? TryLoadAppsFolderIcon(string parsingName)
    {
        IShellItemImageFactory? factory = null;
        nint bitmapHandle = nint.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(
                parsingName,
                nint.Zero,
                ref interfaceId,
                out factory);
            if (result < 0 || factory is null)
            {
                return null;
            }

            result = factory.GetImage(
                new NativeSize(96, 96),
                ShellItemImageFlags.BiggerSizeOk | ShellItemImageFlags.IconOnly,
                out bitmapHandle);
            if (result < 0 || bitmapHandle == nint.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(96, 96));
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            AppLog.Error("Could not load an AppsFolder icon", exception);
            return null;
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
            {
                _ = DeleteObject(bitmapHandle);
            }
            if (factory is not null && Marshal.IsComObject(factory))
            {
                _ = Marshal.FinalReleaseComObject(factory);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [Flags]
    private enum ShellItemImageFlags
    {
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out nint bitmapHandle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? shellItem);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);
}
