using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.App.Windows;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class RingSlotSwapIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [STATestMethod]
    public void MainWindowUsesRealSaveTransactionAndFolderOrderIsStagedUntilSave()
    {
        // No production App instance, controller Start, user configuration file,
        // native window Show, input hook installation, or pointer injection.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ActionsRing;component/Themes/BaseStyles.xaml"),
        });
        var configuration = ConfigurationDefaults.Create();
        configuration.Onboarding.IsCompleted = true;
        configuration.Preferences.Appearance.Theme = ThemePreference.Light;
        configuration.Preferences.Appearance.EnableAnimations = false;
        configuration.Preferences.Appearance.ReduceMotion = true;
        configuration.GlobalProfile.RootRing = RingSlotSwapTests.CreateRing();
        var theme = new ThemeService();
        theme.Apply(configuration.Preferences.Appearance);
        var store = new MemoryStore(configuration);
        var controller = new ApplicationController(configuration, store, theme);
        using var updates = new UpdateService(SemanticVersion.Parse("2.4.0"), Path.Combine(Path.GetTempPath(), "ActionsRing-SwapTest-Updates"));
        var main = new MainWindow(controller, theme, updates);
        try
        {
            Invoke(main, "RefreshAll");
            Capture(main, 1280, 800, "main-root-light.png");
            var ring = configuration.GlobalProfile.RootRing;
            var source = ring.Slots[0];
            var target = ring.Slots[2];
            var originalSource = Json(source);
            var editor = (RingMenuControl)main.FindName("RingEditor");
            editor.PreviewSlotDrop(editor.CreateSlotDragData(source)!, target, DragDropEffects.Move);
            Capture(main, 1280, 800, "main-drop-target-light.png");
            editor.ClearSlotDragFeedback();

            Assert.IsTrue(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, source), target).GetAwaiter().GetResult());
            Assert.AreEqual(1, store.SaveCalls);
            Assert.AreSame(source, ring.Slots[2]);
            Assert.AreSame(source, editor.SelectedSlot);
            Assert.AreSame(source, editor.OpenFolder);
            var loaded = JsonSerializer.Deserialize<ActionsRingConfiguration>(store.SavedJson!, ConfigurationJson.Options)!;
            Assert.AreEqual(originalSource, Json(loaded.GlobalProfile.RootRing.Slots[2]));
            Assert.AreEqual(source.Label, ((TextBlock)main.FindName("SelectedSlotTitle")).Text);

            var child = source.Submenu!.Slots[1];
            var childTarget = source.Submenu.Slots[0];
            Assert.IsTrue(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, child), childTarget).GetAwaiter().GetResult());
            Assert.AreEqual(2, store.SaveCalls);
            Assert.AreSame(child, editor.SelectedSlot);
            Assert.AreSame(source, editor.OpenFolder);
            Assert.AreSame(child, source.Submenu.Slots[0]);
            Capture(main, 1280, 800, "main-submenu-light.png");

            Assert.IsFalse(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, source), child).GetAwaiter().GetResult());
            Assert.IsFalse(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, child), child).GetAwaiter().GetResult());
            Assert.AreEqual(2, store.SaveCalls);
            var beforeFailure = Json(configuration);
            store.FailSave = true;
            Assert.IsFalse(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, child), childTarget).GetAwaiter().GetResult());
            Assert.AreEqual(beforeFailure, Json(configuration));
            Assert.AreEqual(child.Id, editor.SelectedSlot?.Id);
            Assert.AreEqual(source.Id, editor.OpenFolder?.Id);
            Assert.IsFalse(main.SwapEditorSlotsAsync(new RingSlotDrag(ring, child), childTarget).GetAwaiter().GetResult(), "A pre-rollback payload is stale.");
            Assert.AreEqual(3, store.SaveCalls);

            VerifyFolderDraft(theme, configuration);
        }
        finally
        {
            main.RequestExit();
            main.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.Shutdown();
        }
    }

    private void VerifyFolderDraft(ThemeService theme, ActionsRingConfiguration configuration)
    {
        var folder = RingSlotSwapTests.CreateRing().Slots[0];
        var first = folder.Submenu!.Slots[0];
        var second = folder.Submenu.Slots[1];
        var original = Json(folder);
        var canceled = new FolderEditorWindow(folder);
        Assert.IsTrue(canceled.SwapChildSlots(first, second));
        Assert.AreSame(first, ((ListBoxItem)((ListBox)canceled.FindName("ChildOrderList")).Items[1]).Tag);
        Assert.AreEqual(original, Json(folder), "Preview order must not mutate the supplied folder.");
        Capture(canceled, 600, 700, "folder-order-light.png");
        var targetItem = (ListBoxItem)((ListBox)canceled.FindName("ChildOrderList")).Items[0];
        var draft = (RingDefinition)typeof(FolderEditorWindow).GetField("_draftOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canceled)!;
        var data = new DataObject(RingSlotDrag.Format, new RingSlotDrag(draft, first));
        var dragOver = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, targetItem, new Point()], null)!;
        dragOver.RoutedEvent = DragDrop.DragEnterEvent;
        targetItem.RaiseEvent(dragOver);
        Assert.AreEqual(DragDropEffects.Move, dragOver.Effects);
        var frame = (Border)targetItem.Content;
        Assert.IsTrue(((SolidColorBrush)frame.BorderBrush).Color.A > 0);
        var escape = (QueryContinueDragEventArgs)Activator.CreateInstance(typeof(QueryContinueDragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [true, DragDropKeyStates.LeftMouseButton], null)!;
        escape.RoutedEvent = DragDrop.QueryContinueDragEvent;
        targetItem.RaiseEvent(escape);
        Assert.AreEqual(DragAction.Cancel, escape.Action);
        Assert.AreEqual((byte)0, ((SolidColorBrush)frame.BorderBrush).Color.A);
        canceled.Close();
        Assert.AreEqual(original, Json(folder), "Cancel must leave all order and action data untouched.");

        configuration.Preferences.Appearance.Theme = ThemePreference.Dark;
        theme.Apply(configuration.Preferences.Appearance);
        var saved = new FolderEditorWindow(folder);
        Assert.IsTrue(saved.SwapChildSlots(first, second));
        Capture(saved, 600, 700, "folder-order-dark.png");
        saved.CommitChildOrder();
        Assert.AreSame(first, folder.Submenu.Slots[1]);
        Assert.AreSame(second, folder.Submenu.Slots[0]);
        Assert.IsFalse(saved.SwapChildSlots(first, first));
        Assert.IsFalse(saved.SwapChildSlots(first.Submenu!.Slots[0], second));
        saved.Close();
    }

    private void Capture(Window window, int width, int height, string name)
    {
        window.Width = width;
        window.Height = height;
        if (window is MainWindow) Invoke(window, "ApplyResponsiveLayout");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var directory = Path.Combine(Path.GetTempPath(), $"ActionsRing-SlotSwap-VisualTests-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        TestContext.AddResultFile(path);
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ConfigurationJson.Options);

    private sealed class MemoryStore(ActionsRingConfiguration configuration) : IConfigurationStore
    {
        public int SaveCalls { get; private set; }
        public bool FailSave { get; set; }
        public string? SavedJson { get; private set; }
        public string SettingsPath => "memory-only";
        public Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationLoadResult(configuration, ConfigurationLoadStatus.Loaded, []));
        public Task SaveAsync(ActionsRingConfiguration value, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (FailSave) throw new IOException("Simulated persistence failure");
            SavedJson = Json(value);
            return Task.CompletedTask;
        }
    }
}
