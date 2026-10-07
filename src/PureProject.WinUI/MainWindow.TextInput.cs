using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PureProject.Core;
using System.Runtime.CompilerServices;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private sealed class TextCompositionState { public bool Active; public bool JustEnded; }
    private static readonly ConditionalWeakTable<TextBox, TextCompositionState> TextCompositions = new();

    private static void TrackTextComposition(TextBox input)
        => TextCompositions.GetValue(input, static field =>
        {
            var state = new TextCompositionState();
            field.TextCompositionStarted += (_, _) => state.Active = true;
            field.TextCompositionEnded += (_, _) =>
            {
                state.Active = false; state.JustEnded = true;
                field.DispatcherQueue.TryEnqueue(() => state.JustEnded = false);
            };
            return state;
        });

    private static bool IsTextComposing(TextBox input)
        => TextCompositions.TryGetValue(input, out var state) && (state.Active || state.JustEnded);

    private static string NativeText(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void ConfigureTextInput(TextBox input, TextFieldKind kind, string label,
        TextBlock? feedback = null, string? original = null)
    {
        // MaxLength counts UTF-16 units and can split supplementary Chinese or emoji.
        // Validate whole proposed edits instead, retaining the previous text on rejection.
        input.MaxLength = 0;
        TrackTextComposition(input);
        var limit = TextRules.GetLimit(kind);
        var hint = feedback ?? EditorText("", 11, "MutedTextBrush");
        hint.TextWrapping = TextWrapping.Wrap;
        if (feedback is null) input.Description = hint;
        var restoring = false;
        var composing = false;
        var beforeComposition = "";
        var selectionStart = 0;
        var selectionLength = 0;
        bool OriginalUnchanged(string value) => original is not null && NativeText(value) == NativeText(original);
        bool Accept(string before, string after) => OriginalUnchanged(after) || TextRules.CanAcceptInput(before, after, kind);
        void Update(string? error = null)
        {
            var count = TextRules.CountGraphemes(input.Text);
            hint.Text = error ?? (count > limit
                ? $"{count} / {limit} 字 · 原文可保留，修改后请缩减到 {limit} 字以内。"
                : $"{count} / {limit} 字");
            hint.Foreground = ThemeBrush(error is null ? "MutedTextBrush" : "DangerTextBrush");
            hint.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(input, $"{label}最多 {limit} 个字符。" + hint.Text);
        }
        string Error() => $"{label}最多 {limit} 个字符，超出内容未输入。";
        input.BeforeTextChanging += (_, args) =>
        {
            if (restoring || composing || Accept(input.Text, args.NewText)) return;
            args.Cancel = true;
            Update(Error());
        };
        input.TextChanging += (_, _) => { if (!restoring && !composing) Update(); };
        input.TextCompositionStarted += (_, _) =>
        {
            composing = true;
            beforeComposition = input.Text;
            selectionStart = input.SelectionStart;
            selectionLength = input.SelectionLength;
        };
        input.TextCompositionEnded += (_, _) =>
        {
            composing = false;
            if (Accept(beforeComposition, input.Text)) { Update(); return; }
            restoring = true;
            try
            {
                input.Text = beforeComposition;
                var start = Math.Min(selectionStart, input.Text.Length);
                input.Select(start, Math.Min(selectionLength, input.Text.Length - start));
            }
            finally { restoring = false; }
            Update(Error());
        };
        Update();
    }
}
