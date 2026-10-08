using System.Globalization;

namespace PureProject.Infrastructure;

public sealed record ShortcutDefinition(string Id, string Label, string DefaultGesture);

[Flags]
public enum ShortcutModifiers { None = 0, Control = 1, Alt = 2, Shift = 4 }

public readonly record struct ShortcutGesture(int Key, ShortcutModifiers Modifiers)
{
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        parts.Add(ShortcutSettings.KeyName(Key));
        return string.Join('+', parts);
    }
}

/// <summary>Portable shortcut validation shared by settings persistence and the Windows UI.</summary>
public static class ShortcutSettings
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new ShortcutDefinition("New", "新建项目 / 任务", "Ctrl+N"),
        new ShortcutDefinition("TaskSearch", "搜索当前任务", "Ctrl+F"),
        new ShortcutDefinition("GlobalSearch", "全局搜索", "Ctrl+Shift+F"),
        new ShortcutDefinition("Settings", "打开设置", "Ctrl+,"),
        new ShortcutDefinition("Undo", "撤销", "Ctrl+Z"),
        new ShortcutDefinition("Redo", "重做", "Ctrl+Y"),
        new ShortcutDefinition("RedoAlternate", "重做（备用）", "Ctrl+Shift+Z"),
        new ShortcutDefinition("ClosePanel", "关闭面板", "Esc")
    });

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = 27, ["Escape"] = 27,
        [","] = 188, ["Comma"] = 188, ["."] = 190, ["Period"] = 190,
        ["/"] = 191, ["Slash"] = 191, [";"] = 186, ["Semicolon"] = 186,
        ["\\"] = 220, ["Backslash"] = 220, ["["] = 219, ["LeftBracket"] = 219,
        ["]"] = 221, ["RightBracket"] = 221, ["-"] = 189, ["Minus"] = 189,
        ["Plus"] = 187, ["="] = 187, ["Equal"] = 187
    };

    public static void Validate(IReadOnlyDictionary<string, string> overrides) => _ = GetEffective(overrides);

    public static Dictionary<string, string> GetEffective(IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        foreach (var id in overrides.Keys)
            if (!Definitions.Any(definition => definition.Id == id))
                throw new ArgumentException($"未知的快捷键操作：{id}。");

        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        var assigned = new Dictionary<ShortcutGesture, ShortcutDefinition>();
        foreach (var definition in Definitions)
        {
            var text = overrides.TryGetValue(definition.Id, out var configured) ? configured : definition.DefaultGesture;
            ShortcutGesture gesture;
            try { gesture = Parse(text); }
            catch (ArgumentException error) { throw new ArgumentException($"“{definition.Label}”：{error.Message}", error); }
            if (assigned.TryGetValue(gesture, out var previous))
                throw new ArgumentException($"快捷键 {gesture} 同时分配给“{previous.Label}”和“{definition.Label}”，请更换其中一项。");
            assigned.Add(gesture, definition);
            effective.Add(definition.Id, gesture.ToString());
        }
        return effective;
    }

    public static ShortcutGesture Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            throw new ArgumentException("请按下有效的快捷键组合。");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ShortcutModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Control,
                "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift,
                _ => throw new ArgumentException("仅支持 Ctrl、Alt、Shift 与一个按键的组合。")
            };
            if ((modifiers & modifier) != 0) throw new ArgumentException("同一个修饰键不能重复。");
            modifiers |= modifier;
        }
        var keyName = parts[^1].ToUpperInvariant();
        int key;
        if (keyName.Length == 1 && (keyName[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = keyName[0];
        else if (keyName.StartsWith('F') && int.TryParse(keyName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 12) key = 111 + function;
        else if (!NamedKeys.TryGetValue(keyName, out key)) throw new ArgumentException("支持字母、数字、F1–F12、Esc 和常用标点键。");

        if ((modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0 && key != 27 && key is not (>= 112 and <= 123))
            throw new ArgumentException("字母、数字和标点必须搭配 Ctrl 或 Alt，以免影响正常输入。");
        if ((modifiers.HasFlag(ShortcutModifiers.Alt) && key is 115 or 27)
            || (modifiers.HasFlag(ShortcutModifiers.Control) && key == 27)
            || (modifiers == (ShortcutModifiers.Control | ShortcutModifiers.Shift) && key == '0'))
            throw new ArgumentException("此组合由 Windows 使用，请选择其他快捷键。");
        return new(key, modifiers);
    }

    public static string KeyName(int key) => key switch
    {
        >= 65 and <= 90 or >= 48 and <= 57 => ((char)key).ToString(),
        >= 112 and <= 123 => "F" + (key - 111).ToString(CultureInfo.InvariantCulture),
        27 => "Esc", 188 => ",", 190 => ".", 191 => "/", 186 => ";", 220 => "\\",
        219 => "[", 221 => "]", 189 => "-", 187 => "Plus",
        _ => throw new ArgumentException("此按键不能设置为快捷键。")
    };
}
