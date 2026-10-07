using System.Globalization;

namespace PureProject.Core;

public enum TextFieldKind { Title, LongText, Status }

/// <summary>
/// Business input limits use Unicode text elements, independently of the existing
/// UTF-16 storage/import limits. Existing unchanged values are never normalized.
/// </summary>
public static class TextRules
{
    public const int TitleGraphemeLimit = 64;
    public const int LongTextGraphemeLimit = 1024;
    public const int StatusNameGraphemeLimit = 20;
    public const int ProjectNameGraphemeLimit = TitleGraphemeLimit;
    public const int TaskTitleGraphemeLimit = TitleGraphemeLimit;
    public const int ProjectNameUtf16Limit = 500;
    public const int TaskTitleUtf16Limit = 2000;

    public static int CountGraphemes(string? value)
        => string.IsNullOrEmpty(value) ? 0 : new StringInfo(value).LengthInTextElements;

    public static int GetLimit(TextFieldKind kind) => kind switch
    {
        TextFieldKind.Title => TitleGraphemeLimit,
        TextFieldKind.LongText => LongTextGraphemeLimit,
        TextFieldKind.Status => StatusNameGraphemeLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Allows an edit within the limit, or a shortening edit of existing legacy text.</summary>
    public static bool CanAcceptInput(string current, string proposed, TextFieldKind kind, int utf16Limit = 200000)
    {
        var limit = GetLimit(kind);
        if (utf16Limit < 0) throw new ArgumentOutOfRangeException(nameof(utf16Limit));
        if (proposed.Length > utf16Limit) return false;
        if (string.Equals(current, proposed, StringComparison.Ordinal)) return true;
        var proposedCount = CountGraphemes(proposed);
        return proposedCount <= limit || proposedCount < CountGraphemes(current);
    }

    public static string RequireProjectName(string? value, string? original = null)
        => RequireTitle(value, "项目名称", original, ProjectNameUtf16Limit);

    public static string RequireTaskTitle(string? value, string? original = null)
        => RequireTitle(value, "任务标题", original, TaskTitleUtf16Limit);

    public static string RequireTitle(string? value, string label, string? original = null, int utf16Limit = 200000)
        => Require(value, TextFieldKind.Title, label, original, utf16Limit: utf16Limit);

    public static string RequireLongText(string? value, string label = "说明", string? original = null, bool required = false)
        => Require(value, TextFieldKind.LongText, label, original, required, trim: false);

    public static string RequireStatusName(string? value, string? original = null)
        => Require(value, TextFieldKind.Status, "状态名称", original, utf16Limit: 500);

    public static string Require(string? value, TextFieldKind kind, string label, string? original = null,
        bool required = true, bool trim = true, int utf16Limit = 200000)
    {
        var graphemeLimit = GetLimit(kind);
        if (utf16Limit < 0) throw new ArgumentOutOfRangeException(nameof(utf16Limit));
        if (required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label}不能为空。");
        value ??= "";
        var unchanged = original is not null && string.Equals(value, original, StringComparison.Ordinal);
        var candidate = unchanged || !trim ? value : value.Trim();
        if (candidate.Length > utf16Limit)
            throw new ArgumentException($"{label}的编码长度超过 {utf16Limit} 个 UTF-16 单元，请减少复杂组合字符。内容已保留，未截断。");
        if (!unchanged && CountGraphemes(candidate) > graphemeLimit)
            throw new ArgumentException($"{label}最多 {graphemeLimit} 个可见字符（组合表情按一个字符计算）。内容已保留，未截断。");
        return candidate;
    }
}
