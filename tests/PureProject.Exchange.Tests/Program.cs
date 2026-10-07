using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PureProject.Core;
using PureProject.Infrastructure;

var outputArg = Array.IndexOf(args, "--out");
var output = Path.GetFullPath(outputArg >= 0 ? args[outputArg + 1] : Path.Combine(AppContext.BaseDirectory, "exchange-results"));
Directory.CreateDirectory(output);
var service = new ProjectExchangeService();
var verifyArg = Array.IndexOf(args, "--verify-file");
if (verifyArg >= 0)
{
    var verified = service.ImportFile(args[verifyArg + 1]);
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, projects = verified.Select(p => new { p.Id, p.Name, tasks = p.Tasks.Count, firstTask = p.Tasks.Take(1).Select(t => new { t.Id, t.Title, t.TaskGroupId, t.StatusId, t.DueDate, t.DueTime }) }) }));
    return;
}
if (args.Contains("--benchmark")) { RunBenchmark(); return; }
if (args.Contains("--package-capacity-fix")) { Environment.ExitCode = PackageCapacityFix.Run(output); return; }
var compareArg = Array.IndexOf(args, "--compare-file");
if (compareArg >= 0)
{
    var expectedArg = Array.IndexOf(args, "--expected-json");
    if (expectedArg < 0) throw new ArgumentException("--expected-json is required for --compare-file");
    var expected = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(args[expectedArg + 1]))!;
    var inputPath = Path.GetFullPath(args[compareArg + 1]);
    var hashBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(inputPath)));
    try
    {
        var actual = service.ImportFile(inputPath);
        var exact = Equal(expected, actual);
        var hashAfter = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(inputPath)));
        File.WriteAllText(Path.Combine(output, "external-compare-result.json"), JsonSerializer.Serialize(new { inputPath, exact, hashBefore, hashAfter, sourceUnchanged = hashBefore == hashAfter, projects = actual.Count, tasks = actual.Sum(p => p.Tasks.Count) }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"External canonical comparison: exact={exact}; sourceUnchanged={hashBefore == hashAfter}");
        Environment.ExitCode = exact && hashBefore == hashAfter ? 0 : 1;
    }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(output, "external-compare-result.json"), JsonSerializer.Serialize(new { inputPath, exact = false, hashBefore, error = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(ex.Message); Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--regression-fixes"))
{
    var evidenceArg = Array.IndexOf(args, "--evidence-root");
    Environment.ExitCode = RegressionFixes.Run(output, evidenceArg < 0 ? null : Path.GetFullPath(args[evidenceArg + 1]));
    return;
}
var passed = 0;
var original = Fixtures.Create("项目-001", 3, true);
var originals = new[] { original, Fixtures.Create("空项目-002", 0, false) };
var originalJson = JsonSerializer.Serialize(originals);
foreach (var (format, extension) in Formats())
{
    var path = Path.Combine(output, "sample." + extension);
    service.ExportFile(path, originals);
    var roundtrip = service.ImportFile(path);
    Check(Equal(originals, roundtrip), extension + ": all-property round trip, extensions, empty project/group/status");
    Check(roundtrip[0].Tasks[0].Description.Length == 90000, extension + ": long descriptions survive");
    Check(roundtrip[0].Tasks[1].DueDate is null && roundtrip[0].Tasks[2].DueDate == "", extension + ": null versus empty optional values survive");
    using var memory = new MemoryStream(); service.Export(memory, [], format); memory.Position = 0;
    Check(service.Import(memory, format).Count == 0 && memory.CanRead, extension + ": empty collection and caller stream ownership");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Expect<OperationCanceledException>(() => service.ExportFile(path, originals, cts.Token), extension + ": cancellation before export");
    Check(Equal(originals, service.ImportFile(path)), extension + ": cancelled atomic export preserves destination");
    using var truncated = new MemoryStream(File.ReadAllBytes(path)[..20]);
    ExpectAny(() => service.Import(truncated, format), extension + ": truncated file rejected");
}
Check(originalJson == JsonSerializer.Serialize(originals), "exchange does not mutate source projects");
foreach (var (format, extension) in Formats())
{
    var control = Fixtures.Create("controls", 1, false); control.Tasks[0].Title = "标题\u0001 _x0001_"; control.Tasks[0].Description = "描述\u0002 _x0041_";
    using var stream = new MemoryStream(); service.Export(stream, [control], format); stream.Position = 0;
    Check(Equal(new[] { control }, service.Import(stream, format)), extension + ": control characters and literal escape patterns round trip");
    var emojiBoundary = Fixtures.Create("emoji-boundary", 1, false); emojiBoundary.Name = new string('a', 28) + "😀"; emojiBoundary.Tasks[0].Description = new string('a', 69) + "😀" + new string('b', 140);
    using var emojiStream = new MemoryStream(); service.Export(emojiStream, [emojiBoundary], format); emojiStream.Position = 0;
    Check(Equal(new[] { emojiBoundary }, service.Import(emojiStream, format)), extension + ": preview and sheet-name truncation preserve Unicode scalar boundaries");
}
var mm = XDocument.Load(Path.Combine(output, "sample.mm"));
var mapRoot = mm.Root!.Element("node")!;
Check(mapRoot.Attribute("FOLDED") is null && mapRoot.Elements("node").Count() == originals.Length, "mm: root stays expanded to expose project list");
Check(mapRoot.Descendants("node").All(node => (string?)node.Attribute("FOLDED") == "true"), "mm: all non-root nodes default to collapsed");
var unfoldedMap = new XDocument(mm);
foreach (var node in unfoldedMap.Descendants("node")) node.SetAttributeValue("FOLDED", "false");
using (var unfoldedStream = new MemoryStream())
{
    unfoldedMap.Save(unfoldedStream); unfoldedStream.Position = 0;
    Check(Equal(originals, service.Import(unfoldedStream, ProjectExchangeFormat.FreeMind)), "mm: changing fold state does not change canonical project data");
}
var taskNode = mm.Descendants("node").First(n => Attr(n, "pp:id") == "t-00000");
taskNode.SetAttributeValue("TEXT", "修改后的标题 =1+1");
taskNode.Elements("attribute").Single(a => (string?)a.Attribute("NAME") == "pp:priority").SetAttributeValue("VALUE", "high");
var targetStatus = mm.Descendants("node").First(n => Attr(n, "pp:id") == "g-b-done");
taskNode.Remove(); targetStatus.Add(taskNode);
var subtaskNode = taskNode.Elements("node").First(); subtaskNode.SetAttributeValue("TEXT", "已编辑子任务");
subtaskNode.Elements("attribute").Single(a => (string?)a.Attribute("NAME") == "pp:done").SetAttributeValue("VALUE", "true");
var editedMapPath = Path.Combine(output, "edited.mm"); mm.Save(editedMapPath);
var editedMap = service.ImportFile(editedMapPath)[0];
Check(editedMap.Tasks[0].Title == "修改后的标题 =1+1" && editedMap.Tasks[0].Priority == "high", "mm: visible task title/attributes imported");
Check(editedMap.Tasks[0].TaskGroupId == "g-b" && editedMap.Tasks[0].StatusId == "g-b-done" && editedMap.Tasks[0].CompletedAt is not null, "mm: hierarchy move imports group/status and completion");
Check(editedMap.Tasks[0].Subtasks[0].Title == "已编辑子任务" && editedMap.Tasks[0].Subtasks[0].Done, "mm: visible subtask title/completion imported");
Check(editedMap.Tasks[0].Extra["future_task"].GetProperty("array").GetArrayLength() == 3, "mm: visible edits preserve unknown fields");
ExpectMapMutation(n => n.Descendants("node").First(e => Attr(e, "pp:type") == "task").Remove(), "mm: removed task rejected");
ExpectMapMutation(n => { var task = n.Descendants("node").First(e => Attr(e, "pp:type") == "task"); task.AddAfterSelf(new XElement(task)); }, "mm: duplicate task rejected");
ExpectMapMutation(n => n.Descendants("attribute").First(e => ((string?)e.Attribute("NAME")) == "pp:sha256").SetAttributeValue("VALUE", new string('0', 64)), "mm: corrupt metadata rejected");
ExpectMapMutation(n => n.Descendants("node").First(e => Attr(e, "pp:type") == "task").Add(new XElement("richcontent", "unsupported")), "mm: unsupported rich-text edit rejected");
ExpectMapMutation(n => n.Descendants("node").First(e => Attr(e, "pp:type") == "task").Add(new XElement("attribute", new XAttribute("NAME", "custom"), new XAttribute("VALUE", "must not be silently discarded"))), "mm: unsupported added data attribute rejected");
ExpectMapMutation(n => n.Descendants("node").First(e => Attr(e, "pp:type") == "project").Add(new XElement("attribute", new XAttribute("NAME", "pp:data:extra"), new XAttribute("VALUE", "extra"))), "mm: extra metadata block rejected");
using (var xxe = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE map [<!ENTITY xxe SYSTEM 'file:///C:/Windows/win.ini'>]><map><node TEXT='&xxe;'/></map>")))
    ExpectAny(() => service.Import(xxe, ProjectExchangeFormat.FreeMind), "mm: external XML entity rejected");
using (var deep = new MemoryStream(Encoding.UTF8.GetBytes("<map>" + string.Concat(Enumerable.Repeat("<node>", 100)) + string.Concat(Enumerable.Repeat("</node>", 100)) + "</map>")))
    ExpectAny(() => service.Import(deep, ProjectExchangeFormat.FreeMind), "mm: deep XML rejected");

var excelPath = Path.Combine(output, "sample.xlsx");
var editedExcelPath = MutateZip(excelPath, "edited.xlsx", "xl/worksheets/sheet1.xml", doc =>
{
    var row = TaskRow(doc, "t-00000"); SetCell(row, "C", "Excel 修改标题"); SetCell(row, "D", "g-b"); SetCell(row, "E", "g-b-active"); SetCell(row, "I", "修改后的完整描述");
    var date = row.Elements().Single(c => ((string?)c.Attribute("r"))!.StartsWith('G'));
    date.Element(date.Name.Namespace + "v")!.Value = new DateTime(2027, 3, 5).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture);
});
var editedExcel = service.ImportFile(editedExcelPath)[0];
Check(editedExcel.Tasks[0].Title == "Excel 修改标题" && editedExcel.Tasks[0].Description == "修改后的完整描述", "xlsx: visible title and long preview replacement imported");
Check(editedExcel.Tasks[0].TaskGroupId == "g-b" && editedExcel.Tasks[0].StatusId == "g-b-active", "xlsx: visible group/status IDs imported");
Check(editedExcel.Tasks[0].DueDate == "2027-03-05", "xlsx: typed date edit imported");
Check(editedExcel.Tasks[0].Comments[0].Content == original.Tasks[0].Comments[0].Content, "xlsx: visible edits retain comments");
var numericTime = MutateZip(excelPath, "numeric-time.xlsx", "xl/worksheets/sheet1.xml", d =>
{
    var cell = TaskRow(d, "t-00000").Elements().Single(c => ((string?)c.Attribute("r"))!.StartsWith('H'));
    cell.SetAttributeValue("t", "n"); cell.RemoveNodes(); cell.Add(new XElement(cell.Name.Namespace + "v", "0.625"));
});
Check(service.ImportFile(numericTime)[0].Tasks[0].DueTime == "15:00", "xlsx: native edited numeric time converted to HH:mm");
ExpectWorkbookMutation("time-seconds.xlsx", d =>
{
    var cell = TaskRow(d, "t-00000").Elements().Single(c => ((string?)c.Attribute("r"))!.StartsWith('H'));
    cell.SetAttributeValue("t", "n"); cell.RemoveNodes(); cell.Add(new XElement(cell.Name.Namespace + "v", "0.6251"));
}, "xlsx: unsupported seconds rejected instead of truncating");
var date1904Path = MutateZip(excelPath, "date1904.xlsx", "xl/workbook.xml", d => d.Root!.AddFirst(new XElement(d.Root.Name.Namespace + "workbookPr", new XAttribute("date1904", "1"))));
using (var zip = ZipFile.Open(date1904Path, ZipArchiveMode.Update))
{
    var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!; XDocument doc;
    using (var stream = entry.Open()) doc = XDocument.Load(stream);
    var date = TaskRow(doc, "t-00000").Elements().Single(c => ((string?)c.Attribute("r"))!.StartsWith('G')).Element(doc.Root!.Name.Namespace + "v")!;
    date.Value = (double.Parse(date.Value, System.Globalization.CultureInfo.InvariantCulture) - 1462).ToString(System.Globalization.CultureInfo.InvariantCulture);
    entry.Delete(); using var replacement = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); doc.Save(replacement);
}
Check(Equal(originals, service.ImportFile(date1904Path)), "xlsx: Excel 1904 date system interpreted correctly");
var sharedPath = Path.Combine(output, "shared-strings.xlsx"); File.Copy(excelPath, sharedPath, true);
using (var zip = ZipFile.Open(sharedPath, ZipArchiveMode.Update))
{
    var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!; XDocument doc;
    using (var stream = entry.Open()) doc = XDocument.Load(stream);
    var ns = doc.Root!.Name.Namespace; var strings = new XElement(ns + "sst"); var index = 0;
    foreach (var cell in doc.Descendants(ns + "c").Where(c => (string?)c.Attribute("t") == "inlineStr"))
    {
        strings.Add(new XElement(ns + "si", cell.Element(ns + "is")!.Nodes().Select(n => n is XElement el ? new XElement(el) : n)));
        cell.SetAttributeValue("t", "s"); cell.RemoveNodes(); cell.Add(new XElement(ns + "v", index++));
    }
    entry.Delete(); using (var replacement = zip.CreateEntry("xl/worksheets/sheet1.xml").Open()) doc.Save(replacement);
    using var sharedStream = zip.CreateEntry("xl/sharedStrings.xml").Open(); strings.Save(sharedStream);
}
Check(Equal(originals, service.ImportFile(sharedPath)), "xlsx: shared strings produced by spreadsheet editors supported");
using (var zip = ZipFile.OpenRead(excelPath))
{
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Check(sheet.Descendants(ns + "mergeCell").Any(c => (string?)c.Attribute("ref") == "C2:I2"), "xlsx: title merged");
    Check(sheet.Descendants(ns + "mergeCell").Count() >= 5, "xlsx: groups merged individually");
    Check(!sheet.Descendants(ns + "f").Any(), "xlsx: formula-like text exported as literals");
    Check(sheet.Descendants(ns + "pane").Any() && sheet.Descendants(ns + "sheetView").Single().Attribute("showGridLines")?.Value == "0", "xlsx: frozen headers and hidden gridlines");
}
ExpectWorkbookMutation("removed-row.xlsx", d => TaskRow(d, "t-00000").Remove(), "xlsx: removed row rejected");
ExpectWorkbookMutation("duplicate-row.xlsx", d => { var row = TaskRow(d, "t-00000"); var copy = new XElement(row); copy.SetAttributeValue("r", "200"); foreach (var cell in copy.Elements()) cell.SetAttributeValue("r", string.Concat(((string)cell.Attribute("r")!).TakeWhile(char.IsLetter)) + "200"); row.AddAfterSelf(copy); }, "xlsx: duplicate ID rejected");
ExpectWorkbookMutation("invalid-status.xlsx", d => SetCell(TaskRow(d, "t-00000"), "E", "unknown"), "xlsx: invalid status rejected");
ExpectWorkbookMutation("formula.xlsx", d => { var row = TaskRow(d, "t-00000"); var cell = row.Elements().First(c => ((string?)c.Attribute("r"))!.StartsWith('C')); cell.Add(new XElement(cell.Name.Namespace + "f", "1+1")); }, "xlsx: inserted formula rejected");
var corruptExcel = MutateZip(excelPath, "corrupt-metadata.xlsx", "xl/worksheets/sheet3.xml", d => { var row = d.Descendants().First(e => e.Name.LocalName == "row" && (string?)e.Attribute("r") == "2"); SetCell(row, "D", new string('0', 64)); });
ExpectAny(() => service.ImportFile(corruptExcel), "xlsx: corrupt metadata rejected");
var traversal = Path.Combine(output, "traversal.pureproject"); File.Copy(Path.Combine(output, "sample.pureproject"), traversal, true);
using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Update)) { using var entry = zip.CreateEntry("../outside.txt").Open(); entry.WriteByte(1); }
ExpectAny(() => service.ImportFile(traversal), "zip: path traversal rejected without extraction");
var bomb = Path.Combine(output, "bomb.pureproject");
if (File.Exists(bomb)) File.Delete(bomb);
using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create)) { using var entry = zip.CreateEntry("zeros").Open(); var zeros = new byte[1024 * 1024]; for (var i = 0; i < 513; i++) entry.Write(zeros); }
ExpectAny(() => service.ImportFile(bomb), "zip: oversized decompressed payload rejected before reading");
var invalid = Fixtures.Create("invalid", 1, false); invalid.TaskGroups[0].Statuses.Clear();
ExpectAny(() => service.ExportFile(Path.Combine(output, "invalid.pureproject"), [invalid]), "invalid zero-status group rejected");
ExpectAny(() => service.ExportFile(Path.Combine(output, "duplicate.pureproject"), [original, original]), "duplicate project IDs rejected");
var futurePath = Path.Combine(output, "future.pureproject"); File.Copy(Path.Combine(output, "sample.pureproject"), futurePath, true);
using (var zip = ZipFile.Open(futurePath, ZipArchiveMode.Update))
{
    var entry = zip.GetEntry("manifest.json")!; JsonNode manifest;
    using (var stream = entry.Open()) manifest = JsonNode.Parse(stream)!;
    manifest["Version"] = 999; entry.Delete(); using var replacement = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(replacement, manifest);
}
ExpectAny(() => service.ImportFile(futurePath), "pureproject: future manifest version rejected");
var manyProjects = Enumerable.Range(0, 2000).Select(n => Fixtures.Create(new string('中', 196) + n.ToString("D4"), 0, false)).ToList();
using (var longManifest = new MemoryStream())
{
    service.Export(longManifest, manyProjects, ProjectExchangeFormat.PureProject); longManifest.Position = 0;
    Check(service.Import(longManifest, ProjectExchangeFormat.PureProject).Select(p => p.Id).SequenceEqual(manyProjects.Select(p => p.Id)), "pureproject: 2000 longest multibyte IDs fit manifest read limit");
}
var emptyWorkbook = Path.Combine(output, "empty.xlsx"); service.ExportFile(emptyWorkbook, []);
var modifiedEmpty = MutateZip(emptyWorkbook, "modified-empty.xlsx", "xl/worksheets/sheet1.xml", d => SetCell(d.Descendants().First(e => e.Name.LocalName == "row"), "C", "unexpected content"));
ExpectAny(() => service.ImportFile(modifiedEmpty), "xlsx: edits to empty placeholder rejected");
File.WriteAllText(Path.Combine(output, "test-result.json"), JsonSerializer.Serialize(new { success = true, passed, output }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {passed} exchange assertions. Output: {output}");

void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
void Expect<T>(Action action, string name) where T : Exception { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL expected " + typeof(T).Name + ": " + name); }
void ExpectAny(Action action, string name) { try { action(); } catch (Exception e) when (e is ProjectExchangeException or PmValidationException or InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL expected validation failure: " + name); }
void ExpectMapMutation(Action<XDocument> mutate, string name) { var doc = XDocument.Load(Path.Combine(output, "sample.mm")); mutate(doc); using var stream = new MemoryStream(); doc.Save(stream); stream.Position = 0; ExpectAny(() => service.Import(stream, ProjectExchangeFormat.FreeMind), name); }
void ExpectWorkbookMutation(string name, Action<XDocument> mutate, string assertion) { var path = MutateZip(excelPath, name, "xl/worksheets/sheet1.xml", mutate); ExpectAny(() => service.ImportFile(path), assertion); }
string MutateZip(string input, string name, string entryName, Action<XDocument> mutate)
{
    var path = Path.Combine(output, name); File.Copy(input, path, true);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry(entryName)!; XDocument doc;
    using (var stream = entry.Open()) doc = XDocument.Load(stream);
    mutate(doc); entry.Delete(); using var replacement = zip.CreateEntry(entryName).Open(); doc.Save(replacement); return path;
}
static bool Equal(object a, object b) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a), JsonSerializer.SerializeToNode(b));
static string? Attr(XElement node, string name) => (string?)node.Elements("attribute").SingleOrDefault(a => (string?)a.Attribute("NAME") == name)?.Attribute("VALUE");
static XElement TaskRow(XDocument doc, string id) => doc.Descendants().Single(e => e.Name.LocalName == "row" && e.Elements().Any(c => c.Name.LocalName == "c" && ((string?)c.Attribute("r"))!.StartsWith('B') && c.Value == id));
static void SetCell(XElement row, string column, string value)
{
    var ns = row.Name.Namespace; var reference = column + (string)row.Attribute("r")!;
    var cell = row.Elements(ns + "c").SingleOrDefault(c => (string?)c.Attribute("r") == reference);
    if (cell is null) { cell = new XElement(ns + "c", new XAttribute("r", reference)); row.Add(cell); }
    cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes(); cell.Add(new XElement(ns + "is", new XElement(ns + "t", value)));
}
static (ProjectExchangeFormat Format, string Extension)[] Formats() => [(ProjectExchangeFormat.PureProject, "pureproject"), (ProjectExchangeFormat.FreeMind, "mm"), (ProjectExchangeFormat.Excel, "xlsx")];
void RunBenchmark()
{
    var countArg = Array.IndexOf(args, "--tasks"); var tasks = countArg >= 0 ? int.Parse(args[countArg + 1]) : 100000;
    var all = new List<Project>(); var remaining = tasks;
    var inputArg = Array.IndexOf(args, "--input"); var inputPath = inputArg >= 0 ? Path.GetFullPath(args[inputArg + 1]) : null;
    string? sourceHash = null;
    if (inputPath is not null)
    {
        using var input = File.OpenRead(inputPath);
        sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)); input.Position = 0;
        all = PmSerializer.ReadInternalAsync(input).GetAwaiter().GetResult().ToList(); tasks = all.Sum(p => p.Tasks.Count);
    }
    else while (remaining > 0) { var count = Math.Min(10000, remaining); all.Add(Fixtures.Create("scale-" + all.Count, count, false)); remaining -= count; }
    var results = new List<object>();
    var formatArg = Array.IndexOf(args, "--format"); var selectedFormat = formatArg >= 0 ? args[formatArg + 1] : null;
    foreach (var (format, extension) in Formats().Where(p => selectedFormat is null || p.Extension == selectedFormat))
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var baseline = Process.GetCurrentProcess().PrivateMemorySize64;
        long peakPrivate = baseline, peakManaged = GC.GetTotalMemory(false);
        using var timer = new Timer(_ => { InterlockedExtensions.Max(ref peakPrivate, Process.GetCurrentProcess().PrivateMemorySize64); InterlockedExtensions.Max(ref peakManaged, GC.GetTotalMemory(false)); }, null, 0, 20);
        var path = Path.Combine(output, "scale-" + tasks + "." + extension); var watch = Stopwatch.StartNew(); service.ExportFile(path, all); var exportMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var imported = service.ImportFile(path); var importMs = watch.Elapsed.TotalMilliseconds;
        var exact = imported.Count == all.Count && imported.Select(p => p.Tasks.Count).SequenceEqual(all.Select(p => p.Tasks.Count));
        var exchangePeakPrivate = peakPrivate; var exchangePeakManaged = peakManaged; timer.Change(Timeout.Infinite, Timeout.Infinite);
        for (var i = 0; i < all.Count && exact; i++) exact = Equal(all[i], imported[i]);
        var sourceUnchanged = true;
        if (inputPath is not null) { using var source = File.OpenRead(inputPath); sourceUnchanged = sourceHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)); }
        var row = new { format = extension, inputPath, sourceHash, sourceUnchanged, projectCount = all.Count, groupCount = all.Sum(p => p.TaskGroups.Count), statusCount = all.Sum(p => p.TaskGroups.Sum(g => g.Statuses.Count)), taskCount = tasks, bytes = new FileInfo(path).Length, exportMs, importMs, baselinePrivateBytes = baseline, peakPrivateBytes = exchangePeakPrivate, peakManagedBytes = exchangePeakManaged, exact };
        results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row)); if (!exact) throw new Exception("Benchmark round trip mismatch: " + extension);
        if (!sourceUnchanged) throw new Exception("Input fixture changed during benchmark.");
    }
    File.WriteAllText(Path.Combine(output, selectedFormat is null ? "benchmark-result.json" : "benchmark-" + selectedFormat + ".json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}
static class InterlockedExtensions
{
    public static void Max(ref long target, long value) { long before; do { before = Interlocked.Read(ref target); if (value <= before) return; } while (Interlocked.CompareExchange(ref target, value, before) != before); }
}
