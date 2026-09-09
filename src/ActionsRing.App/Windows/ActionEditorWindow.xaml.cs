using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ActionsRing.App.Controls;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using ActionsRing.Core.Domain;
using Microsoft.Win32;
using MouseButton = ActionsRing.Core.Domain.MouseButton;

namespace ActionsRing.App.Windows;

public partial class ActionEditorWindow : Window
{
    private readonly ActionDefinition _working;
    private readonly Func<CancellationToken, Task<KeyChord>> _captureShortcut;
    private readonly InstalledApplicationDiscoveryService _applicationDiscovery = new();
    private readonly ApplicationVisualService _visuals = new();
    private CancellationTokenSource? _captureCancellation;
    private string? _automaticIconReference;
    private string? _selectedIconReference;

    public ActionEditorWindow(
        ActionDefinition action,
        Func<CancellationToken, Task<KeyChord>> captureShortcut)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(captureShortcut);
        _working = Clone(action);
        _captureShortcut = captureShortcut;
        InitializeComponent();
        _selectedIconReference = string.Equals(_working.Icon, DefaultIcon(_working.Kind), StringComparison.OrdinalIgnoreCase)
            ? null
            : _working.Icon;
        SourceInitialized += (_, _) => FitToMonitorWorkArea();
        Closed += (_, _) => _captureCancellation?.Cancel();
        PopulateStaticLists();
        LoadAction();
    }

    public ActionDefinition? Result { get; private set; }

    private void FitToMonitorWorkArea()
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));
        var bottomRight = fromDevice.Transform(new Point(workArea.Right, workArea.Bottom));
        var bounds = WindowPlacementCalculator.FitCentered(
            Width,
            Height,
            MinWidth,
            MinHeight,
            new Rect(topLeft, bottomRight));

        // At very high scaling the logical work area can be smaller than the
        // design minimum. Lower the minimum for this instance so the fixed
        // Save/Cancel row remains reachable and let the body scroll.
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private void PopulateStaticLists()
    {
        MouseButtonCombo.DisplayMemberPath = nameof(DisplayOption<MouseButton>.Label);
        MouseButtonCombo.SelectedValuePath = nameof(DisplayOption<MouseButton>.Value);
        MouseButtonCombo.ItemsSource = new[]
        {
            new DisplayOption<MouseButton>(MouseButton.Left, "Левая кнопка"),
            new DisplayOption<MouseButton>(MouseButton.Right, "Правая кнопка"),
            new DisplayOption<MouseButton>(MouseButton.Middle, "Средняя кнопка"),
            new DisplayOption<MouseButton>(MouseButton.XButton1, "Боковая кнопка 1 (Назад)"),
            new DisplayOption<MouseButton>(MouseButton.XButton2, "Боковая кнопка 2 (Вперёд)"),
            new DisplayOption<MouseButton>(MouseButton.WheelUp, "Колесо вверх"),
            new DisplayOption<MouseButton>(MouseButton.WheelDown, "Колесо вниз"),
            new DisplayOption<MouseButton>(MouseButton.WheelLeft, "Колесо влево"),
            new DisplayOption<MouseButton>(MouseButton.WheelRight, "Колесо вправо"),
        };
        ClickCountCombo.ItemsSource = new[] { 1, 2, 3 };
        ParameterCombo.DisplayMemberPath = nameof(DisplayOption<AdjustableParameter>.Label);
        ParameterCombo.SelectedValuePath = nameof(DisplayOption<AdjustableParameter>.Value);
        ParameterCombo.ItemsSource = new[]
        {
            new DisplayOption<AdjustableParameter>(AdjustableParameter.SystemVolume, "Громкость системы"),
            new DisplayOption<AdjustableParameter>(AdjustableParameter.ScreenBrightness, "Яркость экрана"),
            new DisplayOption<AdjustableParameter>(AdjustableParameter.Zoom, "Масштаб"),
            new DisplayOption<AdjustableParameter>(AdjustableParameter.VerticalScroll, "Вертикальная прокрутка"),
            new DisplayOption<AdjustableParameter>(AdjustableParameter.HorizontalScroll, "Горизонтальная прокрутка"),
        };
    }

    private void LoadAction()
    {
        EditorTitle.Text = _working.Name;
        EditorSubtitle.Text = ActionTypeName(_working.Kind);
        RefreshHeaderIcon();
        NameBox.Text = _working.Name;

        foreach (var panel in new[]
                 {
                     ShortcutPanel, LaunchPanel, UriPanel, TextPanel, MousePanel,
                     AdjustmentPanel, SequencePanel, BuiltInPanel,
                 })
        {
            panel.Visibility = Visibility.Collapsed;
        }

        switch (_working.Kind)
        {
            case ActionKind.KeyboardShortcut:
                ShortcutPanel.Visibility = Visibility.Visible;
                _working.KeyboardShortcut ??= new KeyboardShortcutAction();
                RefreshChords();
                break;
            case ActionKind.LaunchApplication:
                LaunchPanel.Visibility = Visibility.Visible;
                _working.LaunchApplication ??= new LaunchApplicationAction();
                LaunchPathBox.Text = _working.LaunchApplication.ExecutablePath;
                LaunchArgumentsBox.Text = _working.LaunchApplication.Arguments ?? string.Empty;
                WorkingDirectoryBox.Text = _working.LaunchApplication.WorkingDirectory ?? string.Empty;
                RunAsAdminCheck.IsChecked = _working.LaunchApplication.RunAsAdministrator;
                break;
            case ActionKind.OpenUri:
                UriPanel.Visibility = Visibility.Visible;
                _working.OpenUri ??= new OpenUriAction();
                UriBox.Text = _working.OpenUri.Uri;
                break;
            case ActionKind.TypeText:
                TextPanel.Visibility = Visibility.Visible;
                _working.TypeText ??= new TypeTextAction();
                TextValueBox.Text = _working.TypeText.Text;
                CharacterDelayBox.Text = _working.TypeText.CharacterDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
                break;
            case ActionKind.MouseInput:
                MousePanel.Visibility = Visibility.Visible;
                _working.MouseInput ??= new MouseInputAction();
                MouseButtonCombo.SelectedValue = _working.MouseInput.Button;
                ClickCountCombo.SelectedItem = _working.MouseInput.ClickCount;
                break;
            case ActionKind.AdjustParameter:
                AdjustmentPanel.Visibility = Visibility.Visible;
                _working.AdjustParameter ??= new AdjustParameterAction();
                ParameterCombo.SelectedValue = _working.AdjustParameter.Parameter;
                AdjustmentValueBox.Text = _working.AdjustParameter.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case ActionKind.Sequence:
                SequencePanel.Visibility = Visibility.Visible;
                _working.Sequence ??= new SequenceAction();
                RefreshSequence();
                break;
            default:
                BuiltInPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SaveFields()
    {
        _working.Name = NameBox.Text.Trim();
        _working.Icon = _selectedIconReference;
        switch (_working.Kind)
        {
            case ActionKind.LaunchApplication when _working.LaunchApplication is not null:
                _working.LaunchApplication.ExecutablePath = LaunchPathBox.Text.Trim();
                _working.LaunchApplication.Arguments = EmptyToNull(LaunchArgumentsBox.Text);
                _working.LaunchApplication.WorkingDirectory = EmptyToNull(WorkingDirectoryBox.Text);
                _working.LaunchApplication.RunAsAdministrator = RunAsAdminCheck.IsChecked == true;
                break;
            case ActionKind.OpenUri when _working.OpenUri is not null:
                _working.OpenUri.Uri = UriBox.Text.Trim();
                break;
            case ActionKind.TypeText when _working.TypeText is not null:
                _working.TypeText.Text = TextValueBox.Text;
                _working.TypeText.CharacterDelayMilliseconds = ParseInt(CharacterDelayBox.Text, 0, 5000, 0);
                break;
            case ActionKind.MouseInput when _working.MouseInput is not null:
                _working.MouseInput.Button = MouseButtonCombo.SelectedValue is MouseButton mouseButton ? mouseButton : MouseButton.Left;
                _working.MouseInput.ClickCount = ClickCountCombo.SelectedItem is int clicks ? clicks : 1;
                break;
            case ActionKind.AdjustParameter when _working.AdjustParameter is not null:
                _working.AdjustParameter.Parameter = ParameterCombo.SelectedValue is AdjustableParameter parameter
                    ? parameter
                    : AdjustableParameter.SystemVolume;
                _working.AdjustParameter.Value = ParseDouble(AdjustmentValueBox.Text, -1000, 1000);
                break;
        }
    }

    private string? ValidateAction()
    {
        if (string.IsNullOrWhiteSpace(_working.Name)) return "Введите название действия.";
        return _working.Kind switch
        {
            ActionKind.KeyboardShortcut when _working.KeyboardShortcut?.Chords.Count is not > 0 => "Запишите хотя бы одно сочетание клавиш.",
            ActionKind.LaunchApplication when string.IsNullOrWhiteSpace(_working.LaunchApplication?.ExecutablePath) => "Выберите приложение, файл или папку.",
            ActionKind.OpenUri when !Uri.TryCreate(_working.OpenUri?.Uri, UriKind.Absolute, out _) => "Введите корректный полный URL.",
            ActionKind.TypeText when string.IsNullOrEmpty(_working.TypeText?.Text) => "Введите текст для вставки.",
            ActionKind.Sequence when _working.Sequence?.Steps.Count is not > 0 => "Добавьте хотя бы один шаг.",
            _ => null,
        };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            OnStepDelayChanged(sender, e);
            SaveFields();
        }
        catch (InvalidDataException exception)
        {
            ShowValidation(exception.Message);
            return;
        }
        var error = ValidateAction();
        if (error is not null)
        {
            ShowValidation(error);
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            if (_selectedIconReference is null
                && _working.Kind == ActionKind.OpenUri
                && _working.OpenUri is not null)
            {
                _automaticIconReference = await _visuals.GetFaviconAsync(_working.OpenUri.Uri);
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
        if (!IsLoaded)
        {
            return;
        }
        Result = _working;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnBrowseLaunch(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите приложение или файл",
            Filter = "Приложения (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            LaunchPathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Открыть приложение")
            {
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
            var application = new InstalledApplicationInfo(
                NameBox.Text,
                dialog.FileName,
                Path.GetFileNameWithoutExtension(dialog.FileName),
                dialog.FileName,
                IconPath: dialog.FileName);
            _automaticIconReference = await _visuals.CacheApplicationIconAsync(application);
            RefreshHeaderIcon();
        }
    }

    private async void OnChooseInstalledApplication(object sender, RoutedEventArgs e)
    {
        var picker = new InstalledApplicationPickerWindow(_applicationDiscovery, _visuals) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedApplication is not { } application)
        {
            return;
        }

        LaunchPathBox.Text = application.LaunchTarget;
        NameBox.Text = application.Name;
        LaunchArgumentsBox.Clear();
        WorkingDirectoryBox.Clear();
        RunAsAdminCheck.IsChecked = false;
        _automaticIconReference = await _visuals.CacheApplicationIconAsync(application);
        RefreshHeaderIcon();
    }

    private async void OnRecordChord(object sender, RoutedEventArgs e)
    {
        if ((_working.KeyboardShortcut?.Chords.Count ?? 0) >= ConfigurationNormalizer.MaximumShortcutChords)
        {
            ShowValidation($"Можно сохранить не более {ConfigurationNormalizer.MaximumShortcutChords} сочетаний.");
            return;
        }
        RecordChordButton.IsEnabled = false;
        RecordChordButton.Content = "Нажмите сочетание…";
        RecordHint.Visibility = Visibility.Visible;
        _captureCancellation?.Dispose();
        var captureCancellation = new CancellationTokenSource();
        _captureCancellation = captureCancellation;
        try
        {
            var chord = await _captureShortcut(captureCancellation.Token);
            _working.KeyboardShortcut ??= new KeyboardShortcutAction();
            _working.KeyboardShortcut.Chords.Add(chord);
            RefreshChords();
        }
        catch (OperationCanceledException)
        {
            // Esc or closing the editor cancels recording.
        }
        catch (Exception exception)
        {
            AppLog.Error("Shortcut capture failed", exception);
            ShowValidation("Не удалось записать сочетание. Повторите попытку.");
        }
        finally
        {
            if (ReferenceEquals(_captureCancellation, captureCancellation))
            {
                _captureCancellation = null;
            }
            captureCancellation.Dispose();
            FinishChordRecording();
        }
    }

    private void FinishChordRecording()
    {
        RecordChordButton.IsEnabled = true;
        RecordChordButton.Content = "Добавить сочетание";
        RecordHint.Visibility = Visibility.Collapsed;
    }

    private void OnClearChords(object sender, RoutedEventArgs e)
    {
        _working.KeyboardShortcut?.Chords.Clear();
        RefreshChords();
    }

    private void RefreshChords()
    {
        ChordItems.Items.Clear();
        var chords = _working.KeyboardShortcut?.Chords ?? [];
        foreach (var chord in chords)
        {
            var description = InputBindingMapper.Describe(new TriggerBinding
            {
                Kind = InputBindingKind.Keyboard,
                Keyboard = chord,
            });
            ChordItems.Items.Add(new Border
            {
                Style = TryFindResource("PillBorder") as Style,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = description, FontWeight = FontWeights.SemiBold },
            });
        }
    }

    private void RefreshSequence(ActionSequenceStep? selectedStep = null)
    {
        selectedStep ??= (SequenceList.SelectedItem as ListBoxItem)?.Tag as ActionSequenceStep;
        SequenceList.Items.Clear();
        foreach (var step in _working.Sequence?.Steps ?? [])
        {
            SequenceList.Items.Add(new ListBoxItem
            {
                Content = $"{step.Action.Name}  ·  {step.DelayAfterMilliseconds} мс",
                Tag = step,
                Padding = new Thickness(9),
            });
        }
        if (selectedStep is not null)
        {
            SequenceList.SelectedItem = SequenceList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Tag, selectedStep));
        }
        if (SequenceList.Items.Count > 0 && SequenceList.SelectedIndex < 0)
        {
            SequenceList.SelectedIndex = 0;
        }
    }

    private void OnAddShortcutStep(object sender, RoutedEventArgs e) => AddSequenceStep(
        ActionDefinition.Shortcut("Сочетание клавиш", "Space", KeyboardModifiers.Control, "keyboard"));

    private void OnAddTextStep(object sender, RoutedEventArgs e) => AddSequenceStep(new ActionDefinition
    {
        Name = "Вставить текст",
        Kind = ActionKind.TypeText,
        Icon = "text",
        TypeText = new TypeTextAction { Text = "Текст" },
    });

    private void OnAddUriStep(object sender, RoutedEventArgs e) => AddSequenceStep(new ActionDefinition
    {
        Name = "Открыть ссылку",
        Kind = ActionKind.OpenUri,
        Icon = "browser",
        OpenUri = new OpenUriAction { Uri = "https://" },
    });

    private void AddSequenceStep(ActionDefinition action)
    {
        _working.Sequence ??= new SequenceAction();
        if (_working.Sequence.Steps.Count >= ConfigurationNormalizer.MaximumSequenceSteps)
        {
            ShowValidation($"Можно добавить не более {ConfigurationNormalizer.MaximumSequenceSteps} шагов.");
            return;
        }
        var editor = new ActionEditorWindow(action, _captureShortcut) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null)
        {
            return;
        }
        _working.Sequence.Steps.Add(new ActionSequenceStep { Action = editor.Result });
        RefreshSequence();
        SequenceList.SelectedIndex = SequenceList.Items.Count - 1;
    }

    private void OnEditSequenceStep(object sender, RoutedEventArgs e)
    {
        if ((SequenceList.SelectedItem as ListBoxItem)?.Tag is not ActionSequenceStep step)
        {
            return;
        }
        var editor = new ActionEditorWindow(step.Action, _captureShortcut) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            step.Action = editor.Result;
            RefreshSequence();
        }
    }

    private void OnRemoveSequenceStep(object sender, RoutedEventArgs e)
    {
        if ((SequenceList.SelectedItem as ListBoxItem)?.Tag is not ActionSequenceStep step)
        {
            return;
        }
        _working.Sequence?.Steps.Remove(step);
        RefreshSequence();
    }

    private void OnSequenceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StepDelayBox.Text = (SequenceList.SelectedItem as ListBoxItem)?.Tag is ActionSequenceStep step
            ? step.DelayAfterMilliseconds.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private void OnStepDelayChanged(object sender, RoutedEventArgs e)
    {
        if ((SequenceList.SelectedItem as ListBoxItem)?.Tag is ActionSequenceStep step)
        {
            step.DelayAfterMilliseconds = ParseInt(StepDelayBox.Text, 0, 60_000, 0);
            RefreshSequence(step);
        }
    }

    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        var picker = new IconPickerWindow(_selectedIconReference) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            _selectedIconReference = picker.SelectedIcon;
            RefreshHeaderIcon();
        }
    }

    private void OnAutomaticIcon(object sender, RoutedEventArgs e)
    {
        _selectedIconReference = null;
        RefreshHeaderIcon();
    }

    private void RefreshHeaderIcon()
    {
        _working.Icon = _selectedIconReference;
        HeaderIcon.SetIcon(_selectedIconReference ?? _automaticIconReference, _working);
        IconChoiceText.Text = _selectedIconReference is null ? "Автоматический выбор" : "Своя иконка";
    }

    private static string ActionTypeName(ActionKind kind) => kind switch
    {
        ActionKind.KeyboardShortcut => "Сочетание клавиш",
        ActionKind.LaunchApplication => "Запуск",
        ActionKind.OpenUri => "Веб-ссылка",
        ActionKind.TypeText => "Ввод текста",
        ActionKind.BuiltIn => "Системное действие",
        ActionKind.MouseInput => "Мышь",
        ActionKind.Sequence => "Последовательность действий",
        ActionKind.AdjustParameter => "Регулируемый параметр",
        _ => "Действие",
    };

    private static string DefaultIcon(ActionKind kind) => kind switch
    {
        ActionKind.KeyboardShortcut => "keyboard",
        ActionKind.LaunchApplication => "app",
        ActionKind.OpenUri => "browser",
        ActionKind.TypeText => "text",
        ActionKind.MouseInput => "mouse",
        ActionKind.Sequence => "multi",
        ActionKind.AdjustParameter => "settings",
        _ => "settings",
    };

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int ParseInt(string value, int min, int max, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? Math.Clamp(result, min, max)
            : fallback;
    private static double ParseDouble(string value, double min, double max)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidDataException("Введите числовое значение шага.");
        }

        if (!double.IsFinite(result))
        {
            throw new InvalidDataException("Введите конечное числовое значение.");
        }

        return Math.Clamp(result, min, max);
    }

    private void ShowValidation(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.Visibility = Visibility.Visible;
    }

    private sealed record DisplayOption<T>(T Value, string Label);

    private static ActionDefinition Clone(ActionDefinition action)
    {
        var json = JsonSerializer.Serialize(action, ConfigurationJson.Options);
        return JsonSerializer.Deserialize<ActionDefinition>(json, ConfigurationJson.Options)
               ?? throw new InvalidDataException("Could not clone the action.");
    }
}
