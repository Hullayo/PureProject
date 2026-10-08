using PureProject.Infrastructure;

internal static class ShortcutSettingsTests
{
    public static int Run()
    {
        var passed = 0;
        var defaults = ShortcutSettings.GetEffective(new Dictionary<string, string>());
        Check(defaults.Count == 8 && defaults["ClosePanel"] == "Esc" && defaults["Settings"] == "Ctrl+,", "legacy settings receive complete default shortcuts");

        var overrides = new Dictionary<string, string> { ["New"] = " shift + control + k ", ["ClosePanel"] = "f8" };
        var effective = ShortcutSettings.GetEffective(overrides);
        Check(effective["New"] == "Ctrl+Shift+K" && effective["ClosePanel"] == "F8" && effective["TaskSearch"] == "Ctrl+F", "partial customization merges defaults and normalizes modifier order");
        Check(overrides["New"] == " shift + control + k " && overrides.Count == 2, "validation does not mutate caller settings");
        Check(ShortcutSettings.Parse("Ctrl+Comma") == ShortcutSettings.Parse("control+,"), "punctuation aliases map to the same physical key");
        Check(ShortcutSettings.Parse("Ctrl+Plus").ToString() == "Ctrl+Plus", "plus key retains an unambiguous round-trip representation");
        Check(ShortcutSettings.Parse("Alt+Shift+9").Key == '9' && ShortcutSettings.Parse("F12").Key == 123, "supported digits and function keys map to Windows key codes");

        Reject(new() { ["New"] = "Ctrl+F" }, "customization cannot collide with an unchanged default");
        Reject(new() { ["New"] = "Ctrl+K", ["TaskSearch"] = "Control+k" }, "normalized duplicate gestures are rejected");
        Reject(new() { ["MissingAction"] = "Ctrl+K" }, "unknown actions are rejected");
        foreach (var invalid in new[] { "", "N", "Shift+N", "Ctrl+Ctrl+N", "Ctrl+", "Win+N", "Ctrl+F13", "Ctrl+Space", "Alt+F4", "Ctrl+Esc", "Alt+Esc", "Ctrl+N+K" })
            Reject(new() { ["New"] = invalid }, "invalid or system-owned gesture is rejected: " + invalid);

        var swapped = new Dictionary<string, string> { ["New"] = "Ctrl+F", ["TaskSearch"] = "Ctrl+N" };
        ShortcutSettings.Validate(swapped);
        Check(ShortcutSettings.GetEffective(swapped)["TaskSearch"] == "Ctrl+N", "batch customization permits exchanging two actions");
        var conflict = Capture(new() { ["New"] = "Ctrl+F" });
        Check(conflict.Contains("新建项目 / 任务", StringComparison.Ordinal) && conflict.Contains("搜索当前任务", StringComparison.Ordinal), "conflict error identifies both actions");
        return passed;

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            passed++;
            Console.WriteLine("PASS " + name);
        }

        void Reject(Dictionary<string, string> values, string name) => Check(Capture(values).Length > 0, name);

        static string Capture(Dictionary<string, string> values)
        {
            try { ShortcutSettings.Validate(values); }
            catch (ArgumentException error) { return error.Message; }
            return "";
        }
    }
}
