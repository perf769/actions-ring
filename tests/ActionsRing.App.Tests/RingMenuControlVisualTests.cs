using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        var layer = Require<Canvas>(VisualTreeHelper.GetParent(tooltip));
        var node = RootNodes(control).ElementAt(1);
        var container = Property<Canvas>(node, "Container");
        var tooltipBounds = new Rect(
            Canvas.GetLeft(tooltip),
            Canvas.GetTop(tooltip),
            tooltip.ActualWidth,
            tooltip.ActualHeight);
        var bubble = Property<Border>(node, "Bubble");
        var bubbleBounds = new Rect(
            Canvas.GetLeft(container) + Canvas.GetLeft(bubble),
            Canvas.GetTop(container) + Canvas.GetTop(bubble),
            bubble.ActualWidth,
            bubble.ActualHeight);

        Assert.AreEqual(TextTrimming.None, text.TextTrimming);
        Assert.AreEqual("TooltipCanvas", layer.Name);
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
        var glyph = Require<ActionIconView>(nodeType.GetProperty("IconVisual")!.GetValue(firstNode));

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
    public void RegularEntranceKeepsItsScaleAndTranslationAnimations()
    {
        var control = new RingMenuControl { AnimationsEnabled = true };
        Layout(control, 600, 500);
        using var source = new HwndSource(new HwndSourceParameters("Actions Ring animation test")
        {
            Width = 600,
            Height = 500,
            PositionX = -10000,
            PositionY = -10000,
            WindowStyle = unchecked((int)0x90000000),
            ExtendedWindowStyle = 0x08000080,
        });
        source.RootVisual = control;
        control.Present(CreateRing("Копировать"), animate: true);
        var container = Property<Canvas>(FirstRootNode(control), "Container");
        var scale = Require<ScaleTransform>(container.RenderTransform);

        Assert.IsTrue(container.HasAnimatedProperties);
        Assert.IsTrue(scale.HasAnimatedProperties);
        PumpUntil(() => DependencyPropertyHelper.GetValueSource(container, Canvas.TopProperty).IsAnimated);
        Assert.IsTrue(DependencyPropertyHelper.GetValueSource(container, Canvas.TopProperty).IsAnimated);
        Assert.IsTrue(scale.ScaleX < 1);
        Assert.AreNotEqual((double)container.GetAnimationBaseValue(Canvas.TopProperty), Canvas.GetTop(container));
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

            var image = Descendants<Image>(Property<ActionIconView>(FirstRootNode(control), "IconVisual")).Single();
            PumpUntil(() => image.Source is BitmapSource);
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
        Assert.IsInstanceOfType<ActionIconView>(icon);
        var image = Descendants<Image>((ActionIconView)icon!).Single();
        PumpUntil(() => image.Source is BitmapSource);
        Assert.IsInstanceOfType<BitmapSource>(image.Source);
    }

    [STATestMethod]
    public void ConfigureSelectionPersistsOnMouseLeaveAndSecondClickClearsIt()
    {
        var ring = CreateRing("Копировать");
        var control = CreateControl(ring, RingInteractionMode.Configure);
        var node = RootNodes(control).ElementAt(4);
        var changed = 0;
        control.SelectionChanged += (_, _) => changed++;

        InvokeNode(control, "OnNodeClicked", node);
        InvokeNode(control, "OnNodeLeft", node);

        Assert.AreSame(ring.Slots[4], control.SelectedSlot);
        Assert.AreEqual(2d, Property<Border>(node, "Bubble").BorderThickness.Left);
        InvokeNode(control, "OnNodeClicked", node);
        Assert.IsNull(control.SelectedSlot);
        Assert.IsNull(control.OpenFolder);
        Assert.AreEqual(0d, Property<Border>(node, "Bubble").BorderThickness.Left);
        Assert.AreEqual(2, changed);
    }

    [STATestMethod]
    public void ConfigureSelectingOrdinarySiblingClosesFolderAndRestoresAllLabels()
    {
        var ring = CreateRingWithFolder();
        var control = CreateControl(ring, RingInteractionMode.Configure);
        InvokeNode(control, "OnNodeClicked", RootNodes(control).ElementAt(2));

        Assert.AreSame(ring.Slots[2], control.OpenFolder);
        var sibling = RootNodes(control).ElementAt(4);
        Assert.AreEqual(0.28d, Property<Canvas>(sibling, "Container").Opacity, 0.001);
        Assert.IsTrue(Property<Border>(sibling, "Bubble").IsHitTestVisible);
        Assert.AreEqual(0d, Property<Border>(sibling, "Label").Opacity);

        InvokeNode(control, "OnNodeClicked", sibling);

        Assert.AreSame(ring.Slots[4], control.SelectedSlot);
        Assert.IsNull(control.OpenFolder);
        Assert.AreEqual(0, SubmenuNodes(control).Count());
        foreach (var node in RootNodes(control))
        {
            Assert.AreEqual(1d, Property<Canvas>(node, "Container").Opacity);
            Assert.AreEqual(1d, Property<Border>(node, "Label").Opacity);
        }
    }

    [STATestMethod]
    public void ExecuteSiblingHoverClosesPreviousFolderAndRaisesTooltipAboveAllBubbles()
    {
        var ring = CreateRingWithFolder();
        var control = CreateControl(ring, RingInteractionMode.Execute);
        control.TooltipDelayMilliseconds = 0;
        var parent = RootNodes(control).ElementAt(2);
        InvokeNode(control, "OnNodeEntered", parent);
        Assert.AreEqual(1d, Property<Border>(parent, "Label").Opacity);
        control.OpenSubmenu(ring.Slots[2], animate: false);
        Assert.AreEqual(0d, Property<Border>(parent, "Label").Opacity, "The parent caption must not obscure its submenu.");
        InvokeNode(control, "OnNodeEntered", parent);
        Assert.AreEqual(0d, Property<Border>(parent, "Label").Opacity);
        var child = SubmenuNodes(control).First();
        InvokeNode(control, "OnNodeEntered", child);
        Assert.AreSame(ring.Slots[2], control.OpenFolder, "Crossing into a submenu must keep its parent open.");

        var sibling = RootNodes(control).ElementAt(1);
        InvokeNode(control, "OnNodeEntered", sibling);

        Assert.IsNull(control.OpenFolder);
        Assert.AreSame(ring.Slots[1], control.HoveredSlot);
        Assert.AreEqual(0, SubmenuNodes(control).Count());
        var label = Property<Border>(sibling, "Label");
        var tooltipLayer = Require<Canvas>(VisualTreeHelper.GetParent(label));
        var grid = Require<Grid>(VisualTreeHelper.GetParent(tooltipLayer));
        Assert.AreSame(tooltipLayer, grid.Children[grid.Children.Count - 1]);
        Assert.IsFalse(tooltipLayer.IsHitTestVisible);
        Assert.AreEqual(1d, label.Opacity);
    }

    [STATestMethod]
    public void HybridParentInvokesItsActionAndFolderOnlyParentDoesNotInvoke()
    {
        var ring = CreateRingWithFolder();
        var control = CreateControl(ring, RingInteractionMode.Execute);
        var node = RootNodes(control).ElementAt(2);
        var invoked = new List<RingSlotDefinition>();
        control.SlotInvoked += (_, args) => invoked.Add(args.Slot);
        InvokeNode(control, "OnNodeClicked", node);
        Assert.AreEqual(0, invoked.Count);
        Assert.AreSame(ring.Slots[2], control.OpenFolder);

        ring.Slots[2].Action = ActionDefinition.BuiltInCommand("Копировать", BuiltInCommand.Copy, "copy");
        InvokeNode(control, "OnNodeClicked", node);
        Assert.AreEqual(1, invoked.Count);
        Assert.AreSame(ring.Slots[2], invoked[0]);
    }

    [STATestMethod]
    public void SelectedNestedSlotRestoresItsWholeSubmenuPath()
    {
        var ring = CreateRingWithFolder();
        var parent = ring.Slots[2];
        var nested = RingSlotDefinition.ForSubmenu("Вложенное", CreateRing("Вложенное действие"));
        parent.Submenu!.Slots[0] = nested;
        var selected = nested.Submenu!.Slots[1];
        var control = CreateControl(ring, RingInteractionMode.Configure);

        control.SetSelectedSlot(selected);

        Assert.AreSame(selected, control.SelectedSlot);
        Assert.AreSame(nested, control.OpenFolder);
        Assert.IsTrue(SubmenuNodes(control).Any(node => ReferenceEquals(Property<RingSlotDefinition>(node, "Slot"), selected)));
        control.RingScale = 1.25;
        Layout(control, 800, 600);
        Assert.AreSame(selected, control.SelectedSlot);
        Assert.AreSame(nested, control.OpenFolder);
    }

    [STATestMethod]
    public void CenterCrossUsesSymmetricVectorGeometry()
    {
        var control = CreateControl(CreateRing("Копировать"), RingInteractionMode.Execute);
        var center = Require<Border>(typeof(RingMenuControl)
            .GetField("_centerVisual", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control));
        var cross = Require<System.Windows.Shapes.Path>(center.Child);
        var bounds = cross.TransformToAncestor(center).TransformBounds(new Rect(cross.RenderSize));

        Assert.AreEqual(center.ActualWidth / 2, bounds.Left + bounds.Width / 2, 0.01);
        Assert.AreEqual(center.ActualHeight / 2, bounds.Top + bounds.Height / 2, 0.01);
        Assert.AreEqual(cross.Data.Bounds.Width, cross.Data.Bounds.Height, 0.01);
    }

    [STATestMethod]
    public void SubmenuHintIsAttachedAndCompactAndDropletSeparatesContinuously()
    {
        var surface = RingSubmenuMotion.AttachedSurface(new Point(24, 24), 24, 0);
        Assert.IsTrue(surface.Bounds.Right <= 56);
        for (var x = 44d; x < 53d; x += 0.5)
        {
            Assert.IsTrue(surface.FillContains(new Point(x, 24)), "The hint must form one connected surface.");
        }
        var bridge = RingSubmenuMotion.Bridge(new Point(100, 100), 24, new Point(157, 100), 20, 0.8);
        Assert.IsFalse(bridge.IsEmpty());
        Assert.IsTrue(bridge.FillContains(new Point(130, 100)));
        Assert.IsTrue(RingSubmenuMotion.Bridge(new Point(100, 100), 24, new Point(201, 100), 24, 0).IsEmpty());
    }

    [STATestMethod]
    public void SubmenuAnimationGrowsAndSeparatesBeforeFinishingAtItsLayoutTarget()
    {
        var ring = CreateRingWithFolder();
        var control = CreateControl(ring, RingInteractionMode.Configure);
        control.AnimationsEnabled = true;
        control.OpenSubmenu(ring.Slots[2], animate: true);
        var property = (DependencyProperty)typeof(RingMenuControl)
            .GetField("SubmenuProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        control.BeginAnimation(property, null);
        var lead = SubmenuNodes(control).ElementAt(1);
        var container = Property<Canvas>(lead, "Container");
        var transform = Require<ScaleTransform>(container.RenderTransform);
        var previousScale = 0d;
        var previousLeft = double.NegativeInfinity;
        foreach (var progress in new[] { 0d, 40 / 300d, 80 / 300d, 0.5d, 1d })
        {
            typeof(RingMenuControl).GetMethod("RenderSubmenuFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(control, [progress]);
            Assert.IsTrue(transform.ScaleX >= previousScale);
            Assert.IsTrue(Canvas.GetLeft(container) >= previousLeft);
            Assert.AreEqual(transform.ScaleX, transform.ScaleY);
            previousScale = transform.ScaleX;
            previousLeft = Canvas.GetLeft(container);
        }
        var target = (Point)lead.GetType().GetProperty("Center")!.GetValue(lead)!;
        Assert.AreEqual(1d, transform.ScaleX, 0.001);
        Assert.AreEqual(target.X - container.Width / 2, Canvas.GetLeft(container), 0.001);
        typeof(RingMenuControl).GetMethod("StopSubmenuAnimation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(control, null);
    }

    private static RingMenuControl CreateControl(RingDefinition ring, RingInteractionMode mode)
    {
        var control = new RingMenuControl { AnimationsEnabled = false, InteractionMode = mode };
        control.Present(ring, animate: false);
        Layout(control, 800, 600);
        return control;
    }

    private static RingDefinition CreateRingWithFolder()
    {
        var ring = CreateRing("Копировать");
        ring.Slots[2] = RingSlotDefinition.ForSubmenu("Приложения", CreateRing("Вложенное действие"));
        ring.Slots[2].Submenu!.SlotCount = 4;
        return ring;
    }

    private static void InvokeNode(RingMenuControl control, string method, object node) => typeof(RingMenuControl)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [node]);

    private static T Property<T>(object value, string name) where T : class => Require<T>(value.GetType().GetProperty(name)!.GetValue(value));

    private static IEnumerable<object> RootNodes(RingMenuControl control) => Nodes(control, "_rootNodes");
    private static IEnumerable<object> SubmenuNodes(RingMenuControl control) => Nodes(control, "_submenuNodes");
    private static IEnumerable<object> Nodes(RingMenuControl control, string field) => Require<IEnumerable>(typeof(RingMenuControl)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)).Cast<object>();

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
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
