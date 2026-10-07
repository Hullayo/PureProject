using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using PureProject.Core;
using PureProject.Infrastructure;

static class RegressionFixes
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly ProjectExchangeService Service = new();

    public static int Run(string output, string? evidenceRoot)
    {
        var results = new List<object>(); var failures = 0;
        void Test(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true, error = (string?)null }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        var source = Fixtures.Create("修复回归-🌱", 2, true);
        source.Name = "=1+1"; source.Tasks[0].Title = "=1+1";
        source.Description = ("首段\r\n第二段\r第三段\n" + new string('长', 200000))[..200000];
        source.Tasks[0].Description = source.Description;
        source.Tasks[1].Description = "中文 🌱 😀 \u0001 字面 \\u{1F331} 与 \\ 路径 C:\\文件";
        source.Tasks[1].Title = "原始 🌱 标题";
        source.Tasks[1].Subtasks[0].Title = "🌱 子任务 \\u{1F331}";
        var xlsx = Path.Combine(output, "mixed-newlines.xlsx"); Service.ExportFile(xlsx, [source]);
        var mm = Path.Combine(output, "freeplane-v2.mm"); Service.ExportFile(mm, [source]);
        File.WriteAllText(Path.Combine(output, "freeplane-v2-expected.json"), JsonSerializer.Serialize(new[] { source }));
        Test("EX-02 new XLSX keeps 200000-unit mixed-newline descriptions", () => Equal([source], Service.ImportFile(xlsx)));
        Test("EX-02 unchanged preview survives unrelated title edit", () =>
        {
            var path = ChangeSheet(xlsx, Path.Combine(output, "title-edit.xlsx"), d => SetInline(TaskRow(d, "t-00001"), "C", "仅修改标题"));
            var expected = Clone(source); expected.Tasks[1].Title = "仅修改标题"; Equal([expected], Service.ImportFile(path));
        });
        foreach (var shared in new[] { false, true })
        foreach (var logicalLength in new[] { 28000, 32767, 32768 })
        {
            var name = $"EX-01 {(shared ? "shared" : "inline")} decoded length {logicalLength}";
            Test(name, () =>
            {
                var literal = string.Concat(Enumerable.Repeat("_x0041_", logicalLength / 7)) + new string('z', logicalLength % 7);
                var encoded = literal.Replace("_x0041_", "_x005F_x0041_");
                var path = ChangeSheet(xlsx, Path.Combine(output, $"length-{shared}-{logicalLength}.xlsx"), d => SetInline(TaskRow(d, "t-00000"), "I", encoded));
                if (shared) MakeShared(path);
                if (logicalLength > 32767) Reject(() => Service.ImportFile(path));
                else { var expected = Clone(source); expected.Tasks[0].Description = literal; Equal([expected], Service.ImportFile(path)); }
            });
        }
        Test("EX-01 split rich text is joined then decoded once", () =>
        {
            var path = ChangeSheet(xlsx, Path.Combine(output, "rich-escaped.xlsx"), d =>
            {
                var cell = TaskRow(d, "t-00000").Elements(S + "c").Single(c => ((string?)c.Attribute("r"))!.StartsWith('I'));
                cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes();
                cell.Add(new XElement(S + "is", new XElement(S + "r", new XElement(S + "t", "_x00")), new XElement(S + "r", new XElement(S + "t", "5F_x0041_"))));
            });
            var expected = Clone(source); expected.Tasks[0].Description = "_x0041_"; Equal([expected], Service.ImportFile(path));
        });
        Test("EX-01 XML value guard stays enforced before large encoded text allocation", () =>
        {
            var path = ChangeSheet(xlsx, Path.Combine(output, "xml-value-guard.xlsx"), d => SetInline(TaskRow(d, "t-00000"), "I", string.Concat(Enumerable.Repeat("_x0001_", 10000))));
            Reject(() => Service.ImportFile(path), "XML");
        });
        Test("EX-FP01 v2 codec retains Unicode, controls, escapes and structural ID", () => Equal([source], Service.ImportFile(mm)));
        Test("EX-FP01 native numeric-entity low16 rewrite cannot damage v2 data", () =>
        {
            var text = File.ReadAllText(mm); var low16 = Low16(text);
            var path = Path.Combine(output, "simulated-freeplane-save.mm"); File.WriteAllText(path, low16);
            Equal([source], Service.ImportFile(path));
        });
        Test("EX-FP01 v2 visible edits decode once and preserve untouched metadata", () =>
        {
            var doc = XDocument.Load(mm); var task = doc.Descendants("node").Single(n => Attr(n, "pp:id") == "t-00001");
            task.SetAttributeValue("TEXT", "修改 \\u{1F331} 字面 \\\\u{0041}");
            var path = Path.Combine(output, "codec-edited.mm"); SaveXml(doc, path);
            var expected = Clone(source); expected.Tasks[1].Title = "修改 🌱 字面 \\u{0041}"; Equal([expected], Service.ImportFile(path));
        });
        Test("EX-FP01 malformed v2 Unicode scalar is explicitly rejected", () =>
        {
            var doc = XDocument.Load(mm); doc.Descendants("node").First(n => Attr(n, "pp:type") == "task").SetAttributeValue("TEXT", "\\u{D800}");
            var path = Path.Combine(output, "invalid-codec.mm"); SaveXml(doc, path); Reject(() => Service.ImportFile(path));
        });
        foreach (var invalid in new[] { "\\", "\\n", "\\u{XYZ1}", "\\u{001}", "\\u{00110000}", "\\u{110000}", "\\u{0041" })
            Test("MM v2 rejects malformed edit " + invalid, () =>
            {
                var doc = XDocument.Load(mm); doc.Descendants("node").First(n => Attr(n, "pp:type") == "task").SetAttributeValue("TEXT", invalid);
                using var stream = new MemoryStream(); SaveXml(doc, stream); stream.Position = 0; Reject(() => Service.Import(stream, ProjectExchangeFormat.FreeMind), "Unicode");
            });
        foreach (var version in new[] { "PureProject/1", "PureProject/999" })
            Test("MM v2 rejects mismatched or unknown root version " + version, () =>
            {
                var doc = XDocument.Load(mm); doc.Root!.Element("node")!.Elements("attribute").Single(a => (string?)a.Attribute("NAME") == "pp:format").SetAttributeValue("VALUE", version);
                using var stream = new MemoryStream(); SaveXml(doc, stream); stream.Position = 0; Reject(() => Service.Import(stream, ProjectExchangeFormat.FreeMind), "格式");
            });
        Test("MM rejects unknown project text encoding", () =>
        {
            var doc = XDocument.Load(mm); doc.Descendants("attribute").Single(a => (string?)a.Attribute("NAME") == "pp:text_encoding").SetAttributeValue("VALUE", "unicode-scalars/999");
            using var stream = new MemoryStream(); SaveXml(doc, stream); stream.Position = 0; Reject(() => Service.Import(stream, ProjectExchangeFormat.FreeMind), "编码");
        });
        Test("EX-FP02 Freeplane hides retained metadata by default", () =>
        {
            var doc = XDocument.Load(mm);
            Ensure((string?)doc.Root!.Element("attribute_registry")?.Attribute("SHOW_ATTRIBUTES") == "hide", "missing supported hide registry");
            Ensure(doc.Descendants("attribute").Any(a => ((string?)a.Attribute("NAME"))?.StartsWith("pp:data:") == true), "payload missing");
        });
        Test("EX-FP03 literal formula title has Freeplane NO_FORMAT", () =>
        {
            var doc = XDocument.Load(mm); var formula = doc.Descendants("node").Where(n => (string?)n.Attribute("TEXT") == "=1+1").ToArray();
            Ensure(formula.Length == 2 && formula.All(n => (string?)n.Attribute("FORMAT") == "NO_FORMAT"), "literal format missing");
        });
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var fixtureHashes = Directory.GetFiles(fixtures).Where(path => Path.GetExtension(path) is ".xlsx" or ".mm").ToDictionary(path => path, FileHash);
        Test("portable legacy XLSX preserves canonical mixed newlines in names and previews", () =>
        {
            var path = Path.Combine(fixtures, "legacy-newlines.xlsx"); Equal(ReadCanonicalPayloads(path), Service.ImportFile(path));
        });
        Test("portable legacy XLSX accepts 28000-unit edited literal escape text", () =>
        {
            var path = Path.Combine(fixtures, "legacy-escaped-28000.xlsx"); var expected = ReadCanonicalPayloads(path);
            expected[0].Tasks[0].Description = string.Concat(Enumerable.Repeat("_x0041_", 4000)); Equal(expected, Service.ImportFile(path));
        });
        Test("portable legacy MM preserves v1 Unicode, control markers and unknown fields", () =>
        {
            var path = Path.Combine(fixtures, "legacy-v1.mm"); Equal(ReadCanonicalPayloads(path), Service.ImportFile(path));
        });
        Test("portable damaged legacy MM rejects known Unicode corruption", () => Reject(() => Service.ImportFile(Path.Combine(fixtures, "legacy-v1-damaged.mm")), "Unicode"));
        Test("portable fixture checksums match checked-in manifest and remain unchanged", () =>
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "manifest.json")));
            Ensure(fixtureHashes.Count == 4, "portable fixture inventory differs");
            foreach (var pair in fixtureHashes)
            {
                var entry = manifest.RootElement.GetProperty(Path.GetFileName(pair.Key));
                Ensure(pair.Value == FileHash(pair.Key) && pair.Value == entry.GetProperty("sha256").GetString() && new FileInfo(pair.Key).Length == entry.GetProperty("bytes").GetInt64(), "fixture bytes changed");
            }
        });
        if (evidenceRoot is not null)
        {
            var samples = new[]
            {
                ("legacy mixed newlines", "stress-audit-20261006/exchange/diagnostic/failure-full-5.xlsx"),
                ("legacy escaped 28000", "stress-audit-20261006/exchange/diagnostic/excel-escaped-literal-28000.xlsx"),
                ("legacy MM unchanged", "stress-followup-20261006/exchange/external/original.mm"),
                ("legacy MM corrupted by native save", "stress-followup-20261006/exchange/external/freeplane-unedited-saved.mm"),
                ("legacy MM title edit plus corrupted description", "stress-followup-20261006/exchange/external/freeplane-title-edited.mm")
            };
            foreach (var (label, relative) in samples) Test("original failure sample: " + label, () =>
            {
                var path = Path.Combine(evidenceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var before = FileHash(path);
                try
                {
                    if (label.Contains("corrupted")) Reject(() => Service.ImportFile(path), "Unicode");
                    else if (label == "legacy escaped 28000")
                    {
                        var actual = Service.ImportFile(path); Ensure(actual.SelectMany(p => p.Tasks).Any(t => t.Description == string.Concat(Enumerable.Repeat("_x0041_", 4000))), "literal description differs");
                    }
                    else Equal(ReadCanonicalPayloads(path), Service.ImportFile(path));
                }
                finally { Ensure(before == FileHash(path), "original evidence changed"); }
            });
        }
        File.WriteAllText(Path.Combine(output, "regression-result.json"), JsonSerializer.Serialize(new { success = failures == 0, failures, assertions = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Regression: {results.Count - failures}/{results.Count} passed");
        return failures == 0 ? 0 : 1;
    }

    private static Project Clone(Project value) => JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(value))!;
    private static void Equal(IEnumerable<Project> expected, IEnumerable<Project> actual) => Ensure(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)), "canonical fields differ");
    private static void Ensure(bool condition, string error) { if (!condition) throw new Exception(error); }
    private static void Reject(Action action, string? message = null)
    {
        try { action(); } catch (ProjectExchangeException ex) { Ensure(message is null || ex.Message.Contains(message, StringComparison.Ordinal), "unexpected rejection: " + ex.Message); return; }
        throw new Exception("expected explicit rejection");
    }
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string? Attr(XElement node, string name) => (string?)node.Elements("attribute").SingleOrDefault(a => (string?)a.Attribute("NAME") == name)?.Attribute("VALUE");
    private static string Low16(string value) => string.Concat(value.EnumerateRunes().Select(r => ((char)(r.Value & 0xffff)).ToString()));
    private static XElement TaskRow(XDocument doc, string id) => doc.Descendants(S + "row").Single(r => r.Elements(S + "c").Any(c => ((string?)c.Attribute("r"))!.StartsWith('B') && c.Value == id));
    private static void SetInline(XElement row, string column, string value)
    {
        var cell = row.Elements(S + "c").Single(c => ((string?)c.Attribute("r"))!.StartsWith(column));
        cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes(); cell.Add(new XElement(S + "is", new XElement(S + "t", value)));
    }
    private static string ChangeSheet(string source, string target, Action<XDocument> mutate)
    {
        File.Copy(source, target, true); using var zip = ZipFile.Open(target, ZipArchiveMode.Update); var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument doc; using (var input = entry.Open()) doc = XDocument.Load(input); mutate(doc); entry.Delete();
        using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); SaveXml(doc, output); return target;
    }
    private static void MakeShared(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument doc; using (var input = entry.Open()) doc = XDocument.Load(input);
        var strings = new XElement(S + "sst"); var index = 0;
        foreach (var cell in doc.Descendants(S + "c").Where(c => (string?)c.Attribute("t") == "inlineStr"))
        {
            strings.Add(new XElement(S + "si", cell.Element(S + "is")!.Elements().Select(e => new XElement(e))));
            cell.SetAttributeValue("t", "s"); cell.RemoveNodes(); cell.Add(new XElement(S + "v", index++));
        }
        entry.Delete(); using (var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open()) SaveXml(doc, output);
        using var shared = zip.CreateEntry("xl/sharedStrings.xml").Open(); SaveXml(new XDocument(strings), shared);
    }
    private static void SaveXml(XDocument document, string path) { using var stream = File.Create(path); SaveXml(document, stream); }
    private static void SaveXml(XDocument document, Stream stream)
    { using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize }); document.Save(writer); }
    private static IReadOnlyList<Project> ReadCanonicalPayloads(string path)
    {
        var payloads = new List<string>();
        if (Path.GetExtension(path) == ".mm")
        {
            var doc = XDocument.Load(path);
            foreach (var project in doc.Descendants("node").Where(n => Attr(n, "pp:type") == "project"))
                payloads.Add(string.Concat(project.Elements("attribute").Where(a => ((string?)a.Attribute("NAME"))?.StartsWith("pp:data:") == true).OrderBy(a => (string?)a.Attribute("NAME")).Select(a => (string?)a.Attribute("VALUE"))));
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/")))
            {
                using var stream = entry.Open(); var sheet = XDocument.Load(stream);
                var rows = sheet.Descendants(S + "row").ToArray();
                if (rows.Length == 0 || rows[0].Elements(S + "c").FirstOrDefault()?.Value != "PureProject") continue;
                var data = new StringBuilder();
                foreach (var row in rows.Skip(1))
                {
                    var cells = row.Elements(S + "c").ToArray();
                    if (cells[0].Value == "project") { if (data.Length != 0) payloads.Add(data.ToString()); data.Clear(); }
                    else if (cells[0].Value == "data") data.Append(cells[2].Value);
                }
                if (data.Length != 0) payloads.Add(data.ToString());
            }
        }
        return payloads.Select(value => { using var doc = JsonDocument.Parse(Convert.FromBase64String(value)); return PmSerializer.ParseCanonicalProject(doc.RootElement); }).ToArray();
    }
}
