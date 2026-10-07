using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureProject.Core;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private async Task RunTextInputLimitChecksAsync(string directory)
    {
        var host = Column(12);
        host.MaxWidth = 600; host.Margin = new Thickness(20);
        ContentHost.Children.Clear();
        ContentHost.Children.Add(new ScrollViewer { Content = host });
        try
        {
            foreach (var kind in new[] { TextFieldKind.Title, TextFieldKind.Status, TextFieldKind.LongText })
            {
                var limit = TextRules.GetLimit(kind);
                var input = new TextBox { Header = kind.ToString(), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
                var feedback = EditorText("", 11, "MutedTextBrush");
                ConfigureTextInput(input, kind, kind.ToString(), feedback);
                host.Children.Add(input); host.Children.Add(feedback);
                await SmokeLayoutAsync();
                input.Text = new string('项', limit);
                SmokeAssert(TextRules.CountGraphemes(input.Text) == limit, $"{kind}: exact boundary was rejected.");
                var accepted = input.Text;
                input.Text = accepted + "超";
                SmokeAssert(input.Text == accepted && feedback.Text.Contains("未输入"), $"{kind}: overflow changed native text or had no feedback.");
                input.Select(input.Text.Length - 1, 1); input.SelectedText = "换";
                SmokeAssert(input.Text.EndsWith("换") && TextRules.CountGraphemes(input.Text) == limit, $"{kind}: replacing a selection at the boundary failed.");
                var unicode = string.Concat(Enumerable.Repeat("𠀀👩‍💻e\u0301", limit / 3)) + new string('字', limit % 3);
                input.Text = unicode;
                SmokeAssert(input.Text == unicode && TextRules.CountGraphemes(input.Text) == limit, $"{kind}: Unicode was counted as UTF-16 units.");
                input.SelectAll(); input.SelectedText = new string('粘', limit + 1);
                SmokeAssert(input.Text == unicode, $"{kind}: whole replacement silently truncated or accepted overflow.");
            }

            var original = new string('旧', 80);
            var legacy = new TextBox { Text = original };
            ConfigureTextInput(legacy, TextFieldKind.Title, "旧标题", original: original);
            host.Children.Add(legacy); await SmokeLayoutAsync();
            SmokeAssert(legacy.Text == original, "Loading legacy title truncated it.");
            legacy.Text = original + "增";
            SmokeAssert(legacy.Text == original, "Legacy title accepted growth above the limit.");
            legacy.Text = original[..70];
            SmokeAssert(legacy.Text.Length == 70, "Legacy title could not be shortened incrementally.");
            legacy.Text = original;
            SmokeAssert(legacy.Text == original, "Legacy title could not restore its exact original value.");

            var body = new LongTextEditor(this, "说明", "", 72);
            var subtask = new LongTextEditor(this, "子任务", "", 32, compact: true, kind: TextFieldKind.Title);
            host.Children.Add(body.Panel); host.Children.Add(subtask.Panel); await SmokeLayoutAsync();
            foreach (var (editor, limit) in new[] { (body, 1024), (subtask, 64) })
            {
                var text = new string('文', limit - 2) + "\r\n末";
                editor.PasteText(0, 0, text);
                SmokeAssert(editor.Value == text, $"Paged {limit}: paste did not preserve the entire original text/newlines.");
                var failed = false;
                try { editor.PasteText(editor.Input.Text.Length, 0, "超"); }
                catch (ArgumentException) { failed = true; }
                SmokeAssert(failed && editor.Value == text, $"Paged {limit}: overflow paste was not atomic.");
                editor.Input.Text += "超";
                SmokeAssert(editor.Value == text, $"Paged {limit}: native typing bypassed the backing-buffer limit.");
            }
            await SmokeCaptureAsync(directory, "text-input-limits.png");
        }
        finally { Render(); }
    }
}
