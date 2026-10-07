using System.Globalization;

namespace PureProject.Core;

/// <summary>
/// Business input limits use Unicode text elements, independently of the existing
/// UTF-16 storage/import limits. Existing unchanged values are never normalized.
/// </summary>
public static class TextRules
{
    public const int ProjectNameGraphemeLimit = 64;
    public const int TaskTitleGraphemeLimit = 128;
    public const int ProjectNameUtf16Limit = 500;
    public const int TaskTitleUtf16Limit = 2000;

    public static int CountGraphemes(string? value)
        => string.IsNullOrEmpty(value) ? 0 : new StringInfo(value).LengthInTextElements;

    public static string RequireProjectName(string? value, string? original = null)
        => Require(value, original, "项目名称", ProjectNameGraphemeLimit, ProjectNameUtf16Limit);

    public static string RequireTaskTitle(string? value, string? original = null)
        => Require(value, original, "任务标题", TaskTitleGraphemeLimit, TaskTitleUtf16Limit);

    private static string Require(string? value, string? original, string label, int graphemeLimit, int utf16Limit)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label}不能为空。");
        var unchanged = original is not null && string.Equals(value, original, StringComparison.Ordinal);
        var candidate = unchanged ? value : value.Trim();
        if (candidate.Length > utf16Limit)
            throw new ArgumentException($"{label}的编码长度超过 {utf16Limit} 个 UTF-16 单元，请减少复杂组合字符。内容已保留，未截断。");
        if (!unchanged && CountGraphemes(candidate) > graphemeLimit)
            throw new ArgumentException($"{label}最多 {graphemeLimit} 个可见字符（组合表情按一个字符计算）。内容已保留，未截断。");
        return candidate;
    }
}
