using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingSlotDragTests
{
    [STATestMethod]
    public void RootDropRequestsMoveAndSelectionFollowsMovedObjectAfterRendering()
    {
        var root = RingSlotSwapTests.CreateRing();
        var source = root.Slots[2];
        var target = root.Slots[3];
        var control = CreateControl(root);
        var requests = 0;
        control.SlotDropRequested += (_, args) =>
        {
            requests++;
            var move = (RingSlotDrag)args.Payload;
            Assert.IsTrue(RingSlotEditing.TrySwap(root, move.Source, args.Target));
            control.Present(root, animate: false);
            control.SetSelectedSlot(move.Source);
        };
        var data = control.CreateSlotDragData(source)!;

        Assert.AreEqual(DragDropEffects.Move, control.GetSlotDropEffect(data, target));
        Assert.IsTrue(control.RequestSlotDrop(data, target));
        Render(control);

        Assert.AreEqual(1, requests);
        Assert.AreSame(source, root.Slots[3]);
        Assert.AreSame(source, control.SelectedSlot);
        Assert.AreEqual(2d, Bubble(control, source).BorderThickness.Left);
    }

    [STATestMethod]
    public void VisibleSubmenuDropPreservesOpenFolderAndMovedChildSelection()
    {
        var root = RingSlotSwapTests.CreateRing();
        var folder = root.Slots[0];
        var source = folder.Submenu!.Slots[1];
        var target = folder.Submenu.Slots[0];
        var control = CreateControl(root);
        control.SetSelectedSlot(folder);
        control.SlotDropRequested += (_, args) =>
        {
            var move = (RingSlotDrag)args.Payload;
            Assert.IsTrue(RingSlotEditing.TrySwap(root, move.Source, args.Target));
            control.Present(root, animate: false);
            control.SetSelectedSlot(move.Source);
        };

        Assert.IsTrue(control.RequestSlotDrop(control.CreateSlotDragData(source)!, target));
        Render(control);

        Assert.AreSame(folder, control.OpenFolder);
        Assert.AreSame(source, control.SelectedSlot);
        Assert.AreSame(source, folder.Submenu.Slots[0]);
        Assert.AreEqual(2d, Bubble(control, source).BorderThickness.Left);
    }

    [STATestMethod]
    public void InvalidDropsAndExecuteModeNeverRequestMutation()
    {
        var root = RingSlotSwapTests.CreateRing();
        var control = CreateControl(root);
        var source = root.Slots[0];
        var data = control.CreateSlotDragData(source)!;
        var requests = 0;
        control.SlotDropRequested += (_, _) => requests++;

        Assert.IsFalse(control.RequestSlotDrop(data, source));
        Assert.IsFalse(control.RequestSlotDrop(data, source.Submenu!.Slots[0]));
        Assert.IsFalse(control.RequestSlotDrop(new DataObject(DataFormats.Text, "external"), root.Slots[1]));
        control.Present(RingSlotSwapTests.CreateRing(), animate: false);
        Assert.IsFalse(control.RequestSlotDrop(data, source));
        control.Present(root, animate: false);
        control.InteractionMode = RingInteractionMode.Execute;
        Assert.IsNull(control.CreateSlotDragData(source));
        Assert.IsFalse(control.RequestSlotDrop(data, root.Slots[1]));
        Assert.AreEqual(0, requests);
    }

    [STATestMethod]
    public void EscapeAndOutsideCancelClearHighlightWithoutMutation()
    {
        var root = RingSlotSwapTests.CreateRing();
        var control = CreateControl(root);
        var source = root.Slots[1];
        var target = root.Slots[2];
        var data = control.CreateSlotDragData(source)!;
        var requests = 0;
        control.SlotDropRequested += (_, _) => requests++;

        Assert.AreEqual(DragDropEffects.Move, control.PreviewSlotDrop(data, target, DragDropEffects.Move));
        Assert.AreEqual(2d, Bubble(control, target).BorderThickness.Left);
        var escape = (QueryContinueDragEventArgs)Activator.CreateInstance(typeof(QueryContinueDragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [true, DragDropKeyStates.LeftMouseButton], null)!;
        escape.RoutedEvent = DragDrop.QueryContinueDragEvent;
        control.RaiseEvent(escape);
        Assert.AreEqual(DragAction.Cancel, escape.Action);
        Assert.AreEqual(0d, Bubble(control, target).BorderThickness.Left);
        control.PreviewSlotDrop(data, target, DragDropEffects.Move);
        control.ClearSlotDragFeedback();
        Assert.AreEqual(0d, Bubble(control, target).BorderThickness.Left);
        Assert.AreSame(source, root.Slots[1]);
        Assert.AreSame(target, root.Slots[2]);
        Assert.AreEqual(0, requests);
        // The native drag loop suppresses its trailing release; a new press must
        // reset that suppression and retain ordinary click selection.
        typeof(RingMenuControl).GetField("_suppressDragClick", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, true);
        var sourceBubble = Bubble(control, source);
        sourceBubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        sourceBubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.AreSame(source, control.SelectedSlot);
    }

    [STATestMethod]
    public void LibraryCopyDropRetainsOriginalPayloadAndCopyEffect()
    {
        var root = RingSlotSwapTests.CreateRing();
        var control = CreateControl(root);
        var catalogItem = ActionCatalog.Groups.SelectMany(group => group.Items).First();
        var data = new DataObject(ActionCatalog.DragFormat, catalogItem);
        object? received = null;
        control.SlotDropRequested += (_, args) => received = args.Payload;

        Assert.AreEqual(DragDropEffects.Copy, control.GetSlotDropEffect(data, root.Slots[1]));
        Assert.IsTrue(control.RequestSlotDrop(data, root.Slots[1]));
        Assert.AreSame(catalogItem, received);
        Assert.AreEqual(DragDropEffects.None, control.PreviewSlotDrop(data, root.Slots[1], DragDropEffects.Move));
    }

    [STATestMethod]
    public void SmallMotionPreservesClickAndNormalSelectionToggle()
    {
        var root = RingSlotSwapTests.CreateRing();
        var control = CreateControl(root);
        var source = root.Slots[2];
        var bubble = Bubble(control, source);
        Assert.IsFalse(RingSlotDrag.ExceedsThreshold(new Point(10, 10), new Point(11, 11)));
        Assert.IsTrue(RingSlotDrag.ExceedsThreshold(new Point(), new Point(SystemParameters.MinimumHorizontalDragDistance, 0)));
        control.BeginSlotDragCandidate(source, new Point());
        bubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.AreSame(source, control.SelectedSlot);
        bubble.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.IsNull(control.SelectedSlot);
    }

    private static RingMenuControl CreateControl(RingDefinition root)
    {
        var control = new RingMenuControl { InteractionMode = RingInteractionMode.Configure, AnimationsEnabled = false };
        control.Present(root, animate: false);
        Render(control);
        return control;
    }

    private static void Render(RingMenuControl control)
    {
        control.Measure(new Size(900, 700));
        control.Arrange(new Rect(0, 0, 900, 700));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
    }

    private static Border Bubble(RingMenuControl control, RingSlotDefinition slot)
    {
        var fields = new[] { "_rootNodes", "_submenuNodes" };
        var node = fields.SelectMany(field => ((IEnumerable)typeof(RingMenuControl).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!).Cast<object>())
            .Single(item => ReferenceEquals(item.GetType().GetProperty("Slot")!.GetValue(item), slot));
        return (Border)node.GetType().GetProperty("Bubble")!.GetValue(node)!;
    }
}
