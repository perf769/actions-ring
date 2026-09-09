using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<string?> Task)> _faviconRequests = new();

    public ApplicationVisualService(string? cacheDirectory = null, HttpClient? httpClient = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionsRing",
            "icons");
        _httpClient = httpClient ?? HttpClient;
    }

    public ImageSource? TryLoadIcon(string? iconReference)
    {
        if (string.IsNullOrWhiteSpace(iconReference))
        {
            return null;
        }

        try
        {
            if (IconLibrary.FindSvg(iconReference) is { } vector) return vector.CreateImage(Brushes.Black, monochrome: true);
            if (Uri.TryCreate(iconReference, UriKind.Absolute, out var fileUri) && fileUri.IsFile) iconReference = fileUri.LocalPath;
            if (iconReference.StartsWith("image:", StringComparison.OrdinalIgnoreCase)) iconReference = new IconImportService().ResolvePath(iconReference);
            if (iconReference is null) return null;
            if (iconReference.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                return TryLoadAppsFolderIcon(iconReference);
            }

            if (!File.Exists(iconReference))
            {
                return null;
            }
            if (iconReference.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return SafeSvgIcon.Parse(File.ReadAllText(iconReference)).CreateImage(Brushes.Black);

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
                                          or System.Xml.XmlException
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
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsPublicHttpUri(uri)) return null;
        if (IconLibrary.KnownWebsiteBrand(uri.Host) is { } brand) return brand;
        var origin = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
        var key = origin.AbsoluteUri;
        if (_faviconRequests.TryGetValue(key, out var cachedRequest) && cachedRequest.Expires > DateTime.UtcNow)
            return await cachedRequest.Task.WaitAsync(cancellationToken);
        var task = ResolveFaviconAsync(origin);
        _faviconRequests[key] = (DateTime.UtcNow.AddMinutes(5), task);
        return await task.WaitAsync(cancellationToken);
    }

    private async Task<string?> ResolveFaviconAsync(Uri origin)
    {
        var destination = GetCachePath("sites", origin.AbsoluteUri);
        if (IsUsableCachedIcon(destination)) return destination;
        var svgDestination = Path.ChangeExtension(destination, ".svg");
        if (IsUsableCachedIcon(svgDestination)) return svgDestination;
        DeleteInvalidCacheEntry(destination);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var candidates = new List<Uri>();
            var html = await DownloadBoundedAsync(origin, 256 * 1024, timeout.Token);
            if (html is not null) candidates.AddRange(FindFaviconCandidates(Encoding.UTF8.GetString(html), origin));
            candidates.Add(new Uri(origin, "/favicon.ico"));
            candidates.Add(new Uri(origin, "/apple-touch-icon.png"));
            foreach (var candidate in candidates.Distinct().Take(7))
            {
                var bytes = await DownloadBoundedAsync(candidate, MaximumDownloadedIconBytes, timeout.Token);
                if (bytes is null) continue;
                try
                {
                    using var memory = new MemoryStream(bytes, writable: false);
                    var beginning = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 512)));
                    if (beginning.Contains("<svg", StringComparison.OrdinalIgnoreCase) || beginning.TrimStart().StartsWith("<?xml", StringComparison.Ordinal))
                    {
                        var svg = SafeSvgIcon.Parse(Encoding.UTF8.GetString(bytes));
                        _ = svg.CreateImage(Brushes.Black);
                        Directory.CreateDirectory(Path.GetDirectoryName(svgDestination)!);
                        var temporary = svgDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { await File.WriteAllTextAsync(temporary, svg.ToSanitizedString(), timeout.Token); File.Move(temporary, svgDestination, overwrite: true); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        return svgDestination;
                    }
                    var frame = new BitmapImage();
                    frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad; frame.DecodePixelWidth = 96; frame.StreamSource = memory; frame.EndInit();
                    if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelHeight > 2048) continue;
                    frame.Freeze();
                    await Task.Run(() => WriteBitmapAtomically(frame, destination), timeout.Token);
                    return destination;
                }
                catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException or System.Xml.XmlException) { }
            }
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or FormatException
                                          or ArgumentException
                                          or System.Security.SecurityException
                                          or RegexMatchTimeoutException
                                          or OperationCanceledException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<Uri> FindFaviconCandidates(string html, Uri origin)
    {
        var candidates = new List<(Uri Uri, int Priority)>();
        foreach (Match match in Regex.Matches(html, @"<link\b[^>]{0,4096}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Regex.Matches(match.Value, "([a-zA-Z-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                attributes[attribute.Groups[1].Value] = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
            if (!attributes.TryGetValue("rel", out var rel) || !attributes.TryGetValue("href", out var href)) continue;
            var tokens = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var standard = tokens.Contains("icon", StringComparer.OrdinalIgnoreCase);
            var touch = tokens.Any(token => token.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase));
            if ((!standard && !touch) || !Uri.TryCreate(origin, href, out var uri) || !IsPublicHttpUri(uri)) continue;
            candidates.Add((uri, standard ? 0 : 1));
        }
        return candidates.OrderBy(candidate => candidate.Priority).Select(candidate => candidate.Uri).Distinct().Take(5).ToArray();
    }

    private async Task<byte[]?> DownloadBoundedAsync(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        try
        {
            for (var redirects = 0; redirects <= 3; redirects++)
            {
                if (!IsPublicHttpUri(uri)) return null;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("ActionsRing/2.2");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
                { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximum) return null;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var memory = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                { if (memory.Length + read > maximum) return null; await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
                return memory.Length == 0 ? null : memory.ToArray();
            }
        }
        catch (HttpRequestException) { }
        return null;
    }

    internal static bool IsPublicHttpUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort && !uri.IsLoopback
        && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && (!IPAddress.TryParse(uri.Host, out var ip) || IsPublicAddress(ip));

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) : (bytes[0] & 0xfe) != 0xfc;
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
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var address = addresses.FirstOrDefault(IsPublicAddress) ?? throw new HttpRequestException("Icon host does not resolve to a public address.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
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
