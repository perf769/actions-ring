using System.Reflection;
using System.Windows;
using ActionsRing.App.Controls;
using ActionsRing.App.Windows;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingOverlayHoldReleaseTests
{
    [STATestMethod]
    public void HoldReleaseOverFolderClosesRingAndSubmenuWithoutAction()
    {
        WithOverlay((overlay, ring, folder, _) =>
        {
            var invoked = 0;
            overlay.ActionRequested += (_, _) => invoked++;
            ring.OpenSubmenu(folder, animate: false);
            Hover(ring, folder);

            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();

            Assert.IsFalse(overlay.IsVisible);
            Assert.IsNull(ring.OpenFolder);
            Assert.IsNull(ring.HoveredSlot);
            Assert.AreEqual(0, invoked);
        });
    }

    [STATestMethod]
    public void ToggleCommitOverFolderStillNavigatesIntoSubmenu()
    {
        WithOverlay((overlay, ring, folder, _) =>
        {
            Hover(ring, folder);

            overlay.CommitHoveredAsync().GetAwaiter().GetResult();

            Assert.IsTrue(overlay.IsRingVisible);
            Assert.AreSame(folder, ring.OpenFolder);
        });
    }

    [STATestMethod]
    public void HoldReleaseInGapDoesNotInvokeActiveHybridFolder()
    {
        WithOverlay((overlay, ring, folder, _) =>
        {
            folder.Action = ActionDefinition.BuiltInCommand("Родитель", BuiltInCommand.Paste, "paste");
            var invoked = 0;
            overlay.ActionRequested += (_, _) => invoked++;
            ring.OpenSubmenu(folder, animate: false);
            Hover(ring, null);

            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();

            Assert.IsFalse(overlay.IsVisible);
            Assert.IsNull(ring.OpenFolder);
            Assert.AreEqual(0, invoked);
        });
    }

    [STATestMethod]
    public void HoldReleaseInvokesOnlyHoveredChildOnceAfterRingCloses()
    {
        WithOverlay((overlay, ring, folder, child) =>
        {
            folder.Action = ActionDefinition.BuiltInCommand("Родитель", BuiltInCommand.Paste, "paste");
            var invoked = new List<RingSlotDefinition>();
            overlay.ActionRequested += (_, args) =>
            {
                Assert.IsFalse(overlay.IsVisible, "Hide must complete before dispatching the action.");
                invoked.Add(args.Slot);
            };
            ring.OpenSubmenu(folder, animate: false);
            Hover(ring, child);

            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();
            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();

            Assert.AreEqual(1, invoked.Count);
            Assert.AreSame(child, invoked[0]);
            Assert.IsNull(ring.OpenFolder);
        });
    }

    [STATestMethod]
    public void HoldReleaseOverHybridRootInvokesItsOwnActionOnce()
    {
        WithOverlay((overlay, ring, folder, _) =>
        {
            folder.Action = ActionDefinition.BuiltInCommand("Родитель", BuiltInCommand.Paste, "paste");
            var invoked = new List<RingSlotDefinition>();
            overlay.ActionRequested += (_, args) => invoked.Add(args.Slot);
            ring.OpenSubmenu(folder, animate: false);
            Hover(ring, folder);

            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();
            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();

            Assert.IsFalse(overlay.IsVisible);
            Assert.AreEqual(1, invoked.Count);
            Assert.AreSame(folder, invoked[0]);
        });
    }

    [STATestMethod]
    public void HoldReleaseOverAdjustmentClosesWithoutAnExtraAdjustment()
    {
        WithOverlay((overlay, ring, folder, child) =>
        {
            child.Action = new ActionDefinition
            {
                Kind = ActionKind.AdjustParameter,
                AdjustParameter = new AdjustParameterAction { Parameter = AdjustableParameter.SystemVolume, Value = 1 },
            };
            var invoked = 0;
            overlay.ActionRequested += (_, _) => invoked++;
            ring.OpenSubmenu(folder, animate: false);
            Hover(ring, child);

            overlay.CommitHoveredAsync(isHoldRelease: true).GetAwaiter().GetResult();

            Assert.IsFalse(overlay.IsVisible);
            Assert.AreEqual(0, invoked);
        });
    }

    private static void WithOverlay(Action<RingOverlayWindow, RingMenuControl, RingSlotDefinition, RingSlotDefinition> check)
    {
        var child = RingSlotDefinition.ForAction(ActionDefinition.BuiltInCommand("Копировать", BuiltInCommand.Copy, "copy"));
        var folder = RingSlotDefinition.ForSubmenu("Папка", new RingDefinition { SlotCount = 1, Slots = [child] });
        var definition = new RingDefinition { SlotCount = 1, Slots = [folder] };
        var overlay = new RingOverlayWindow
        {
            Width = 600,
            Height = 500,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var ring = (RingMenuControl)overlay.FindName("Ring");
        ring.AnimationsEnabled = false;
        typeof(RingOverlayWindow).GetField("_effectiveAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(overlay, false);
        try
        {
            overlay.Show();
            overlay.UpdateLayout();
            ring.Present(definition, animate: false);
            ring.UpdateLayout();
            check(overlay, ring, folder, child);
        }
        finally
        {
            overlay.HideAnimatedAsync(animate: false).GetAwaiter().GetResult();
            overlay.Close();
        }
    }

    private static void Hover(RingMenuControl ring, RingSlotDefinition? slot) =>
        typeof(RingMenuControl).GetProperty(nameof(RingMenuControl.HoveredSlot))!.SetValue(ring, slot);
}
