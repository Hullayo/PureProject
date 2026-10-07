using System.Text.Json;
using PureProject.Core;

internal static class PagedTextTests
{
    public static void Register(List<(string Name, Action Test)> tests)
    {
        tests.Add(("Paged text preserves 200000 units exactly without splitting surrogate or CRLF boundaries", () =>
        {
            var prefix = new string('a', 4095) + "🌱" + new string('b', 4093) + "\r\n";
            const string mixed = "中🌱\r\n👩‍💻e\u0301\n";
            var repeats = (200000 - prefix.Length) / mixed.Length;
            var original = prefix + string.Concat(Enumerable.Repeat(mixed, repeats));
            original += new string('z', 200000 - original.Length);
            var buffer = new PagedTextBuffer(original);
            Check(buffer.Length == 200000 && ReferenceEquals(buffer.Value, original), "Unchanged source not retained exactly");
            CheckPages(buffer);
            Check(string.Concat(Pages(buffer)) == original, "Initial splitting lost text");
            buffer.ReplacePage(1, buffer.GetPage(1)); buffer.InsertPage(2);
            Check(buffer.Value == original && ReferenceEquals(buffer.Value, original), "No-op or blank editing page changed persisted text");
        }));
        tests.Add(("Paged edits and long paste preserve other content and overflow is transactional", () =>
        {
            var original = string.Concat(Enumerable.Repeat("a🌱\r\n中文", 3000));
            var buffer = new PagedTextBuffer(original, maximumLength: original.Length + 10000);
            var oldPages = Pages(buffer); var middle = oldPages.Length / 2;
            var pasted = string.Concat(Enumerable.Repeat("新🌱\r\n", 1600));
            var expected = string.Concat(oldPages.Take(middle)) + pasted + string.Concat(oldPages.Skip(middle + 1));
            buffer.ReplacePage(middle, pasted);
            Check(buffer.Value == expected && buffer.Length == expected.Length, "Middle paste changed untouched pages");
            CheckPages(buffer);
            var beforePages = Pages(buffer); var before = buffer.Value;
            try { buffer.ReplacePage(0, new string('x', buffer.MaximumLength + 1)); throw new Exception("Overflow accepted"); }
            catch (ArgumentException) { }
            Check(buffer.Value == before && Pages(buffer).SequenceEqual(beforePages), "Rejected overflow changed editing pages");
            buffer.InsertPage(buffer.Count);
            Check(buffer.Value == before, "Inserting blank page after edits changed text");
        }));
        tests.Add(("History retention weight includes long descriptions, comments and unknown extension payloads", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("memory weight");
            var task = service.CreateTask(project, "task");
            var small = ModelMemoryEstimate.ForProject(project);
            task.Description = new string('d', 200000);
            var description = ModelMemoryEstimate.ForProject(project);
            Check(description - small >= 399000, "Large description omitted from retention weight");
            task.Comments.Add(new() { Id = "comment", Content = new string('c', 10000) });
            var comments = ModelMemoryEstimate.ForProject(project);
            Check(comments - description >= 20000, "Comment text omitted from retention weight");
            task.Extra["plugin"] = JsonSerializer.SerializeToElement(new { payload = new string('p', 15000) });
            var extensions = ModelMemoryEstimate.ForProject(project);
            Check(extensions - comments >= 30000, "Unknown JSON extension omitted from retention weight");
        }));
        tests.Add(("Editing repairs CRLF and surrogate pairs formed across existing or blank page boundaries", () =>
        {
            var line = new PagedTextBuffer("abcX\nabc", maximumLength: 100, pageSize: 4);
            line.InsertPage(1);
            line.ReplacePage(0, "abc\r");
            Check(line.Value == "abc\r\nabc", "CRLF repair changed concatenated value");
            CheckPages(line);
            var emoji = new PagedTextBuffer("abcXq", maximumLength: 100, pageSize: 4);
            emoji.ReplacePage(1, "\uDF31q");
            emoji.ReplacePage(0, "abc\uD83C");
            Check(emoji.Value == "abc🌱q", "Surrogate repair changed concatenated value");
            CheckPages(emoji);
        }));
        tests.Add(("Native newline normalization does not rewrite untouched page content during edits", () =>
        {
            const string source = "a\r\nb\nc\rd";
            foreach (var newline in new[] { "\r", "\n", "\r\n" })
            {
                var displayed = string.Join(newline, new[] { "a", "b", "c", "d" });
                var buffer = new PagedTextBuffer(source);
                buffer.EditDisplayedPage(0, displayed, displayed);
                Check(ReferenceEquals(buffer.Value, source), "Native no-op changed source identity");
                buffer.EditDisplayedPage(0, displayed, displayed.Replace("b", "新🌱"));
                Check(buffer.Value == "a\r\n新🌱\nc\rd", "Single text edit normalized untouched line endings");

                buffer = new PagedTextBuffer(source);
                buffer.EditDisplayedPage(0, displayed, displayed.Replace("b" + newline + "c", ""));
                Check(buffer.Value == "a\r\n\rd", "Range delete changed line endings outside its span");

                buffer = new PagedTextBuffer(source);
                buffer.EditDisplayedPage(0, displayed, displayed.Replace("b", "b" + newline + "新"));
                Check(buffer.Value == "a\r\nb" + newline + "新\nc\rd", "Newly entered newline or original suffix changed");
            }
        }));
        tests.Add(("Native selections map to original offsets and clipboard text keeps exact newlines", () =>
        {
            const string source = "a\r\nb\nc\rd";
            const string pasted = "x\r\ny\nz\r🌱";
            foreach (var newline in new[] { "\r", "\n", "\r\n" })
            {
                var displayed = string.Join(newline, new[] { "a", "b", "c", "d" });
                var buffer = new PagedTextBuffer(source);
                buffer.PasteIntoDisplayedPage(0, displayed, displayed.IndexOf('b'), 1, pasted);
                Check(buffer.Value == "a\r\n" + pasted + "\nc\rd", "Clipboard or untouched source line endings changed");

                buffer = new PagedTextBuffer(source);
                buffer.PasteIntoDisplayedPage(0, displayed, displayed.IndexOf('b'), 2 + newline.Length, "");
                Check(buffer.Value == "a\r\n\rd", "Selected deletion mapped to wrong original offsets");

                buffer = new PagedTextBuffer(source);
                buffer.PasteIntoDisplayedPage(0, displayed, displayed.Length, 0, pasted);
                Check(buffer.Value == source + pasted, "Paste at final caret changed original source");
            }
        }));
        tests.Add(("Stale native edits and mapped paste overflow preserve the complete original buffer", () =>
        {
            const string source = "a\r\nb\nc\rd";
            var buffer = new PagedTextBuffer(source, maximumLength: source.Length + 2);
            try { buffer.EditDisplayedPage(0, "stale", "new"); throw new Exception("Stale editor accepted"); }
            catch (InvalidOperationException) { }
            try { buffer.PasteIntoDisplayedPage(0, "a\rb\rc\rd", 2, 1, "too long"); throw new Exception("Overflow accepted"); }
            catch (ArgumentException) { }
            Check(ReferenceEquals(buffer.Value, source) && buffer.Length == source.Length, "Rejected operation changed original buffer");
        }));
        tests.Add(("Native selection hints preserve the intended boundary between repeated newlines", () =>
        {
            const string source = "a\r\n\nb";
            const string displayed = "a\r\rb";
            var insertBefore = new PagedTextBuffer(source);
            insertBefore.EditDisplayedPage(0, displayed, "a\r\r\rb", 1, 0);
            Check(insertBefore.Value == "a\r\r\n\nb", "Insertion moved across original repeated line endings");
            var insertBetween = new PagedTextBuffer(source);
            insertBetween.EditDisplayedPage(0, displayed, "a\r\r\rb", 2, 0);
            Check(insertBetween.Value == "a\r\n\r\nb", "Insertion hint between repeated newlines ignored");
            var deleteFirst = new PagedTextBuffer(source);
            deleteFirst.EditDisplayedPage(0, displayed, "a\rb", 1, 1);
            Check(deleteFirst.Value == "a\nb", "First selected newline not removed exactly");
            var deleteSecond = new PagedTextBuffer(source);
            deleteSecond.EditDisplayedPage(0, displayed, "a\rb", 2, 1);
            Check(deleteSecond.Value == "a\r\nb", "Second selected newline not removed exactly");
        }));
    }

    private static string[] Pages(PagedTextBuffer buffer) => Enumerable.Range(0, buffer.Count).Select(buffer.GetPage).ToArray();
    private static void CheckPages(PagedTextBuffer buffer)
    {
        var pages = Pages(buffer).Where(p => p.Length != 0).ToArray();
        Check(Pages(buffer).All(p => p.Length <= buffer.PageSize), "Native page exceeds layout bound");
        for (var i = 1; i < pages.Length; i++)
        {
            if (pages[i - 1].Length == 0 || pages[i].Length == 0) continue;
            var previous = pages[i - 1][^1]; var next = pages[i][0];
            Check(!(char.IsHighSurrogate(previous) && char.IsLowSurrogate(next)) && !(previous == '\r' && next == '\n'), "Unsafe page boundary");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
