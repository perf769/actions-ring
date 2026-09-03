using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ActionsRing.App.Controls;
using ActionsRing.Core.Domain;
using ActionsRing.Core.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingMenuControlVisualTests
{
    [STATestMethod]
    public void LargeRingTooltipUsesItsMeasuredTextAndStaysInsideWorkArea()
    {
        const string label = "Вставить без форматирования";
        var ring = CreateRing(label);
        var control = new RingMenuControl
        {
            AnimationsEnabled = false,
            InteractionMode = RingInteractionMode.Configure,
            RingScale = 2,
            TooltipBounds = new Rect(16, 12, 768, 536),
        };

        control.Present(ring, animate: false);
        Layout(control, 800, 600);

        var text = Descendants<TextBlock>(control).Single(item => item.Text == label);
        var tooltip = Require<Border>(VisualTreeHelper.GetParent(text));
        var container = Require<Canvas>(VisualTreeHelper.GetParent(tooltip));
        var tooltipBounds = new Rect(
            Canvas.GetLeft(container) + Canvas.GetLeft(tooltip),
            Canvas.GetTop(container) + Canvas.GetTop(tooltip),
            tooltip.ActualWidth,
            tooltip.ActualHeight);
        var bubble = Descendants<Border>(container)
            .Single(item => item.Child is TextBlock glyph && !ReferenceEquals(glyph, text));
        var bubbleBounds = new Rect(
            Canvas.GetLeft(container) + Canvas.GetLeft(bubble),
            Canvas.GetTop(container) + Canvas.GetTop(bubble),
            bubble.ActualWidth,
            bubble.ActualHeight);

        Assert.AreEqual(TextTrimming.None, text.TextTrimming);
        Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
        Assert.IsTrue(tooltipBounds.Left >= 32 - 0.01);
        Assert.IsTrue(tooltipBounds.Top >= 28 - 0.01);
        Assert.IsTrue(tooltipBounds.Right <= 768 + 0.01);
        Assert.IsTrue(tooltipBounds.Bottom <= 532 + 0.01);
        Assert.IsFalse(tooltipBounds.IntersectsWith(bubbleBounds));
    }

    [STATestMethod]
    public void AdjustmentBubbleUsesASeparateUnclippedShadowSurface()
    {
        var ring = CreateRing("Громкость");
        ring.Slots[1].Action = new ActionDefinition
        {
            Name = "Громкость",
            Kind = ActionKind.AdjustParameter,
            AdjustParameter = new AdjustParameterAction
            {
                Parameter = AdjustableParameter.SystemVolume,
                Value = 1,
            },
        };
        var shadow = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.2 };
        var control = new RingMenuControl { AnimationsEnabled = false };
        control.Resources["SoftShadow"] = shadow;

        control.Present(ring, animate: false);
        Layout(control, 800, 600);

        var clippedBubble = Descendants<Border>(control).Single(item => item.ClipToBounds);
        var container = Require<Canvas>(VisualTreeHelper.GetParent(clippedBubble));
        var shadowSurface = Descendants<Border>(container)
            .Single(item => item.Child is null
                            && !ReferenceEquals(item, clippedBubble)
                            && item.Effect is DropShadowEffect);

        Assert.IsNull(clippedBubble.Effect);
        Assert.IsFalse(shadowSurface.ClipToBounds);
        Assert.AreEqual(clippedBubble.ActualWidth, shadowSurface.ActualWidth, 0.001);
    }

    [STATestMethod]
    public void CustomSlotPaletteControlsRestingAndHoverColors()
    {
        var ring = CreateRing("Копировать");
        ring.Appearance = new RingAppearanceDefinition
        {
            BubbleColor = "#FFEEEEEE",
            BubbleHoverColor = "#FF222222",
            IconColor = "#FF111111",
            IconHoverColor = "#FFFFFFFF",
        };
        ring.Slots[0].AppearanceOverride = new RingSlotAppearanceDefinition
        {
            BubbleColor = "#FF102030",
            BubbleHoverColor = "#FF405060",
            IconColor = "#FF708090",
            IconHoverColor = "#FFA0B0C0",
        };
        var control = new RingMenuControl { AnimationsEnabled = false };
        control.ApplyStyle(new RingStyleDefinition { Preset = RingStylePreset.Custom });

        control.Present(ring, animate: false);
        Layout(control, 600, 500);

        var firstNode = FirstRootNode(control);
        var nodeType = firstNode.GetType();
        var bubble = Require<Border>(nodeType.GetProperty("Bubble")!.GetValue(firstNode));
        var glyph = Require<TextBlock>(nodeType.GetProperty("Glyph")!.GetValue(firstNode));

        Assert.AreEqual(Color.FromRgb(0x10, 0x20, 0x30), ((SolidColorBrush)bubble.Background).Color);
        Assert.AreEqual(Color.FromRgb(0x70, 0x80, 0x90), ((SolidColorBrush)glyph.Foreground).Color);

        typeof(RingMenuControl)
            .GetMethod("SetNodeHover", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [firstNode, true, false]);

        Assert.AreEqual(Color.FromRgb(0x40, 0x50, 0x60), ((SolidColorBrush)bubble.Background).Color);
        Assert.AreEqual(Color.FromRgb(0xA0, 0xB0, 0xC0), ((SolidColorBrush)glyph.Foreground).Color);
    }

    [STATestMethod]
    public void ReducedMotionAnimatesOpacityWithoutScaleOrTranslation()
    {
        var control = new RingMenuControl
        {
            AnimationsEnabled = true,
            ReduceMotion = true,
        };

        control.Present(CreateRing("Копировать"), animate: true);
        Layout(control, 600, 500);

        var node = FirstRootNode(control);
        var container = Require<Canvas>(node.GetType().GetProperty("Container")!.GetValue(node));
        var transform = Require<ScaleTransform>(container.RenderTransform);
        var currentLeft = Canvas.GetLeft(container);
        var baseLeft = (double)container.GetAnimationBaseValue(Canvas.LeftProperty);
        var currentTop = Canvas.GetTop(container);
        var baseTop = (double)container.GetAnimationBaseValue(Canvas.TopProperty);

        Assert.IsTrue(container.HasAnimatedProperties, "Reduced motion should retain a short fade.");
        Assert.IsFalse(transform.HasAnimatedProperties, "Reduced motion must not animate scale.");
        Assert.AreEqual(1, transform.ScaleX, 0.001);
        Assert.AreEqual(1, transform.ScaleY, 0.001);
        Assert.AreEqual(baseLeft, currentLeft, 0.001, "Reduced motion must not translate nodes.");
        Assert.AreEqual(baseTop, currentTop, 0.001, "Reduced motion must not translate nodes.");
    }

    [STATestMethod]
    public void LocalRasterIconIsLoadedIntoMemoryAndReplacesGlyph()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"actions-ring-icon-{Guid.NewGuid():N}.png");
        try
        {
            WriteTestPng(path);
            var ring = CreateRing("Приложение");
            ring.Slots[0].Icon = new Uri(path).AbsoluteUri;
            var control = new RingMenuControl { AnimationsEnabled = false };

            control.Present(ring, animate: false);
            Layout(control, 600, 500);

            var image = Descendants<Image>(control).Single();
            Assert.IsInstanceOfType<BitmapSource>(image.Source);
            System.IO.File.Delete(path);
            Assert.IsFalse(System.IO.File.Exists(path), "The decoded icon must not keep its source file locked.");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [STATestMethod]
    public void MissingCachedLaunchIconFallsBackToExecutableIcon()
    {
        var executable = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        Assert.IsTrue(System.IO.File.Exists(executable));

        var ring = CreateRing("Проводник");
        ring.Slots[0].Icon = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"missing-actions-ring-icon-{Guid.NewGuid():N}.png");
        ring.Slots[0].Action = new ActionDefinition
        {
            Name = "Проводник",
            Kind = ActionKind.LaunchApplication,
            LaunchApplication = new LaunchApplicationAction { ExecutablePath = executable },
        };
        var control = new RingMenuControl { AnimationsEnabled = false };

        control.Present(ring, animate: false);
        Layout(control, 600, 500);

        var node = FirstRootNode(control);
        var icon = node.GetType().GetProperty("IconVisual")!.GetValue(node);
        Assert.IsInstanceOfType<Image>(icon);
        Assert.IsNotNull(((Image)icon!).Source);
    }

    private static RingDefinition CreateRing(string secondLabel)
    {
        var ring = new RingDefinition { SlotCount = 8 };
        for (var index = 0; index < 8; index++)
        {
            var label = index == 1 ? secondLabel : $"Действие {index + 1}";
            ring.Slots.Add(RingSlotDefinition.ForAction(
                ActionDefinition.BuiltInCommand(label, BuiltInCommand.Copy, "copy")));
        }
        return ring;
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static object FirstRootNode(RingMenuControl control)
    {
        var rootNodes = Require<IEnumerable>(typeof(RingMenuControl)
            .GetField("_rootNodes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(control));
        return rootNodes.Cast<object>().First();
    }

    private static T Require<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void WriteTestPng(string path)
    {
        var pixels = new byte[]
        {
            0x20, 0x50, 0xF0, 0xFF,
            0x20, 0x50, 0xF0, 0xFF,
            0x20, 0x50, 0xF0, 0xFF,
            0x20, 0x50, 0xF0, 0xFF,
        };
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write);
        encoder.Save(stream);
    }
}
