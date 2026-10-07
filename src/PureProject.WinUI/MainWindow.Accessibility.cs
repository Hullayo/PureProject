using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private sealed record UiFocusSnapshot(Control Control, string Id, string ElementName,
        FocusState State, int SelectionStart, int SelectionLength);

    private sealed class MenuFocusOrigin { public UiFocusSnapshot? Snapshot { get; set; } }
    private readonly ConditionalWeakTable<MenuFlyoutItem, MenuFocusOrigin> _menuFocusOrigins = new();
    private UiFocusSnapshot? _menuActionFocus;
    private int _focusRequestVersion;
    private Action? _pendingFocusAction;
    private bool _focusActionQueued;

    private void EnqueueLatestFocus(Action action)
    {
        _pendingFocusAction = action;
        if (_focusActionQueued) return;
        _focusActionQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _focusActionQueued = false;
            var pending = _pendingFocusAction; _pendingFocusAction = null;
            pending?.Invoke();
        })) { _focusActionQueued = false; _pendingFocusAction = null; }
    }

    private void CaptureMenuOrigin(MenuFlyout menu, string fallbackId)
    {
        var origin = new MenuFocusOrigin();
        void Bind(IEnumerable<MenuFlyoutItemBase> items)
        {
            foreach (var item in items)
            {
                if (item is MenuFlyoutItem command)
                {
                    _menuFocusOrigins.Remove(command);
                    _menuFocusOrigins.Add(command, origin);
                }
                else if (item is MenuFlyoutSubItem submenu) Bind(submenu.Items);
            }
        }
        menu.Opening += (_, _) =>
        {
            // Opening may populate a task menu lazily before this handler runs.
            Bind(menu.Items);
            var anchor = FindUiControl(fallbackId, null)
                ?? FindUiControl(fallbackId.Replace("TaskMore:", "TaskOpen:"), null);
            origin.Snapshot = anchor is null ? CaptureUiFocus() : new(anchor,
                AutomationProperties.GetAutomationId(anchor), anchor.Name, FocusState.Keyboard, 0, 0);
        };
        menu.Closed += (_, _) => RestoreUiFocus(origin.Snapshot);
    }

    private async Task RunMenuActionAsync(MenuFlyoutItem item, Func<Task> action)
    {
        var previous = _menuActionFocus;
        _menuActionFocus = _menuFocusOrigins.TryGetValue(item, out var origin) ? origin.Snapshot : CaptureUiFocus();
        try { await GuardAsync(action); }
        finally
        {
            var focus = _menuActionFocus;
            _menuActionFocus = previous;
            RestoreUiFocus(focus, Current is null ? "DashboardNavigation" : "ProjectView:" + _view);
        }
    }

    private UiFocusSnapshot? CaptureUiFocus()
    {
        if (_dialogOpen || Root.XamlRoot is null) return null;
        if (_menuActionFocus is not null) return _menuActionFocus;
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is Control control && BelongsToRoot(control))
            {
                var id = AutomationProperties.GetAutomationId(control);
                if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(control.Name))
                    return new(control, id, control.Name,
                        control.FocusState == FocusState.Unfocused ? FocusState.Programmatic : control.FocusState,
                        control is TextBox input ? input.SelectionStart : 0,
                        control is TextBox text ? text.SelectionLength : 0);
            }
            focused = VisualTreeHelper.GetParent(focused);
        }
        return null;
    }

    private bool BelongsToRoot(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current == Root) return true;
        return false;
    }

    private void RestoreUiFocus(UiFocusSnapshot? snapshot, string? fallbackId = null)
    {
        if (snapshot is null && fallbackId is null) return;
        var requestVersion = ++_focusRequestVersion;
        EnqueueLatestFocus(() => Restore(allowRealization: true));

        void Restore(bool allowRealization)
        {
            if (requestVersion != _focusRequestVersion || !_ready || _dialogOpen || Root.XamlRoot is null) return;
            // A loaded ListViewItem may have been recycled for a different task.
            var target = snapshot is not null && snapshot.Control.IsLoaded && BelongsToRoot(snapshot.Control)
                && ((!string.IsNullOrEmpty(snapshot.Id) && AutomationProperties.GetAutomationId(snapshot.Control) == snapshot.Id)
                    || (string.IsNullOrEmpty(snapshot.Id) && snapshot.Control.Name == snapshot.ElementName))
                ? snapshot.Control : FindUiControl(snapshot?.Id, snapshot?.ElementName);
            if (target is null && allowRealization && TryRealizeTaskControl(snapshot?.Id))
            {
                // Only one retry, after the virtualizing panel has produced the row.
                // A later explicit focus request supersedes this restoration.
                EnqueueLatestFocus(() => Restore(allowRealization: false));
                return;
            }
            if (target is null && TaskIdFromControlId(snapshot?.Id) is { } taskId)
                target = FindUiControl("TaskOpen:" + taskId, null);
            target ??= FindUiControl(fallbackId, null);
            if (target is null || !target.IsEnabled || target.Visibility != Visibility.Visible) return;
            // Do not reset a live input's caret on an unrelated refresh.
            if (ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), target)) return;
            if (target.Focus(snapshot?.State ?? FocusState.Keyboard))
            {
                if (target is TextBox input && snapshot is not null)
                {
                    var start = Math.Min(snapshot.SelectionStart, input.Text.Length);
                    input.Select(start, Math.Min(snapshot.SelectionLength, input.Text.Length - start));
                }
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        }
    }

    private static string? TaskIdFromControlId(string? id)
    {
        if (id is null) return null;
        foreach (var prefix in new[] { "TaskOpen:", "TaskMore:", "TaskToggle:", "TaskTimer:", "TaskDelete:" })
            if (id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length) return id[prefix.Length..];
        return null;
    }

    private bool TryRealizeTaskControl(string? id)
    {
        if (TaskIdFromControlId(id) is not { } taskId) return false;
        foreach (var list in ShellDescendants<ListView>(Root))
        {
            // Search only rows in the current view and current filters. A removed
            // or filtered-out task must not change selection, filters or scroll.
            if (list.Items.OfType<TaskRow>().FirstOrDefault(item => item.Id == taskId) is not { } row) continue;
            list.ScrollIntoView(row, ScrollIntoViewAlignment.Default);
            list.UpdateLayout();
            return true;
        }
        return false;
    }

    private Control? FindUiControl(string? id, string? name)
        => ShellDescendants<Control>(Root).FirstOrDefault(control =>
            (!string.IsNullOrEmpty(id) && AutomationProperties.GetAutomationId(control) == id) ||
            (string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name) && control.Name == name));

    private void FocusUiElement(string id, FocusState state = FocusState.Keyboard)
    {
        var requestVersion = ++_focusRequestVersion;
        EnqueueLatestFocus(() => Focus(allowRealization: true));

        void Focus(bool allowRealization)
        {
            if (requestVersion != _focusRequestVersion || !_ready || _dialogOpen || Root.XamlRoot is null) return;
            var control = FindUiControl(id, null);
            if (control is null && allowRealization && TryRealizeTaskControl(id))
            {
                EnqueueLatestFocus(() => Focus(allowRealization: false));
                return;
            }
            if (control is not { IsEnabled: true, Visibility: Visibility.Visible }) return;
            if (control.Focus(state)) control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void SetSaveStatus(string message)
    {
        SaveState.Text = message;
        OperationStatusText.Text = message;
        OperationStatus.Visibility = _busy && message.StartsWith("正在", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SaveState, "保存状态：" + message);
        // The live region has no visible log or tooltip. Errors remain visible
        // through the existing InfoBar and editor validation paths.
        if (FrameworkElementAutomationPeer.FromElement(SaveState) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
