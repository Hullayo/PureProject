using System.Text;

namespace PureProject.Core;

/// <summary>A lossless editing buffer whose individual pages are safe for native text layout.</summary>
public sealed class PagedTextBuffer
{
    public const int DefaultPageSize = 4096;
    private readonly string _original;
    private readonly TextFieldKind? _inputKind;
    private readonly List<string> _pages;
    private bool _changed;
    public int PageSize { get; }
    public int MaximumLength { get; }
    public int Count => _pages.Count;
    public int Length { get; private set; }
    public string Value => _changed ? string.Concat(_pages) : _original;

    public PagedTextBuffer(string value, int maximumLength = 200000, int pageSize = DefaultPageSize, TextFieldKind? inputKind = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (pageSize < 2) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maximumLength < value.Length) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        PageSize = pageSize; MaximumLength = maximumLength; _original = value; _inputKind = inputKind;
        _pages = Split(value).ToList(); Length = value.Length;
    }

    public string GetPage(int index) => _pages[index];
    public bool MatchesDisplayedPage(int index, string displayed)
        => NormalizeLines(_pages[index]).Text == NormalizeLines(displayed).Text;

    /// <summary>Apply the displayed edit while retaining original newline spellings outside the changed span.</summary>
    public void EditDisplayedPage(int index, string displayedBefore, string displayedAfter, int? selectionStart = null, int selectionLength = 0)
    {
        var source = _pages[index];
        var original = NormalizeLines(source);
        var before = NormalizeLines(displayedBefore);
        var after = NormalizeLines(displayedAfter);
        if (original.Text != before.Text) throw new InvalidOperationException("当前段落已变化，请重新打开后编辑。");
        if (before.Text == after.Text) return;
        var prefix = 0;
        var suffix = 0;
        var selectionMatches = false;
        if (selectionStart is { } selected && selected >= 0 && selectionLength >= 0 && selected + selectionLength <= displayedBefore.Length)
        {
            var start = NormalizeLines(displayedBefore[..selected]).Text.Length;
            var end = NormalizeLines(displayedBefore[..(selected + selectionLength)]).Text.Length;
            var tail = before.Text.Length - end;
            if (after.Text.Length >= start + tail && before.Text.AsSpan(0, start).SequenceEqual(after.Text.AsSpan(0, start))
                && before.Text.AsSpan(end).SequenceEqual(after.Text.AsSpan(after.Text.Length - tail)))
            { prefix = start; suffix = tail; selectionMatches = true; }
        }
        // With no usable caret range, keep the common suffix first. This avoids
        // moving an original line ending onto a newly inserted line before it.
        while (!selectionMatches && suffix < before.Text.Length && suffix < after.Text.Length
            && before.Text[^(suffix + 1)] == after.Text[^(suffix + 1)]) suffix++;
        while (!selectionMatches && prefix < before.Text.Length - suffix && prefix < after.Text.Length - suffix
            && before.Text[prefix] == after.Text[prefix]) prefix++;
        ReplacePage(index, source[..original.Offsets[prefix]]
            + displayedAfter[after.Offsets[prefix]..after.Offsets[after.Text.Length - suffix]]
            + source[original.Offsets[original.Text.Length - suffix]..]);
    }

    /// <summary>Map a native selection to its original page, so clipboard newlines remain exact.</summary>
    public void PasteIntoDisplayedPage(int index, string displayed, int selectionStart, int selectionLength, string pasted)
    {
        if (selectionStart < 0 || selectionLength < 0 || selectionStart + selectionLength > displayed.Length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        var source = _pages[index];
        var original = NormalizeLines(source);
        if (original.Text != NormalizeLines(displayed).Text) throw new InvalidOperationException("当前段落已变化，请重新粘贴。");
        var start = NormalizeLines(displayed[..selectionStart]).Text.Length;
        var end = NormalizeLines(displayed[..(selectionStart + selectionLength)]).Text.Length;
        ReplacePage(index, source[..original.Offsets[start]] + pasted + source[original.Offsets[end]..]);
    }

    private static (string Text, int[] Offsets) NormalizeLines(string text)
    {
        var value = new StringBuilder(text.Length);
        var offsets = new List<int>(text.Length + 1) { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                value.Append('\n');
            }
            else value.Append(character);
            offsets.Add(index + 1);
        }
        return (value.ToString(), offsets.ToArray());
    }

    public bool CanReplacePage(int index, string text)
    {
        if (Length - _pages[index].Length + text.Length > MaximumLength) return false;
        if (_inputKind is not { } kind) return true;
        var proposed = string.Concat(_pages.Take(index)) + text + string.Concat(_pages.Skip(index + 1));
        return string.Equals(proposed, _original, StringComparison.Ordinal)
            || TextRules.CanAcceptInput(Value, proposed, kind, MaximumLength);
    }

    public void ReplacePage(int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var previous = _pages[index];
        if (string.Equals(previous, text, StringComparison.Ordinal)) return;
        var length = Length - previous.Length + text.Length;
        if (length > MaximumLength) throw new ArgumentException($"全文不能超过 {MaximumLength:N0} 个文本单位。", nameof(text));
        if (!CanReplacePage(index, text))
            throw new ArgumentException($"全文最多 {TextRules.GetLimit(_inputKind!.Value)} 个字符，超出内容未输入。", nameof(text));
        var replacements = Split(text).ToArray();
        _pages.RemoveAt(index); _pages.InsertRange(index, replacements);
        RepairBoundaries();
        Length = length; _changed = true;
    }

    public void InsertPage(int index)
    {
        _pages.Insert(index, "");
        // An empty editing page does not change the persisted content.
    }

    private void RepairBoundaries()
    {
        for (var index = 0; index + 1 < _pages.Count; index++)
        {
            var left = _pages[index];
            if (left.Length == 0) continue;
            var next = index + 1;
            while (next < _pages.Count && _pages[next].Length == 0) next++;
            if (next == _pages.Count) break;
            var right = _pages[next];
            if (!IsPair(left[^1], right[0])) continue;
            if (left.Length < PageSize)
            { _pages[index] = left + right[0]; _pages[next] = right[1..]; }
            else
            {
                _pages[index] = left[..^1];
                var replacement = Split(left[^1] + right).ToArray();
                _pages.RemoveAt(next); _pages.InsertRange(next, replacement);
            }
        }
    }

    private static bool IsPair(char left, char right)
        => (char.IsHighSurrogate(left) && char.IsLowSurrogate(right)) || (left == '\r' && right == '\n');

    private IEnumerable<string> Split(string text)
    {
        if (text.Length == 0) { yield return ""; yield break; }
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(start + PageSize, text.Length);
            if (end < text.Length && IsPair(text[end - 1], text[end])) end--;
            yield return text[start..end];
            start = end;
        }
    }
}
