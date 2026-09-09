using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net;
using System.Net.Http;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class IconLibraryTests
{
    [TestMethod]
    public void AllBundledIconsParseAndRender()
    {
        Assert.IsTrue(IconLibrary.All.Count > 5000);
        var invalid = new List<string>();
        foreach (var icon in IconLibrary.All)
        {
            try
            {
                var rendered = IconLibrary.FindSvg(icon.Reference)!.CreateImage(Brushes.Black, true);
                if (rendered.Width <= 0 || rendered.Height <= 0) invalid.Add(icon.Reference + " empty");
                var repeated = IconLibrary.FindSvg(icon.Reference)!.CreateImage(Brushes.White, true);
                if (repeated.Width <= 0 || repeated.Height <= 0) invalid.Add(icon.Reference + " repeated empty");
            }
            catch (Exception exception) { invalid.Add(icon.Reference + ": " + exception.Message); }
        }
        Assert.AreEqual(0, invalid.Count, string.Join("\n", invalid.Take(20)));
    }

    [TestMethod]
    public void SearchAcceptsRussianAndEnglishSemanticNames()
    {
        Assert.IsTrue(IconLibrary.Search("кисть").Any(icon => icon.Reference == "lucide:brush"));
        Assert.IsTrue(IconLibrary.Search("pipette").Any(icon => icon.Reference == "lucide:pipette"));
        Assert.IsTrue(IconLibrary.Search("чатгпт").Any(icon => icon.Reference == "brand:openai"));
        Assert.IsTrue(IconLibrary.Search("", "Бренды").All(icon => icon.IsBrand));
    }

    [TestMethod]
    public void ExistingWebsitePlaceholdersGainKnownBrandWithoutEditingConfiguration()
    {
        var action = new ActionDefinition { Kind = ActionKind.OpenUri, Icon = "browser", OpenUri = new OpenUriAction { Uri = "https://chatgpt.com/c/example" } };
        Assert.AreEqual("brand:openai", IconLibrary.ResolveReference("browser", action));
        Assert.AreEqual("brand:openai", IconLibrary.ResolveReference(null, action));
        Assert.AreEqual("lucide:star", IconLibrary.ResolveReference("lucide:star", action));
        Assert.AreEqual("lucide:star", IconLibrary.ResolveReference("star", action));
        Assert.AreEqual("image:" + new string('A', 64) + ".png", IconLibrary.ResolveReference("image:" + new string('A', 64) + ".png", action));
        Assert.IsNull(IconLibrary.KnownWebsiteBrand("chatgpt.com.evil.example"));
        Assert.AreEqual("lucide:palette", IconLibrary.ResolveReference("palette", ActionDefinition.BuiltInCommand("Copy", BuiltInCommand.Copy)));
        Assert.AreEqual("lucide:skip-forward", IconLibrary.ResolveReference("media", ActionDefinition.BuiltInCommand("Next", BuiltInCommand.MediaNext)));
    }

    [STATestMethod]
    public void VectorIconTracksForegroundChangesForHover()
    {
        var view = new ActionIconView { Width = 32, Height = 32, Foreground = Brushes.Black, Background = Brushes.White };
        view.SetIcon("brand:openai");
        var image = (Image)((Border)view.Content).Child;
        var before = image.Source;
        view.Foreground = Brushes.White;
        view.Background = Brushes.Black;
        Assert.AreNotSame(before, image.Source);
        Assert.IsInstanceOfType<DrawingImage>(image.Source);
        Assert.IsNull(((Border)view.Content).Background);
    }

    [STATestMethod]
    public void AutomaticUnknownApplicationLoadsExecutableIconInsteadOfKeepingPlaceholder()
    {
        var view = new ActionIconView();
        view.SetIcon(null, new ActionDefinition { Kind = ActionKind.LaunchApplication, LaunchApplication = new LaunchApplicationAction { ExecutablePath = Environment.ProcessPath! } });
        PumpUntil(() => ((Image)((Border)view.Content).Child).Source is BitmapSource);
    }

    [STATestMethod]
    public void AutomaticUnknownWebsiteResolvesRelFaviconInsteadOfKeepingPlaceholder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ActionsRingIconsTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new IconHandler());
            var view = new ActionIconView(new ApplicationVisualService(directory, http));
            view.SetIcon("browser", new ActionDefinition { Kind = ActionKind.OpenUri, OpenUri = new OpenUriAction { Uri = "https://example.com" } });
            PumpUntil(() => ((Image)((Border)view.Content).Child).Source is BitmapSource);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.IsTrue(condition(), "The automatic image was not loaded.");
    }

    [STATestMethod]
    public void WhiteBitmapGetsAContrastPlateOnlyOnLightBackground()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ActionsRingIconsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "white.png");
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var view = new ActionIconView { Foreground = Brushes.Black, Background = Brushes.White };
            view.SetIcon(path);
            var surface = (Border)view.Content;
            PumpUntil(() => ((Image)surface.Child).Source is BitmapSource);
            Assert.IsNotNull(surface.Background);
            view.Background = Brushes.Black;
            Assert.IsNull(surface.Background);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class IconHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath == "/"
                    ? new StringContent("<html><head><link rel='icon' href='/brand.png'></head></html>")
                    : new ByteArrayContent(TinyPng()),
            };
            return Task.FromResult(response);
        }
        private static byte[] TinyPng()
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 50, 100, 150, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
        }
    }

    [TestMethod]
    public void PassiveSvgRejectsActiveAndExternalContent()
    {
        foreach (var svg in new[]
        {
            "<svg><script>alert(1)</script></svg>",
            "<svg><image href='https://example.com/icon.png'/></svg>",
            "<svg onload='alert(1)'/>",
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>",
            "<svg><path fill='url(https://example.com/a)' d='M0 0L1 1'/></svg>",
            "<svg><defs><linearGradient id='g'><stop offset='0' stop-color='url(#g)'/></linearGradient></defs><path fill='url(#g)' d='M0 0H24V24H0Z'/></svg>",
            "<svg><defs><linearGradient id='g'><stop offset='0' stop-color='url(#b)'/></linearGradient><linearGradient id='b'><stop offset='0' stop-color='url(#g)'/></linearGradient></defs><path fill='url(#g)' d='M0 0H24V24H0Z'/></svg>",
        })
        {
            try { SafeSvgIcon.Parse(svg); Assert.Fail("Unsafe SVG was accepted."); }
            catch (Exception exception) when (exception is FormatException or System.Xml.XmlException) { }
        }
    }

    [TestMethod]
    public void SvgRenderingDoesNotFreezeTheHostsLivePaletteBrush()
    {
        var brush = new SolidColorBrush(Colors.Black);
        _ = IconLibrary.FindSvg("lucide:brush")!.CreateImage(brush, true);
        Assert.IsFalse(brush.IsFrozen);
        brush.Color = Colors.White;
    }

    [TestMethod]
    public void ImportCopiesAndSanitizesSvgAndRefCannotEscapeCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ActionsRingIconsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "original.svg");
            File.WriteAllText(input, "<svg viewBox='0 0 24 24'><path fill='currentColor' d='M0 0L24 24L0 24Z'/></svg>");
            var service = new IconImportService(Path.Combine(directory, "cache"));
            var reference = service.Import(input);
            File.Delete(input);
            Assert.IsTrue(reference.StartsWith("image:", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(service.ResolvePath(reference)));
            Assert.IsNull(service.ResolvePath("image:../original.svg"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
