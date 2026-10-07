using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PureProject.Core;
using Windows.ApplicationModel.DataTransfer;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private static Func<string> PreserveInitialText(TextBox input, string? original)
    {
        if (original is null) return () => input.Text;
        var displayed = input.Text; var captured = false;
        input.Loaded += (_, _) => { if (!captured) { displayed = input.Text; captured = true; } };
        static string NativeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        // Native multiline controls can normalize line endings during loading or
        // focus. If the displayed value is unchanged, keep the original string.
        return () => string.Equals(NativeLines(input.Text), NativeLines(displayed), StringComparison.Ordinal) ? original : input.Text;
    }

    private static string EditorPreview(string text, int limit = 360)
    {
        if (text.Length <= limit) return text;
        if (char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit] + "…";
    }

    // Each native TextBox receives only one bounded page. The buffer retains every
    // other page, including original newline spellings, even when the editor is hidden.
    private sealed class LongTextEditor
    {
        private PagedTextBuffer _buffer;
        private readonly TextBlock _position;
        private readonly TextBlock _feedback;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly StackPanel _actions;
        private readonly bool _compact;
        private readonly double _minimumHeight;
        private readonly TextFieldKind _kind;
        private readonly string _header;
        private int _page;
        private string _displayed = "";
        private int? _editSelectionStart;
        private int _editSelectionLength;
        private Windows.System.VirtualKey? _pendingDeleteKey;
        private bool _loading;
        private bool _composing;
        public StackPanel Panel { get; }
        public TextBox Input { get; }
        public string Value => _buffer.Value;
        public event Action? Changed;

        public void Clear()
        { _buffer = new PagedTextBuffer("", inputKind: _kind); _page = 0; LoadPage(); Changed?.Invoke(); }

        public LongTextEditor(MainWindow owner, string header, string value, double minimumHeight = 96, bool compact = false, TextFieldKind kind = TextFieldKind.LongText)
        {
            _compact = compact;
            _minimumHeight = minimumHeight;
            _kind = kind; _header = header;
            _buffer = new PagedTextBuffer(value, inputKind: kind);
            Input = new TextBox { Header = compact ? null : header, AcceptsReturn = !compact, TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
                MinHeight = minimumHeight, MaxHeight = Math.Max(minimumHeight, 176), MaxLength = 0 };
            TrackTextComposition(Input);
            ScrollViewer.SetVerticalScrollBarVisibility(Input, ScrollBarVisibility.Auto);
            Panel = Column(5); Panel.Children.Add(Input);
            _position = EditorText("", 11, "MutedTextBrush");
            _feedback = EditorText("", 11, "DangerTextBrush"); _feedback.Visibility = Visibility.Collapsed;
            _previous = owner.EditorButton("上一段", () => { ShowPage(_page - 1); return Task.CompletedTask; });
            _next = owner.EditorButton("下一段", () => { ShowPage(_page + 1); return Task.CompletedTask; });
            var append = owner.EditorButton("插入新段", () => { _buffer.InsertPage(_page + 1); ShowPage(_page + 1); return Task.CompletedTask; });
            foreach (var button in new[] { _previous, _next, append })
            { button.FontSize = 11; button.MinHeight = 28; button.MinWidth = 0; button.Padding = new Thickness(7, 3, 7, 3); }
            _actions = Row(4); _actions.Children.Add(_previous); _actions.Children.Add(_next); _actions.Children.Add(append);
            Panel.Children.Add(_position); Panel.Children.Add(_actions); Panel.Children.Add(_feedback);
            AutomationProperties.SetName(_previous, header + "上一段"); AutomationProperties.SetName(_next, header + "下一段");
            AutomationProperties.SetName(append, header + "插入新段");
            AutomationProperties.SetHelpText(Input, $"{header}最多 {TextRules.GetLimit(kind)} 个字符。已有长内容保留原文，修改后需符合上限。");
            Input.KeyDown += (_, args) => _pendingDeleteKey = args.Key is Windows.System.VirtualKey.Back or Windows.System.VirtualKey.Delete ? args.Key : null;
            Input.BeforeTextChanging += (_, args) =>
            {
                if (_loading || _composing) return;
                _editSelectionStart = Input.SelectionStart; _editSelectionLength = Input.SelectionLength;
                // A collapsed selection alone cannot distinguish which adjacent
                // mixed newline Backspace/Delete removes. Capture that range;
                // the buffer still verifies it against the actual native change.
                if (_editSelectionLength == 0 && _pendingDeleteKey is { } key)
                {
                    var native = Input.Text; var caret = Input.SelectionStart;
                    if (key == Windows.System.VirtualKey.Back && caret > 0)
                    {
                        var start = caret - 1;
                        if (start > 0 && ((native[start] == '\n' && native[start - 1] == '\r')
                            || (char.IsLowSurrogate(native[start]) && char.IsHighSurrogate(native[start - 1])))) start--;
                        _editSelectionStart = start; _editSelectionLength = caret - start;
                    }
                    else if (key == Windows.System.VirtualKey.Delete && caret < native.Length)
                    {
                        var end = caret + 1;
                        if (end < native.Length && ((native[caret] == '\r' && native[end] == '\n')
                            || (char.IsHighSurrogate(native[caret]) && char.IsLowSurrogate(native[end])))) end++;
                        _editSelectionLength = end - caret;
                    }
                }
                _pendingDeleteKey = null;
                if (!_buffer.CanReplacePage(_page, args.NewText))
                {
                    args.Cancel = true;
                    Feedback($"{header}最多 {TextRules.GetLimit(kind)} 个字符，超出内容未输入。");
                }
                else if (args.NewText.Length > _buffer.PageSize)
                {
                    args.Cancel = true;
                    Feedback("本段已满，请切换到下一段；字数上限按全文计算。");
                }
            };
            // TextChanging is synchronous with the native text update. TextChanged
            // may arrive after a submit command and would leave the backing draft stale.
            void ApplyDisplayedEdit()
            {
                if (_loading || _composing || string.Equals(Input.Text, _displayed, StringComparison.Ordinal)) return;
                try { _buffer.EditDisplayedPage(_page, _displayed, Input.Text, _editSelectionStart, _editSelectionLength); }
                catch (ArgumentException ex) { LoadPage(); Feedback(ex.Message); return; }
                catch (InvalidOperationException ex) { LoadPage(); Feedback(ex.Message); return; }
                _displayed = Input.Text;
                if (!_buffer.MatchesDisplayedPage(_page, Input.Text))
                { var caret = Input.SelectionStart; LoadPage(); Input.Select(Math.Min(caret, Input.Text.Length), 0); }
                _feedback.Visibility = Visibility.Collapsed; UpdatePosition(); Changed?.Invoke();
            }
            Input.TextChanging += (_, _) => ApplyDisplayedEdit();
            // Do not count the temporary Latin spelling while an IME is composing.
            // Only the committed text enters the draft buffer; rejection restores it.
            Input.TextCompositionStarted += (_, _) =>
            {
                _composing = true;
                _editSelectionStart = Input.SelectionStart; _editSelectionLength = Input.SelectionLength;
                _pendingDeleteKey = null;
            };
            Input.TextCompositionEnded += (_, _) => { _composing = false; ApplyDisplayedEdit(); };
            Input.Paste += async (_, args) =>
            {
                args.Handled = true;
                if (_composing) { Feedback("请先完成当前输入，再粘贴。"); return; }
                var page = _page; var start = Input.SelectionStart; var length = Input.SelectionLength;
                var original = _buffer.GetPage(page);
                try
                {
                    var clipboard = Clipboard.GetContent();
                    if (!clipboard.Contains(StandardDataFormats.Text)) return;
                    var pasted = await clipboard.GetTextAsync();
                    if (page != _page || !string.Equals(original, _buffer.GetPage(page), StringComparison.Ordinal))
                    { Feedback("粘贴期间内容发生变化，请重新粘贴。"); return; }
                    PasteText(start, length, pasted);
                }
                catch (Exception ex) { Feedback("无法粘贴：" + ex.Message); }
            };
            LoadPage();
        }

        // Shared by the native clipboard event and the in-process regression harness.
        // The buffer validates the complete field before changing any page.
        public void PasteText(int start, int length, string pasted)
        {
            _buffer.PasteIntoDisplayedPage(_page, Input.Text, start, length, pasted);
            LoadPage(); Changed?.Invoke();
            if (pasted.Length > _buffer.PageSize) Feedback("已粘贴全部内容，并自动分段。", false);
        }

        private void Feedback(string message, bool error = true)
        { _feedback.Text = message; _feedback.Foreground = ThemeBrush(error ? "DangerTextBrush" : "SecondaryTextBrush"); _feedback.Visibility = Visibility.Visible; }

        private void ShowPage(int page)
        {
            if (page < 0 || page >= _buffer.Count) return;
            _page = page; LoadPage(); Input.Focus(FocusState.Programmatic);
        }

        private void LoadPage()
        {
            _loading = true;
            try
            {
                var text = _buffer.GetPage(_page);
                // A single-line native TextBox drops everything after the first
                // newline even for programmatic Text assignments. Expand before
                // loading a multiline/long page so display and buffer stay aligned.
                var expanded = !_compact || _buffer.Count > 1 || text.IndexOfAny(['\r', '\n']) >= 0;
                Input.AcceptsReturn = expanded;
                Input.TextWrapping = expanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
                Input.MinHeight = expanded ? Math.Max(_minimumHeight, 56) : _minimumHeight;
                Input.Text = text; _displayed = Input.Text;
            }
            finally { _loading = false; }
            _feedback.Visibility = Visibility.Collapsed; UpdatePosition();
        }

        private void UpdatePosition()
        {
            var count = TextRules.CountGraphemes(_buffer.Value);
            var limit = TextRules.GetLimit(_kind);
            _position.Text = $"{count} / {limit} 字"
                + (_buffer.Count > 1 ? $" · 第 {_page + 1} / {_buffer.Count} 段" : "")
                + (count > limit ? $" · 原文可保留，修改后请缩减到 {limit} 字以内。" : "");
            _previous.IsEnabled = _page > 0; _next.IsEnabled = _page + 1 < _buffer.Count;
            _position.Visibility = Visibility.Visible;
            _actions.Visibility = _buffer.Count == 1 && _buffer.Length < _buffer.PageSize ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(Input, $"{_header}最多 {limit} 个字符。" + _position.Text);
        }
    }
}
