using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private readonly List<object> _consistencyMeasurements = [];
    private readonly List<string> _consistencyThemeFindings = [];
    private static bool ConsistencyStrict => Environment.GetEnvironmentVariable("PUREPROJECT_UI_CONSISTENCY_STRICT") == "1";

    // This opt-in extension uses the existing isolated smoke fixtures. Baseline mode
    // records visual differences; native behavior and data-isolation checks always run.
    private async Task SmokeConsistencyTaskControlsAsync(string directory, string theme, string projectId,
        string groupId, List<UiSmokeScreenshot> screenshots)
    {
        var firstFinding = _consistencyThemeFindings.Count;
        var repository = _repository ?? throw new InvalidOperationException("The consistency test repository is unavailable.");
        var before = await File.ReadAllTextAsync(repository.DataFilePath);
        var undoCount = _undo.Count; var redoCount = _redo.Count;
        _selectedId = projectId; _groupId = groupId; _view = 0; Render(); await SmokeLayoutAsync();
        var task = _projects.Single(project => project.Id == projectId).Tasks.Single(item => item.Title == "实现 WinUI 3 原生任务编辑器");
        var editing = EditTaskAsync(task.Id, projectId);
        var dialog = await SmokeWaitForDialogAsync(editing);
        var date = SmokeFind<CalendarDatePicker>(dialog, field => Equals(field.Header, "截止日期"));
        try
        {
            await ConsistencyRevealAsync(dialog, date);
            var originalDate = date.Date;
            new CalendarDatePickerAutomationPeer(date).Invoke();
            await SmokeWaitAsync(() => date.IsCalendarOpen && ConsistencyPopupElements<CalendarView>().Any(view => view.ActualHeight > 0),
                "The real task date picker did not open its native calendar.");
            var calendar = ConsistencyPopupElements<CalendarView>().First(view => view.ActualHeight > 0);
            await SmokeLayoutAsync();
            ConsistencyBrushRule(theme, "calendar", "background", calendar.Background, "CardBackgroundBrush");
            ConsistencyBrushRule(theme, "calendar", "foreground", calendar.Foreground, "PrimaryTextBrush");
            ConsistencyBrushRule(theme, "calendar", "border", calendar.BorderBrush, "BorderStrongBrush");
            var calendarFonts = new Dictionary<string, string>
            {
                ["control"] = calendar.FontFamily.Source, ["day"] = calendar.DayItemFontFamily.Source,
                ["monthYear"] = calendar.MonthYearItemFontFamily.Source,
                ["firstOfMonth"] = calendar.FirstOfMonthLabelFontFamily.Source,
                ["firstOfYearDecade"] = calendar.FirstOfYearDecadeLabelFontFamily.Source
            };
            foreach (var font in calendarFonts) ConsistencyThemeRule(font.Value == AppFontSource, theme, "calendar", font.Key + " font must use the bundled application font.");
            await ConsistencyCaptureAsync(directory, theme, "calendar-popup", calendar, screenshots, new
            {
                fonts = calendarFonts, background = ConsistencyBrush(calendar.Background), foreground = ConsistencyBrush(calendar.Foreground),
                border = ConsistencyBrush(calendar.BorderBrush), calendarItemForeground = ConsistencyBrush(calendar.CalendarItemForeground),
                selectedForeground = ConsistencyBrush(calendar.SelectedForeground), selectedBorder = ConsistencyBrush(calendar.SelectedBorderBrush),
                todayBackground = ConsistencyBrush(calendar.TodayBackground), todayForeground = ConsistencyBrush(calendar.TodayForeground),
                disabledForeground = ConsistencyBrush(calendar.DisabledForeground), outOfScopeForeground = ConsistencyBrush(calendar.OutOfScopeForeground)
            });
            var day = SmokeDescendants<CalendarViewDayItem>(calendar).FirstOrDefault(item => item.IsEnabled && !item.IsBlackout
                && item.ActualWidth > 0 && item.ActualHeight > 0 && SmokeRenderedVisible(item, calendar)
                && item.Date.Month == (originalDate ?? DateTimeOffset.Now).Month && item.Date.Date != originalDate?.Date
                && ConsistencyFullyWithin(item, calendar));
            SmokeAssert(day is not null, "The calendar has no different visible selectable day.");
            var chosenDate = day!.Date.Date;
            var dayPeer = FrameworkElementAutomationPeer.CreatePeerForElement(day);
            if (dayPeer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection) selection.Select();
            else if (dayPeer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
            else throw new InvalidOperationException("The native calendar day exposes no selection or invoke provider.");
            await SmokeWaitAsync(() => date.Date?.Date == chosenDate, "Selecting the native calendar day did not update the task date field.");
            date.IsCalendarOpen = false;
            await SmokeWaitAsync(() => !date.IsCalendarOpen, "The native calendar did not close after selection.");

            var number = SmokeFind<NumberBox>(dialog, field => Equals(field.Header, "开始偏移（自项目创建日起，天）"));
            await ConsistencyNumberSpinAsync(directory, theme, dialog, number, "number-spin", screenshots, false);

            var check = SmokeFind<CheckBox>(dialog, field => Equals(field.Content, "完成后创建下一次任务"));
            await ConsistencyRevealAsync(dialog, check);
            SmokeAssert(check.IsChecked == false, "The consistency recurrence fixture must begin unchecked.");
            AutomationPeer CurrentTogglePeer() => FrameworkElementAutomationPeer.FromElement(check)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(check)
                ?? throw new InvalidOperationException("The recurrence checkbox lost its registered native peer.");
            void ToggleCurrentCheck()
            {
                // Native captures/focus transitions can invalidate detached providers.
                // Acquire the registered peer at action time, rather than retaining a
                // separately constructed peer across asynchronous screenshot work.
                if (CurrentTogglePeer().GetPattern(PatternInterface.Toggle) is not IToggleProvider provider)
                    throw new InvalidOperationException("The recurrence checkbox lost its native toggle provider.");
                provider.Toggle();
            }
            SmokeAssert(check.Focus(FocusState.Keyboard), "The recurrence checkbox cannot receive focus.");
            await ConsistencyCaptureCheckboxAsync(directory, theme, "checkbox-unchecked", check, screenshots);
            ToggleCurrentCheck();
            await SmokeWaitAsync(() => check.IsChecked == true, "The recurrence checkbox did not toggle on.");
            await ConsistencyRevealAsync(dialog, check);
            await ConsistencyCaptureCheckboxAsync(directory, theme, "checkbox-checked", check, screenshots);
            var interval = SmokeFind<NumberBox>(dialog, field => Equals(field.Header, "间隔"));
            await ConsistencyNumberSpinAsync(directory, theme, dialog, interval, "number-spin-minimum", screenshots, true);
            await ConsistencyRevealAsync(dialog, check);
            SmokeHeaderCloseButton(dialog).Focus(FocusState.Keyboard);
            check.IsEnabled = false;
            try
            {
                SmokeAssert(!check.Focus(FocusState.Keyboard) && !CurrentTogglePeer().IsEnabled(), "A disabled recurrence checkbox accepts focus or reports enabled.");
                await ConsistencyCaptureCheckboxAsync(directory, theme, "checkbox-disabled", check, screenshots);
            }
            finally { check.IsEnabled = true; }
            ToggleCurrentCheck();
            await SmokeWaitAsync(() => check.IsChecked == false, "The recurrence checkbox did not toggle off.");
        }
        finally
        {
            date.IsCalendarOpen = false;
            if (ReferenceEquals(_currentActiveDialog, dialog)) SmokeInvoke(SmokeHeaderCloseButton(dialog));
            await SmokeAwaitDialogAsync(editing, dialog);
            await ConsistencyWriteMeasurementsAsync(directory);
        }
        SmokeAssert(await File.ReadAllTextAsync(repository.DataFilePath) == before && _undo.Count == undoCount && _redo.Count == redoCount,
            "Inspecting and cancelling task control states changed persisted data or history.");
        ConsistencyAssertStrict(firstFinding);
    }

    private async Task ConsistencyNumberSpinAsync(string directory, string theme, ContentDialog dialog, NumberBox number,
        string surface, List<UiSmokeScreenshot> screenshots, bool minimumBoundary)
    {
        await ConsistencyRevealAsync(dialog, number);
        var original = number.Value;
        if (minimumBoundary) number.Value = number.Minimum;
        else if (double.IsNaN(number.Value)) number.Value = 2;
        var initial = number.Value;
        var input = SmokeFind<TextBox>(number, field => field.Name == "InputBox");
        SmokeAssert(input.Focus(FocusState.Keyboard), "The real NumberBox input cannot receive focus.");
        // Resolve the popup in this NumberBox's template namescope. Other compact
        // NumberBoxes may still have popup visuals during their closing transition.
        var templateRoot = VisualTreeHelper.GetChild(number, 0) as FrameworkElement
            ?? throw new InvalidOperationException("The NumberBox has no native template root.");
        var ownedPopup = templateRoot.FindName("UpDownPopup") as Popup
            ?? SmokeDescendants<Popup>(number).FirstOrDefault(part => part.Name == "UpDownPopup")
            ?? throw new InvalidOperationException("The NumberBox template has no owned spin popup.");
        await SmokeWaitAsync(() => ownedPopup.IsOpen && ownedPopup.Child is Grid { ActualHeight: > 0 },
            "Focusing the compact NumberBox did not open its native spin popup.");
        var popup = (Grid)ownedPopup.Child;
        var up = SmokeFind<RepeatButton>(popup, button => button.Name == "PopupUpSpinButton");
        var down = SmokeFind<RepeatButton>(popup, button => button.Name == "PopupDownSpinButton");
        await SmokeLayoutAsync();
        var downPeer = FrameworkElementAutomationPeer.CreatePeerForElement(down)
            ?? throw new InvalidOperationException("The native NumberBox decrement has no associated automation peer.");
        if (downPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider downInvoke)
            throw new InvalidOperationException("The native NumberBox decrement peer has no invoke provider.");
        ConsistencyBrushRule(theme, surface, "background", popup.Background, "CardBackgroundBrush");
        ConsistencyBrushRule(theme, surface, "border", popup.BorderBrush, "BorderStrongBrush");
        ConsistencyBrushRule(theme, surface, "upForeground", up.Foreground, "SecondaryTextBrush");
        if (minimumBoundary)
        {
            var downPresenter = SmokeFind<ContentPresenter>(down, element => element.Name == "ContentPresenter");
            ConsistencyBrushRule(theme, surface, "disabledForeground", downPresenter.Foreground, "MutedTextBrush");
        }
        await ConsistencyCaptureAsync(directory, theme, surface, popup, screenshots, new
        {
            background = ConsistencyBrush(popup.Background), border = ConsistencyBrush(popup.BorderBrush),
            up = ConsistencyButtonVisual(up), down = ConsistencyButtonVisual(down), minimumBoundary,
            beforeInvoke = new
            {
                controlIsEnabled = down.IsEnabled, peerIsEnabled = downPeer.IsEnabled(), peerType = downPeer.GetType().Name,
                popupIsOpen = ownedPopup.IsOpen, popupIsLoaded = ownedPopup.IsLoaded,
                popupContentIsLoaded = popup.IsLoaded, buttonIsLoaded = down.IsLoaded
            },
            inputFont = input.FontFamily.Source, step = number.SmallChange, value = number.Value,
            minimum = double.IsFinite(number.Minimum) ? (double?)number.Minimum : null,
            maximum = double.IsFinite(number.Maximum) ? (double?)number.Maximum : null, isWrapEnabled = number.IsWrapEnabled,
            popupOwnedByInspectedNumberBox = ReferenceEquals(ownedPopup.Child, popup),
            visualStates = VisualStateManager.GetVisualStateGroups(templateRoot)
                .Select(group => new { group = group.Name, state = group.CurrentState?.Name }).ToArray()
        });
        if (minimumBoundary)
        {
            SmokeAssert(Math.Abs(number.Value - number.Minimum) < .0001 && !number.IsWrapEnabled,
                "The minimum-boundary NumberBox fixture is not at its non-wrapping minimum.");
            var disabledState = VisualStateManager.GetVisualStateGroups(templateRoot).Any(group =>
                group.Name == "DownSpinButtonEnabledStates" && group.CurrentState?.Name == "DownSpinButtonDisabled");
            // This SDK's composite RepeatButton peer can report enabled while its
            // loaded control and native VSM are disabled. Preserve the raw report,
            // but judge the real control state and the bounded invocation below.
            ConsistencyThemeRule(!down.IsEnabled && disabledState, theme, surface,
                "The owned NumberBox decrement must be disabled in its control and native visual state at the minimum.");
            try { downInvoke.Invoke(); }
            catch (Exception error) when (unchecked((uint)error.HResult) == 0x80040200) { /* UIA_E_ELEMENTNOTENABLED */ }
            await SmokeLayoutAsync();
            SmokeAssert(number.Value == initial, "The native NumberBox decrement moved below its minimum.");
        }
        SmokeAssert(up.IsEnabled, "The real NumberBox increment action is unexpectedly disabled.");
        new RepeatButtonAutomationPeer(up).Invoke();
        await SmokeWaitAsync(() => Math.Abs(number.Value - (initial + number.SmallChange)) < .0001,
            "The native NumberBox increment did not change the draft by one step.");
        SmokeAssert(down.IsEnabled, "NumberBox decrement did not re-enable above its minimum.");
        downInvoke.Invoke();
        await SmokeWaitAsync(() => Math.Abs(number.Value - initial) < .0001, "The native NumberBox decrement did not restore the draft value.");
        number.Value = original;
        SmokeHeaderCloseButton(dialog).Focus(FocusState.Keyboard);
        ownedPopup.IsOpen = false;
        await SmokeLayoutAsync();
    }

    private async Task ConsistencyCaptureCheckboxAsync(string directory, string theme, string surface, CheckBox check,
        List<UiSmokeScreenshot> screenshots)
    {
        await SmokeLayoutAsync();
        var box = SmokeFind<Microsoft.UI.Xaml.Shapes.Rectangle>(check, part => part.Name == "NormalRectangle");
        var content = SmokeFind<ContentPresenter>(check, part => part.Name == "ContentPresenter");
        ConsistencyThemeRule(check.FontFamily.Source == AppFontSource, theme, surface, "Checkbox font must use the bundled application font.");
        if (check.IsEnabled && check.IsChecked == true) ConsistencyBrushRule(theme, surface, "checkedFill", box.Fill, "AccentBrush");
        if (check.IsEnabled && check.IsChecked == false) ConsistencyBrushRule(theme, surface, "uncheckedStroke", box.Stroke, "BorderStrongBrush");
        ConsistencyBrushRule(theme, surface, "contentForeground", content.Foreground, check.IsEnabled ? "PrimaryTextBrush" : "MutedTextBrush");
        var currentThemeKey = check.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        var currentThemeResources = check.Resources.ThemeDictionaries.TryGetValue(currentThemeKey, out var themeDictionary)
            ? themeDictionary as ResourceDictionary : null;
        check.Resources.TryGetValue("CheckBoxCheckBackgroundFillChecked", out var localCheckedFill);
        object? themeCheckedFill = null;
        currentThemeResources?.TryGetValue("CheckBoxCheckBackgroundFillChecked", out themeCheckedFill);
        await ConsistencyCaptureAsync(directory, theme, surface, check, screenshots, new
        {
            isChecked = check.IsChecked, isEnabled = check.IsEnabled, fill = ConsistencyBrush(box.Fill), stroke = ConsistencyBrush(box.Stroke),
            foreground = ConsistencyBrush(content.Foreground), font = check.FontFamily.Source, strokeThickness = box.StrokeThickness,
            resourceCount = check.Resources.Count, themeDictionaryCount = check.Resources.ThemeDictionaries.Count,
            currentThemeKey, currentThemeResourceCount = currentThemeResources?.Count,
            localCheckedFill = ConsistencyBrush(localCheckedFill as Brush), currentThemeCheckedFill = ConsistencyBrush(themeCheckedFill as Brush)
        });
    }

    private async Task SmokeConsistencyMenusAsync(string directory, string theme, string projectId, string groupId,
        List<UiSmokeScreenshot> screenshots)
    {
        var firstFinding = _consistencyThemeFindings.Count;
        var repository = _repository ?? throw new InvalidOperationException("The consistency test repository is unavailable.");
        var before = await File.ReadAllTextAsync(repository.DataFilePath);
        var undoCount = _undo.Count; var redoCount = _redo.Count;
        _selectedId = projectId; _groupId = groupId; _view = 0; Render(); await SmokeLayoutAsync();
        var project = _projects.Single(item => item.Id == projectId);
        var task = project.Tasks.Single(item => item.Title == "实现 WinUI 3 原生任务编辑器");
        var destination = project.TaskGroups.Single(item => item.Id != groupId);
        var (menu, leaf) = await SmokeTaskMenuItemAsync(task.Id, $"move:{destination.Id}:{destination.InitialStatusId}");
        try
        {
            int MenuDepth(MenuFlyoutPresenter presenter)
            {
                var submenuIds = SmokeDescendants<MenuFlyoutSubItem>(presenter).Select(AutomationProperties.GetAutomationId).ToArray();
                if (submenuIds.Contains("TaskMenu:move:" + task.Id)) return 1;
                if (submenuIds.Contains($"TaskMenu:move:{destination.Id}:{task.Id}")) return 2;
                return SmokeDescendants<MenuFlyoutItem>(presenter).Any(item => ReferenceEquals(item, leaf)) ? 3 : 4;
            }
            var presenters = ConsistencyPopupElements<MenuFlyoutPresenter>().Where(item => item.ActualHeight > 0)
                .Distinct().OrderBy(MenuDepth).ToArray();
            SmokeAssert(presenters.Length >= 3, "Opening the move-to-group action did not show all three native menu levels.");
            SmokeAssert(leaf.IsEnabled, "The nested move destination is unexpectedly disabled.");
            for (var index = 0; index < presenters.Length; index++)
            {
                var presenter = presenters[index]; var surface = "menu-level-" + MenuDepth(presenter);
                ConsistencyBrushRule(theme, surface, "background", presenter.Background, "CardBackgroundBrush");
                ConsistencyBrushRule(theme, surface, "border", presenter.BorderBrush, "BorderStrongBrush");
                var labels = ConsistencyTextStyles(presenter);
                ConsistencyThemeRule(labels.Length > 0 && labels.All(label => label.Font == AppFontSource), theme, surface, "Menu text must use the bundled application font.");
                await ConsistencyCaptureAsync(directory, theme, surface, presenter, screenshots, new
                {
                    background = ConsistencyBrush(presenter.Background), foreground = ConsistencyBrush(presenter.Foreground),
                    border = ConsistencyBrush(presenter.BorderBrush), borderThickness = ConsistencyThickness(presenter.BorderThickness),
                    cornerRadius = ConsistencyCorners(presenter.CornerRadius), textStyles = labels
                });
            }
        }
        finally { menu.Hide(); await SmokeLayoutAsync(); }

        var anchor = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "TaskMore:" + task.Id);
        var originalTip = ToolTipService.GetToolTip(anchor);
        SmokeAssert(originalTip is string or ToolTip, "The real task-more action has no tooltip to inspect.");
        var tip = originalTip as ToolTip ?? new ToolTip { Content = originalTip };
        var originalPlacement = tip.Placement; var originalTarget = tip.PlacementTarget;
        try
        {
            ToolTipService.SetToolTip(anchor, tip);
            tip.PlacementTarget = anchor; tip.Placement = PlacementMode.Bottom;
            tip.XamlRoot = Root.XamlRoot; tip.IsOpen = true;
            Popup? tooltipPopup = null;
            ContentPresenter? tooltipContent = null;
            await SmokeWaitAsync(() =>
            {
                tooltipPopup = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).FirstOrDefault(popup => popup.IsOpen
                    && popup.Child is not null && SmokeDescendants<ToolTip>(popup.Child).Any(item => ReferenceEquals(item, tip)));
                tooltipContent = SmokeDescendants<ContentPresenter>(tip).FirstOrDefault(part => part.Name == "LayoutRoot");
                return tip.IsOpen && tip.IsLoaded && tooltipPopup is not null
                    && tooltipContent is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible };
            }, "The existing task-more tooltip did not load its native popup and template content.");
            var popupHost = tooltipPopup ?? throw new InvalidOperationException("The tooltip popup disappeared after loading.");
            var renderedTip = tooltipContent ?? throw new InvalidOperationException("The tooltip template content disappeared after loading.");
            await SmokeLayoutAsync();
            renderedTip.UpdateLayout();
            SmokeAssert(tip.IsOpen && popupHost.IsOpen && renderedTip.IsLoaded,
                "The tooltip closed before its native template content was ready to capture.");
            ConsistencyBrushRule(theme, "tooltip", "background", tip.Background, "CardBackgroundBrush");
            ConsistencyBrushRule(theme, "tooltip", "foreground", tip.Foreground, "PrimaryTextBrush");
            ConsistencyBrushRule(theme, "tooltip", "border", tip.BorderBrush, "BorderStrongBrush");
            var labels = ConsistencyTextStyles(renderedTip);
            ConsistencyThemeRule(labels.Length > 0 && labels.All(label => label.Font == AppFontSource), theme, "tooltip", "Tooltip text must use the bundled application font.");
            // ToolTip itself is a popup owner and may return an empty RTB. Capture
            // its real rendered presenter, preserving the popup's original pixels.
            await ConsistencyCaptureAsync(directory, theme, "tooltip", renderedTip, screenshots, new
            {
                openingMethod = "Programmatic ToolTip.IsOpen on the existing task-more anchor; physical hover is not exercised",
                popupChildType = popupHost.Child?.GetType().Name, tooltipLoaded = tip.IsLoaded,
                capturePart = renderedTip.Name, popupOpen = popupHost.IsOpen,
                background = ConsistencyBrush(tip.Background), foreground = ConsistencyBrush(tip.Foreground), border = ConsistencyBrush(tip.BorderBrush),
                borderThickness = ConsistencyThickness(tip.BorderThickness), textStyles = labels
            });
        }
        finally
        {
            tip.IsOpen = false; tip.Placement = originalPlacement; tip.PlacementTarget = originalTarget;
            ToolTipService.SetToolTip(anchor, originalTip);
            await SmokeLayoutAsync();
            await ConsistencyWriteMeasurementsAsync(directory);
        }
        SmokeAssert(await File.ReadAllTextAsync(repository.DataFilePath) == before && _undo.Count == undoCount && _redo.Count == redoCount,
            "Opening nested menus or a tooltip changed persisted data or history.");
        ConsistencyAssertStrict(firstFinding);
    }

    private async Task SmokeConsistencyNoticesAsync(string directory, string theme, List<UiSmokeScreenshot> screenshots)
    {
        var firstFinding = _consistencyThemeFindings.Count;
        var previousOpen = Notice.IsOpen; var previousMessage = Notice.Message; var previousTitle = Notice.Title;
        var previousSeverity = Notice.Severity;
        try
        {
            foreach (var severity in new[] { InfoBarSeverity.Informational, InfoBarSeverity.Success, InfoBarSeverity.Warning, InfoBarSeverity.Error })
            {
                Notice.Title = "界面一致性检查";
                ShowMessage("这是隔离验收提示，可通过右侧按钮关闭。", severity);
                await SmokeWaitAsync(() => Notice.IsOpen && Notice.ActualHeight > 0, "The notification did not render.");
                await SmokeLayoutAsync();
                var surface = "notice-" + severity.ToString().ToLowerInvariant();
                var body = SmokeFind<Border>(Notice, part => part.Name == "ContentRoot");
                var message = SmokeFind<TextBlock>(Notice, part => part.Name == "Message");
                var icon = SmokeFind<TextBlock>(Notice, part => part.Name == "StandardIcon");
                var iconBackground = SmokeFind<TextBlock>(Notice, part => part.Name == "IconBackground");
                ConsistencyBrushRule(theme, surface, "messageForeground", message.Foreground, "PrimaryTextBrush");
                ConsistencyThemeRule(message.FontFamily.Source == AppFontSource, theme, surface, "Notification text must use the bundled application font.");
                var contrast = ConsistencyContrast(message.Foreground, body.Background);
                ConsistencyThemeRule(contrast is >= 4.5, theme, surface, "Notification message contrast must be at least 4.5:1.");
                var close = SmokeFind<Button>(Notice, part => part.Name == "CloseButton");
                SmokeAssert(close.IsEnabled && close.Focus(FocusState.Keyboard), "The native notification close action cannot receive focus.");
                await ConsistencyCaptureAsync(directory, theme, surface, Notice, screenshots, new
                {
                    severity = severity.ToString(), background = ConsistencyBrush(body.Background), border = ConsistencyBrush(body.BorderBrush),
                    foreground = ConsistencyBrush(message.Foreground), messageFont = message.FontFamily.Source,
                    iconForeground = ConsistencyBrush(icon.Foreground), iconBackground = ConsistencyBrush(iconBackground.Foreground),
                    messageContrast = contrast, close = ConsistencyButtonVisual(close)
                });
                SmokeInvoke(close);
                await SmokeWaitAsync(() => !Notice.IsOpen, "The native notification close action did not dismiss the bar.");
            }
        }
        finally
        {
            Notice.IsOpen = false; Notice.Message = previousMessage; Notice.Title = previousTitle; Notice.Severity = previousSeverity; Notice.IsOpen = previousOpen;
            await SmokeLayoutAsync();
            await ConsistencyWriteMeasurementsAsync(directory);
        }
        ConsistencyAssertStrict(firstFinding);
    }

    private async Task ConsistencyRevealAsync(ContentDialog dialog, Control field)
    {
        field.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
        SmokeAssert(field.Focus(FocusState.Keyboard), "The consistency control cannot receive focus.");
        await SmokeLayoutAsync();
        SmokeEditorFieldVisible(dialog, field, "Consistency control");
    }

    private IEnumerable<T> ConsistencyPopupElements<T>() where T : DependencyObject =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Where(popup => popup.IsOpen && popup.Child is not null)
            .SelectMany(popup => SmokeDescendants<T>(popup.Child));

    private static bool ConsistencyFullyWithin(FrameworkElement child, FrameworkElement parent)
    {
        var bounds = SmokeBounds(child, parent);
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= parent.ActualWidth && bounds.Bottom <= parent.ActualHeight;
    }

    private async Task ConsistencyCaptureAsync(string directory, string theme, string surface, FrameworkElement target,
        List<UiSmokeScreenshot> screenshots, object details)
    {
        var shot = await SmokeCaptureAsync(directory, $"consistency-{surface}-{theme.ToLowerInvariant()}.png", target);
        screenshots.Add(shot);
        _consistencyMeasurements.Add(new
        {
            theme, surface, controlType = target.GetType().Name, actualTheme = target.ActualTheme.ToString(),
            widthDip = target.ActualWidth, heightDip = target.ActualHeight, focusWithin = SmokeContainsFocus(target),
            focusState = target is Control control ? control.FocusState.ToString() : null,
            isEnabled = target is Control enabledControl ? (bool?)enabledControl.IsEnabled : null,
            screenshot = Path.GetFileName(shot.Path), screenshotWidth = shot.Width, screenshotHeight = shot.Height,
            composition = shot.Composition, details
        });
        await ConsistencyWriteMeasurementsAsync(directory);
    }

    private Task ConsistencyWriteMeasurementsAsync(string directory) => File.WriteAllTextAsync(
        Path.Combine(directory, "consistency-measurements.json"), JsonSerializer.Serialize(new
        {
            strict = ConsistencyStrict,
            interaction = "In-process native automation peers, focus, and programmatic tooltip opening; no physical mouse or keyboard claim",
            capturedAt = DateTimeOffset.UtcNow, observations = _consistencyMeasurements, themeFindings = _consistencyThemeFindings
        }, new JsonSerializerOptions { WriteIndented = true }));

    private void ConsistencyThemeRule(bool condition, string theme, string surface, string expectation)
    {
        if (!condition) _consistencyThemeFindings.Add(theme + " / " + surface + ": " + expectation);
    }

    private void ConsistencyBrushRule(string theme, string surface, string property, Brush? actual, string expectedKey) =>
        ConsistencyThemeRule(ConsistencyBrush(actual) == ConsistencyBrush(ThemeBrush(expectedKey)), theme, surface,
            property + " must match " + expectedKey + "; actual " + ConsistencyBrush(actual) + ".");

    private void ConsistencyAssertStrict(int firstFinding)
    {
        if (ConsistencyStrict) SmokeAssert(_consistencyThemeFindings.Count == firstFinding,
            "Strict consistency checks failed: " + string.Join(" | ", _consistencyThemeFindings.Skip(firstFinding)));
    }

    private static string ConsistencyBrush(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "null", _ => brush.GetType().Name
    };
    private static double[] ConsistencyThickness(Thickness value) => [value.Left, value.Top, value.Right, value.Bottom];
    private static double[] ConsistencyCorners(CornerRadius value) => [value.TopLeft, value.TopRight, value.BottomRight, value.BottomLeft];
    private sealed record ConsistencyTextStyle(string Font, double Size, string Foreground);
    private static ConsistencyTextStyle[] ConsistencyTextStyles(DependencyObject root) => SmokeDescendants<TextBlock>(root)
        .Where(label => label.ActualWidth > 0 && label.ActualHeight > 0 && SmokeRenderedVisible(label, root)
            && label.Text.Any(character => character is >= '\u4E00' and <= '\u9FFF'))
        .Select(label => new ConsistencyTextStyle(label.FontFamily.Source, label.FontSize, ConsistencyBrush(label.Foreground))).Distinct().ToArray();

    private static object ConsistencyButtonVisual(Control button)
    {
        var presenter = SmokeDescendants<ContentPresenter>(button).FirstOrDefault(part => part.Name == "ContentPresenter");
        return new
        {
            isEnabled = button.IsEnabled, focus = button.FocusState.ToString(), widthDip = button.ActualWidth, heightDip = button.ActualHeight,
            foreground = ConsistencyBrush(button.Foreground), background = ConsistencyBrush(button.Background), border = ConsistencyBrush(button.BorderBrush),
            renderedForeground = ConsistencyBrush(presenter?.Foreground), renderedBackground = ConsistencyBrush(presenter?.Background),
            renderedBorder = ConsistencyBrush(presenter?.BorderBrush), font = button.FontFamily.Source
        };
    }

    private double? ConsistencyContrast(Brush foreground, Brush background)
    {
        if (foreground is not SolidColorBrush fg || background is not SolidColorBrush bg || ThemeBrush("CardBackgroundBrush") is not SolidColorBrush paper) return null;
        static double[] Blend(Windows.UI.Color top, double[] below)
        {
            var alpha = top.A / 255d;
            return new[] { top.R / 255d, top.G / 255d, top.B / 255d }.Select((channel, index) => channel * alpha + below[index] * (1 - alpha)).ToArray();
        }
        static double Luminance(double[] color) => color.Select(channel => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4))
            .Zip(new[] { .2126, .7152, .0722 }, (channel, weight) => channel * weight).Sum();
        var backdrop = Blend(bg.Color, [paper.Color.R / 255d, paper.Color.G / 255d, paper.Color.B / 255d]);
        var ink = Blend(fg.Color, backdrop); var a = Luminance(ink); var b = Luminance(backdrop);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
