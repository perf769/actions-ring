using ActionsRing.App.Services;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlatformInput = ActionsRing.Platform.Windows.Input;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class InputBindingMapperTests
{
    [TestMethod]
    public void SideMouseButtonsDoNotShowMissingBindingWarning()
    {
        var binding = new TriggerBinding { Kind = InputBindingKind.MouseButton, Button = MouseButton.XButton2 };

        Assert.IsNull(InputBindingMapper.GetConflictWarning(binding));
    }

    [TestMethod]
    public void StandaloneSideMouseTrigger_AllowsHeldKeyboardModifiers()
    {
        var binding = new TriggerBinding
        {
            Kind = InputBindingKind.MouseButton,
            Button = MouseButton.XButton2,
            MouseModifiers = KeyboardModifiers.None,
        };
        var gesture = InputBindingMapper.ToPlatform(binding);
        var input = new PlatformInput.GlobalInputEvent(
            PlatformInput.InputDeviceKind.Mouse,
            PlatformInput.InputEventKind.MouseButtonDown,
            DateTimeOffset.UtcNow,
            new PlatformInput.ScreenPoint(100, 100),
            PlatformInput.InputModifiers.Shift,
            MouseButton: PlatformInput.MouseButton.XButton2);

        Assert.IsTrue(gesture.AllowAdditionalModifiers);
        Assert.IsTrue(gesture.Matches(input));
    }

    [TestMethod]
    public void ModifierSpecificMouseTrigger_RemainsExact()
    {
        var binding = new TriggerBinding
        {
            Kind = InputBindingKind.MouseButton,
            Button = MouseButton.XButton2,
            MouseModifiers = KeyboardModifiers.Control,
        };
        var gesture = InputBindingMapper.ToPlatform(binding);
        var input = new PlatformInput.GlobalInputEvent(
            PlatformInput.InputDeviceKind.Mouse,
            PlatformInput.InputEventKind.MouseButtonDown,
            DateTimeOffset.UtcNow,
            new PlatformInput.ScreenPoint(100, 100),
            PlatformInput.InputModifiers.Control | PlatformInput.InputModifiers.Shift,
            MouseButton: PlatformInput.MouseButton.XButton2);

        Assert.IsFalse(gesture.AllowAdditionalModifiers);
        Assert.IsFalse(gesture.Matches(input));
    }

    [TestMethod]
    public void HorizontalWheelAndModifiersRoundTrip()
    {
        var gesture = PlatformInput.InputGesture.Wheel(
            PlatformInput.MouseWheelDirection.Right,
            PlatformInput.InputModifiers.Control | PlatformInput.InputModifiers.Shift);

        var core = InputBindingMapper.ToCore(gesture, ActivationMode.Hold);
        var roundTrip = InputBindingMapper.ToPlatform(core);

        Assert.AreEqual(MouseButton.WheelRight, core.Button);
        Assert.AreEqual(ActivationMode.Toggle, core.ActivationMode);
        Assert.AreEqual(KeyboardModifiers.Control | KeyboardModifiers.Shift, core.MouseModifiers);
        Assert.AreEqual(PlatformInput.MouseWheelDirection.Right, roundTrip.WheelDirection);
        Assert.AreEqual(PlatformInput.InputModifiers.Control | PlatformInput.InputModifiers.Shift, roundTrip.Modifiers);
    }

    [TestMethod]
    public void ArbitraryFunctionKeyRoundTripsByVirtualKey()
    {
        var gesture = PlatformInput.InputGesture.Keyboard(0x87, PlatformInput.InputModifiers.Control);

        var core = InputBindingMapper.ToCore(gesture, ActivationMode.Toggle);
        var roundTrip = InputBindingMapper.ToPlatform(core);

        Assert.AreEqual("F24", core.Keyboard?.Key);
        Assert.AreEqual(0x87, roundTrip.VirtualKey);
        Assert.AreEqual(PlatformInput.InputModifiers.Control, roundTrip.Modifiers);
    }

    [TestMethod]
    public void AltAndWindowsModifiersAreRejectedForActivation()
    {
        var gesture = PlatformInput.InputGesture.Keyboard(0x87, PlatformInput.InputModifiers.Windows);

        Assert.ThrowsException<InvalidDataException>(
            () => InputBindingMapper.ToCore(gesture, ActivationMode.Toggle));
    }

    [DataTestMethod]
    [DataRow(0xA0, "LeftShift")]
    [DataRow(0xA2, "LeftControl")]
    [DataRow(0xA4, "LeftAlt")]
    [DataRow(0x5B, "LeftWindows")]
    public void StandaloneModifierKeyCanBeUsedAsTrigger(int virtualKey, string expectedName)
    {
        var gesture = PlatformInput.InputGesture.Keyboard(virtualKey, PlatformInput.InputModifiers.None);

        var binding = InputBindingMapper.ToCore(gesture, ActivationMode.Hold);

        Assert.AreEqual(expectedName, binding.Keyboard?.Key);
        Assert.AreEqual(virtualKey, InputBindingMapper.ToPlatform(binding).VirtualKey);
        Assert.IsFalse(string.IsNullOrWhiteSpace(InputBindingMapper.GetConflictWarning(binding)));
    }

    [TestMethod]
    public void RawVirtualKeyOutsideWin32Range_IsRejectedByMapper()
    {
        Assert.ThrowsException<ArgumentException>(
            () => InputBindingMapper.KeyNameToVirtualKey("VK_0x0100"));
    }
}
