using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PureProject.Infrastructure;
using System.Runtime.CompilerServices;
using Windows.System;
using Windows.UI.Core;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private readonly List<KeyboardAccelerator> _configuredShortcuts = [];
    private readonly ConditionalWeakTable<ContentDialog, object> _shortcutDialogs = new();
    private sealed record ShortcutRecorder(Action<VirtualKey, VirtualKeyModifiers> Record);

    private void ConfigureShortcuts()
    {
        foreach (var shortcut in _configuredShortcuts) Root.KeyboardAccelerators.Remove(shortcut);
        _configuredShortcuts.Clear();
        foreach (var (id, value) in ShortcutSettings.GetEffective(_settings.Shortcuts))
        {
            var gesture = ShortcutSettings.Parse(value);
            var shortcut = new KeyboardAccelerator { Key = (VirtualKey)gesture.Key, Modifiers = ToWindowsModifiers(gesture.Modifiers) };
            shortcut.Invoked += async (_, args) =>
            {
                var focused = Root.XamlRoot is null ? null : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
                if (_currentActiveDialog is { } activeDialog && HasOpenShortcutPopup(activeDialog, focused)) return;
                if (FindShortcutRecorder(focused) is { } recorder)
                {
                    args.Handled = true;
                    recorder.Record(shortcut.Key, shortcut.Modifiers);
                    return;
                }
                if (!_ready) return;
                if (id == "ClosePanel")
                {
                    if (_currentActiveDialog is not null && CanCloseShortcutDialog(_currentActiveDialog) && !IsComposingShortcutInput(focused)
                        && !(IsShortcutTextInput(focused) && IsNativeTextGesture(gesture)))
                    {
                        args.Handled = true;
                        _currentActiveDialog.Hide();
                    }
                    return;
                }
                if (_busy) return;
                // TextBox/RichEditBox retain their native edit, selection and IME shortcuts.
                if (_dialogOpen || IsComposingShortcutInput(focused)
                    || (IsShortcutTextInput(focused) && (id is "Undo" or "Redo" or "RedoAlternate" || IsNativeTextGesture(gesture)))) return;
                args.Handled = true;
                await GuardAsync(() => InvokeShortcutAsync(id));
            };
            Root.KeyboardAccelerators.Add(shortcut);
            _configuredShortcuts.Add(shortcut);
        }
        ToolTipService.SetToolTip(TopSearchButton, ShortcutGestureLabel("GlobalSearch") + " 全局搜索");
        ToolTipService.SetToolTip(TopSettingsButton, ShortcutGestureLabel("Settings") + " 设置");
        ToolTipService.SetToolTip(UndoButton, ShortcutGestureLabel("Undo") + " 撤销");
        ToolTipService.SetToolTip(RedoButton, ShortcutGestureLabel("Redo") + " / " + ShortcutGestureLabel("RedoAlternate") + " 重做");
        AutomationProperties.SetAcceleratorKey(TopSearchButton, ShortcutGestureLabel("GlobalSearch"));
        AutomationProperties.SetAcceleratorKey(TopSettingsButton, ShortcutGestureLabel("Settings"));
        AutomationProperties.SetAcceleratorKey(UndoButton, ShortcutGestureLabel("Undo"));
        AutomationProperties.SetAcceleratorKey(RedoButton, ShortcutGestureLabel("Redo"));
    }

    private Task InvokeShortcutAsync(string id) => id switch
    {
        "New" => Current is null ? EditProjectAsync() : EditTaskAsync(null),
        "TaskSearch" => FocusTaskSearchAsync(),
        "GlobalSearch" => ShowGlobalSearchAsync(),
        "Settings" => ShowSettingsAsync(),
        "Undo" => UndoAsync(),
        "Redo" or "RedoAlternate" => RedoAsync(),
        _ => Task.CompletedTask
    };

    private string ShortcutGestureLabel(string id) => ShortcutSettings.GetEffective(_settings.Shortcuts)[id].Replace("+", " + ", StringComparison.Ordinal);

    private bool IsClosePanelShortcut(VirtualKey key)
    {
        var gesture = ShortcutSettings.Parse(ShortcutSettings.GetEffective(_settings.Shortcuts)["ClosePanel"]);
        return gesture.Key == (int)key && ToWindowsModifiers(gesture.Modifiers) == CurrentShortcutModifiers();
    }

    private void ConfigureDialogShortcuts(ContentDialog dialog)
    {
        if (_shortcutDialogs.TryGetValue(dialog, out _)) return;
        _shortcutDialogs.Add(dialog, new object());
        dialog.PreviewKeyDown += (_, args) =>
        {
            var focused = FocusManager.GetFocusedElement(dialog.XamlRoot) as DependencyObject;
            // Let the innermost native picker/flyout receive Escape before the dialog.
            // This also preserves Escape dismissal after ClosePanel is customized.
            if (HasOpenShortcutPopup(dialog, focused)) return;
            if (TryRecordShortcut(focused, args)) return;
            if (IsComposingShortcutInput(focused)) return;
            if (IsClosePanelShortcut(args.Key))
            {
                var closeGesture = ShortcutSettings.Parse(ShortcutSettings.GetEffective(_settings.Shortcuts)["ClosePanel"]);
                if (IsShortcutTextInput(focused) && IsNativeTextGesture(closeGesture)) return;
                args.Handled = true;
                if (CanCloseShortcutDialog(dialog)) dialog.Hide();
            }
            // ContentDialog normally closes on Escape; a custom close binding replaces it.
            else if (args.Key == VirtualKey.Escape && CurrentShortcutModifiers() == VirtualKeyModifiers.None)
                args.Handled = true;
        };
    }

    private bool CanCloseShortcutDialog(ContentDialog dialog)
        => !_busy || AutomationProperties.GetAutomationId(dialog) == "TransferProgress";

    private static bool HasOpenShortcutPopup(ContentDialog dialog, DependencyObject? focused)
    {
        static bool IsExpandedPicker(DependencyObject element) => element is ComboBox { IsDropDownOpen: true }
            or CalendarDatePicker { IsCalendarOpen: true } or AutoSuggestBox { IsSuggestionListOpen: true };
        for (var element = focused; element is not null; element = VisualTreeHelper.GetParent(element))
            if (IsExpandedPicker(element)) return true;
        // WinUI moves focus into a separate popup tree. Read the owning controls'
        // public state inside this dialog rather than inferring it from Popup flags.
        if (ShellDescendants<Control>(dialog).Any(IsExpandedPicker)) return true;
        if (dialog.XamlRoot is null) return false;
        // Flyouts may also use popup hosts without IsLightDismissEnabled. Inspect
        // only open popup hosts and their presenters; exclude the dialog's own host.
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot))
        {
            if (!popup.IsOpen || popup.Child is null) continue;
            var hostsDialog = false;
            for (DependencyObject? element = dialog; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, popup.Child)) { hostsDialog = true; break; }
            if (!hostsDialog && (popup.IsLightDismissEnabled
                || ShellDescendants<Control>(popup.Child).Any(element => element is FlyoutPresenter or MenuFlyoutPresenter))) return true;
        }
        return false;
    }

    private StackPanel BuildShortcutSettingsPage(Func<AppSettings, Task<bool>> saveSettings, Dictionary<string, string>? initialDraft = null)
    {
        var page = Column(16);
        page.Children.Add(EditorText("选择快捷键框并按下新组合，再点击“保存快捷键”。Tab 可切换到下一项。", 12, "SecondaryTextBrush"));
        page.Children.Add(EditorText("字母、数字和标点需搭配 Ctrl 或 Alt；也可使用 F1–F12。关闭面板默认使用 Esc。输入文字时保留文本框原有快捷键。", 11, "MutedTextBrush"));
        var draft = initialDraft is null ? ShortcutSettings.GetEffective(_settings.Shortcuts) : new Dictionary<string, string>(initialDraft, StringComparer.Ordinal);
        var inputs = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed, IsClosable = true };
        AutomationProperties.SetAutomationId(error, "ShortcutsValidation");
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
        var saving = false;
        void ShowFeedback(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
        {
            error.Message = message;
            error.Severity = severity;
            error.Visibility = Visibility.Visible;
            error.IsOpen = true;
        }
        void ClearFeedback() { error.IsOpen = false; error.Visibility = Visibility.Collapsed; }
        foreach (var definition in ShortcutSettings.Definitions)
        {
            var input = new TextBox
            {
                Header = definition.Label,
                Text = draft[definition.Id].Replace("+", " + ", StringComparison.Ordinal),
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 32,
                FontFamily = UiFont,
                FontSize = 13
            };
            NativeControlPalette.SetIsEnabled(input, true);
            AutomationProperties.SetAutomationId(input, "ShortcutInput_" + definition.Id);
            AutomationProperties.SetName(input, definition.Label + "快捷键");
            AutomationProperties.SetHelpText(input, "聚焦后按下新的快捷键组合；Tab 切换到下一项。默认 " + definition.DefaultGesture);
            input.Tag = new ShortcutRecorder((key, modifiers) =>
            {
                if (saving || IsModifierKey(key)) return;
                try
                {
                    if ((modifiers & VirtualKeyModifiers.Windows) != 0) throw new ArgumentException("Windows 组合键由系统使用，请选择其他快捷键。");
                    var gesture = ShortcutSettings.Parse(new ShortcutGesture((int)key, FromWindowsModifiers(modifiers)).ToString());
                    draft[definition.Id] = gesture.ToString();
                    input.Text = gesture.ToString().Replace("+", " + ", StringComparison.Ordinal);
                    input.SelectAll();
                    ClearFeedback();
                    try { ShortcutSettings.Validate(draft); }
                    catch (ArgumentException conflict) { ShowFeedback(conflict.Message); }
                }
                catch (ArgumentException invalid) { ShowFeedback(invalid.Message); }
            });
            input.PreviewKeyDown += (_, args) => TryRecordShortcut(input, args);
            input.GotFocus += (_, _) => input.SelectAll();
            inputs.Add(definition.Id, input);
            page.Children.Add(input);
        }
        page.Children.Add(error);
        Button? save = null;
        Button? reset = null;
        save = EditorButton("保存快捷键", async () =>
        {
            if (saving) return;
            try
            {
                var validated = ShortcutSettings.GetEffective(draft);
                saving = true;
                save!.IsEnabled = reset!.IsEnabled = false;
                foreach (var input in inputs.Values) input.IsEnabled = false;
                if (!await saveSettings(_settings with { Shortcuts = validated })) return;
                ConfigureShortcuts();
                ShowFeedback("快捷键已保存并生效。", InfoBarSeverity.Success);
            }
            catch (ArgumentException invalid) { ShowFeedback(invalid.Message); }
            finally
            {
                saving = false;
                save!.IsEnabled = reset!.IsEnabled = true;
                foreach (var input in inputs.Values) input.IsEnabled = true;
            }
        }, true);
        reset = EditorButton("恢复默认", () =>
        {
            if (saving) return Task.CompletedTask;
            draft = ShortcutSettings.GetEffective(new Dictionary<string, string>());
            foreach (var (id, input) in inputs) input.Text = draft[id].Replace("+", " + ", StringComparison.Ordinal);
            ShowFeedback("已恢复默认组合，点击“保存快捷键”后生效。", InfoBarSeverity.Informational);
            return Task.CompletedTask;
        });
        AutomationProperties.SetAutomationId(save, "ShortcutsSave");
        AutomationProperties.SetAutomationId(reset, "ShortcutsReset");
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        save.HorizontalAlignment = reset.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(reset); Grid.SetColumn(save, 1); actions.Children.Add(save);
        page.Children.Add(actions);
        return page;
    }

    private static bool TryRecordShortcut(DependencyObject? focused, KeyRoutedEventArgs args)
    {
        // Modifiers alone and Tab retain WinUI's native focus-navigation behavior.
        if (FindShortcutRecorder(focused) is not { } recorder || args.Key == VirtualKey.Tab || IsModifierKey(args.Key)) return false;
        args.Handled = true;
        recorder.Record(args.Key, CurrentShortcutModifiers());
        return true;
    }

    private static void RecordShortcut(TextBox input, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (input.Tag is not ShortcutRecorder recorder) throw new ArgumentException("此输入框不能录制快捷键。", nameof(input));
        recorder.Record(key, modifiers);
    }

    private static ShortcutRecorder? FindShortcutRecorder(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox { Tag: ShortcutRecorder recorder }) return recorder;
        return null;
    }

    private static bool IsShortcutTextInput(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or RichEditBox or PasswordBox) return true;
        return false;
    }

    private static bool IsNativeTextGesture(ShortcutGesture gesture)
        => (gesture.Modifiers.HasFlag(ShortcutModifiers.Control) && gesture.Modifiers.HasFlag(ShortcutModifiers.Alt))
        || (gesture.Modifiers.HasFlag(ShortcutModifiers.Control) && gesture.Key is 'A' or 'C' or 'V' or 'X' or 'Z' or 'Y');

    private bool IsComposingShortcutInput(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox input) return IsTextComposing(input);
        return false;
    }

    private static bool IsModifierKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static VirtualKeyModifiers CurrentShortcutModifiers()
    {
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        var modifiers = VirtualKeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (Down(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    private static VirtualKeyModifiers ToWindowsModifiers(ShortcutModifiers modifiers)
        => (modifiers.HasFlag(ShortcutModifiers.Control) ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None)
        | (modifiers.HasFlag(ShortcutModifiers.Alt) ? VirtualKeyModifiers.Menu : VirtualKeyModifiers.None)
        | (modifiers.HasFlag(ShortcutModifiers.Shift) ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None);

    private static ShortcutModifiers FromWindowsModifiers(VirtualKeyModifiers modifiers)
        => (modifiers.HasFlag(VirtualKeyModifiers.Control) ? ShortcutModifiers.Control : ShortcutModifiers.None)
        | (modifiers.HasFlag(VirtualKeyModifiers.Menu) ? ShortcutModifiers.Alt : ShortcutModifiers.None)
        | (modifiers.HasFlag(VirtualKeyModifiers.Shift) ? ShortcutModifiers.Shift : ShortcutModifiers.None);
}
