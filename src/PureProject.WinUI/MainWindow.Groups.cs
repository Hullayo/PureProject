using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PureProject.Core;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private async Task CreateTaskGroupAsync(string projectId)
    {
        if (!_ready || _busy || _dialogOpen || !_projects.Any(project => project.Id == projectId)) return;
        var name = new TextBox { Header = "任务组名称", PlaceholderText = "输入任务组名称",
            HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        ConfigureTextInput(name, TextFieldKind.Title, "任务组名称");
        AutomationProperties.SetAutomationId(name, "TaskGroupName");
        AutomationProperties.SetName(name, "任务组名称");
        AutomationProperties.SetIsRequiredForForm(name, true);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed };
        error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
        var form = Column(12);
        form.Width = 326;
        form.Children.Add(error); form.Children.Add(name);
        var dialog = new ContentDialog { Tag = "TaskGroupEditor", Title = "新建任务组", Content = form,
            PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        var validation = new EditorValidation(error);
        string? createdId = null;
        dialog.Opened += (_, _) => name.Focus(FocusState.Keyboard);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            validation.Clear();
            try
            {
                var savedName = TextRules.RequireTitle(name.Text, "任务组名称");
                name.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
                string? nextId = null;
                await ChangeAsync(projectId, project => nextId = _service.CreateTaskGroup(project, savedName).Id, "任务组已创建");
                createdId = nextId;
                _selectedId = projectId; _groupId = createdId; _view = 0;
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                validation.Show(exception.Message, name);
            }
            finally
            {
                name.IsEnabled = true; dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        await DialogAsync(dialog);
        if (createdId is null) return;
        Render();
        FocusUiElement(Root.ActualWidth < 1200 ? "KanbanGroupDrawerToggle" : "KanbanGroup:" + createdId);
    }
}
