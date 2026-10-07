using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed partial class ProjectExchangeService
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string MetadataSheet = "__PureProject";
    private static readonly XNamespace S = SpreadsheetNs;
    private sealed record SheetSpec(string Name, string Path, bool Hidden);
    private sealed record CellValue(string Text, bool Numeric = false);
    private sealed record WorksheetRow(int Index, Dictionary<int, CellValue> Cells)
    {
        public string Text(int column) => Cells.GetValueOrDefault(column)?.Text ?? "";
    }

    private static void ExportWorkbook(Stream output, IReadOnlyList<Project> projects, CancellationToken token)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
        var sheets = new List<SheetSpec>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataSheet };
        for (var i = 0; i < projects.Count; i++)
        {
            var cleaned = Regex.Replace(projects[i].Name, "[\\[\\]:*?/\\\\\\x00-\\x1f]", " ").Trim(' ', '\'');
            if (cleaned.Length == 0) cleaned = "项目";
            var prefix = $"{i + 1} "; var sheetName = prefix + ScalarPrefix(cleaned, 31 - prefix.Length);
            if (!names.Add(sheetName)) throw Error("工作表名称重复。");
            sheets.Add(new(sheetName, $"xl/worksheets/sheet{i + 1}.xml", false));
        }
        if (projects.Count == 0) sheets.Add(new("项目", "xl/worksheets/sheet1.xml", false));
        sheets.Add(new(MetadataSheet, $"xl/worksheets/sheet{sheets.Count + 1}.xml", true));
        WriteWorkbookPackage(zip, sheets);
        for (var i = 0; i < projects.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var stream = zip.CreateEntry(sheets[i].Path, CompressionLevel.Fastest).Open();
            WriteProjectSheet(stream, projects[i], token);
        }
        if (projects.Count == 0)
        {
            using var stream = zip.CreateEntry(sheets[0].Path, CompressionLevel.Fastest).Open(); using var writer = XmlOutput(stream);
            StartWorksheet(writer); writer.WriteStartElement("sheetData", SpreadsheetNs); WriteRow(writer, 1, ["empty", "尚无项目"], 0);
            writer.WriteEndElement(); writer.WriteEndElement();
        }
        using var metadataStream = zip.CreateEntry(sheets[^1].Path, CompressionLevel.Fastest).Open();
        using var metadata = XmlOutput(metadataStream); StartWorksheet(metadata); metadata.WriteStartElement("sheetData", SpreadsheetNs);
        WriteRow(metadata, 1, ["PureProject", "1", projects.Count.ToString(CultureInfo.InvariantCulture)], 0);
        var row = 2; long total = 0;
        for (var p = 0; p < projects.Count; p++)
        {
            token.ThrowIfCancellationRequested(); var bytes = Payload(projects[p]); total += bytes.Length;
            if (total > MaximumExpandedBytes / 2) throw Error("Excel 项目数据过大；请选择 .pureproject 格式。");
            var data = Convert.ToBase64String(bytes); var chunks = (data.Length + ChunkSize - 1) / ChunkSize;
            WriteRow(metadata, row++, ["project", projects[p].Id, sheets[p].Name, Hash(bytes), chunks.ToString(CultureInfo.InvariantCulture)], 0);
            for (var offset = 0; offset < data.Length; offset += ChunkSize)
                WriteRow(metadata, row++, ["data", (offset / ChunkSize).ToString(CultureInfo.InvariantCulture), data.Substring(offset, Math.Min(ChunkSize, data.Length - offset))], 0);
        }
        metadata.WriteEndElement(); metadata.WriteEndElement();
    }

    private static void WriteProjectSheet(Stream stream, Project project, CancellationToken token)
    {
        using var writer = XmlOutput(stream); StartWorksheet(writer);
        writer.WriteStartElement("sheetViews", SpreadsheetNs); writer.WriteStartElement("sheetView", SpreadsheetNs);
        writer.WriteAttributeString("workbookViewId", "0"); writer.WriteAttributeString("showGridLines", "0");
        writer.WriteStartElement("pane", SpreadsheetNs); writer.WriteAttributeString("xSplit", "3"); writer.WriteAttributeString("ySplit", "6");
        writer.WriteAttributeString("topLeftCell", "D7"); writer.WriteAttributeString("activePane", "bottomRight"); writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        writer.WriteStartElement("cols", SpreadsheetNs);
        var widths = new[] { 8d, 22d, 38d, 22d, 22d, 12d, 15d, 12d, 58d };
        for (var i = 0; i < widths.Length; i++)
        {
            writer.WriteStartElement("col", SpreadsheetNs); writer.WriteAttributeString("min", (i + 1).ToString()); writer.WriteAttributeString("max", (i + 1).ToString());
            writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("customWidth", "1");
            if (i == 0) writer.WriteAttributeString("hidden", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteStartElement("sheetData", SpreadsheetNs);
        var merges = new List<string> { "C2:I2", "C3:I3", "B4:I4" };
        WriteRow(writer, 2, ["project", project.Id, project.Name], 1, 30);
        WriteRow(writer, 3, ["description", "项目说明", Preview(project.Description)], 0, 42);
        WriteRow(writer, 4, ["help", "可编辑名称、任务字段和组/状态 ID；保留所有行、ID及隐藏元数据。high/medium/low 为优先级。长文本预览未改保留全文，改后替换全文。完成状态变更会记录导入时间。"], 4, 32);
        WriteRow(writer, 6, ["类型", "ID（保留）", "名称 / 任务标题", "任务组 ID", "状态 ID / 状态类别", "优先级", "截止日期", "截止时间", "任务描述"], 2, 26);
        var row = 7; var byGroup = project.Tasks.ToLookup(t => t.TaskGroupId, StringComparer.Ordinal);
        foreach (var group in project.TaskGroups)
        {
            token.ThrowIfCancellationRequested();
            merges.Add($"C{row}:I{row}"); WriteRow(writer, row++, ["group", group.Id, group.Name], 3, 28);
            foreach (var status in group.Statuses)
                WriteRow(writer, row++, ["status", status.Id, status.Name, group.Id, status.Category], 0, 24);
            foreach (var task in byGroup[group.Id])
            {
                token.ThrowIfCancellationRequested();
                WriteRow(writer, row++, ["task", task.Id, task.Title, task.TaskGroupId, task.StatusId, task.Priority, task.DueDate ?? "", task.DueTime ?? "", Preview(task.Description)], 0, task.Description.Length > 200 ? 58 : task.Title.Length > 60 ? 42 : 30, dateColumn: 7);
            }
        }
        writer.WriteEndElement(); writer.WriteStartElement("mergeCells", SpreadsheetNs); writer.WriteAttributeString("count", merges.Count.ToString());
        foreach (var merge in merges) { writer.WriteStartElement("mergeCell", SpreadsheetNs); writer.WriteAttributeString("ref", merge); writer.WriteEndElement(); }
        writer.WriteEndElement(); writer.WriteEndElement();
    }

    private static void WriteWorkbookPackage(ZipArchive zip, IReadOnlyList<SheetSpec> sheets)
    {
        WriteXmlEntry(zip, "[Content_Types].xml", writer =>
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            writer.WriteStartElement("Types", ns);
            Empty(writer, ns, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
            Empty(writer, ns, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
            Empty(writer, ns, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            Empty(writer, ns, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            foreach (var sheet in sheets) Empty(writer, ns, "Override", ("PartName", "/" + sheet.Path), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            writer.WriteEndElement();
        });
        WriteXmlEntry(zip, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNs);
            Empty(writer, PackageRelationshipNs, "Relationship", ("Id", "rId1"), ("Type", RelationshipNs + "/officeDocument"), ("Target", "xl/workbook.xml")); writer.WriteEndElement();
        });
        WriteXmlEntry(zip, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", SpreadsheetNs); writer.WriteAttributeString("xmlns", "r", null, RelationshipNs);
            writer.WriteStartElement("sheets", SpreadsheetNs);
            for (var i = 0; i < sheets.Count; i++)
            {
                writer.WriteStartElement("sheet", SpreadsheetNs); writer.WriteAttributeString("name", sheets[i].Name); writer.WriteAttributeString("sheetId", (i + 1).ToString());
                if (sheets[i].Hidden) writer.WriteAttributeString("state", "hidden");
                writer.WriteAttributeString("r", "id", RelationshipNs, "rId" + (i + 1)); writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement();
        });
        WriteXmlEntry(zip, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNs);
            for (var i = 0; i < sheets.Count; i++) Empty(writer, PackageRelationshipNs, "Relationship", ("Id", "rId" + (i + 1)), ("Type", RelationshipNs + "/worksheet"), ("Target", sheets[i].Path[3..]));
            Empty(writer, PackageRelationshipNs, "Relationship", ("Id", "styles"), ("Type", RelationshipNs + "/styles"), ("Target", "styles.xml")); writer.WriteEndElement();
        });
        WriteXmlEntry(zip, "xl/styles.xml", writer => writer.WriteRaw(StylesXml));
    }

    private static IReadOnlyList<Project> ImportWorkbook(Stream input, CancellationToken token)
    {
        using var zip = OpenZip(input); var entries = ValidateZip(zip);
        var workbook = ReadSmallXml(RequiredEntry(entries, "xl/workbook.xml"), token);
        var dateSystem = (string?)workbook.Root?.Element(S + "workbookPr")?.Attribute("date1904");
        if (dateSystem is not null and not "0" and not "1" and not "true" and not "false") throw Error("工作簿日期系统无效。");
        var date1904 = dateSystem is "1" or "true";
        var relationships = ReadSmallXml(RequiredEntry(entries, "xl/_rels/workbook.xml.rels"), token);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in relationships.Root?.Elements(XName.Get("Relationship", PackageRelationshipNs)) ?? [])
        {
            if ((string?)rel.Attribute("TargetMode") == "External") throw Error("不支持带外部引用的工作簿。");
            var target = (string?)rel.Attribute("Target") ?? "";
            if (target.Contains('\\') || target.Contains(':') || target.Split('/').Any(p => p is ".." or ".")) throw Error("工作簿关系路径无效。");
            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            if (!paths.TryAdd((string?)rel.Attribute("Id") ?? "", path)) throw Error("工作簿关系 ID 重复。");
        }
        var sheets = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Descendants(S + "sheet"))
        {
            var id = (string?)sheet.Attribute(XName.Get("id", RelationshipNs)) ?? "";
            if (!paths.TryGetValue(id, out var path) || !sheets.TryAdd((string?)sheet.Attribute("name") ?? "", RequiredEntry(entries, path))) throw Error("工作表身份无效或重复。");
        }
        if (sheets.Count > 2002 || !sheets.TryGetValue(MetadataSheet, out var metadata)) throw Error("仅支持本应用导出的 Excel；完整元数据工作表缺失。");
        var shared = entries.TryGetValue("xl/sharedStrings.xml", out var sharedEntry) ? ReadSharedStrings(sharedEntry, token) : [];
        var projects = new List<Project>(); var usedSheets = new HashSet<string>(StringComparer.Ordinal) { MetadataSheet };
        using (var stream = metadata.Open())
        using (var reader = XmlInput(stream))
        using (var rows = ReadRows(reader, shared, token).GetEnumerator())
        {
            if (!rows.MoveNext() || rows.Current.Text(1) != "PureProject" || rows.Current.Text(2) != "1" || !int.TryParse(rows.Current.Text(3), out var count) || count is < 0 or > 2000)
                throw Error("Excel 元数据版本无效。");
            for (var p = 0; p < count; p++)
            {
                if (!rows.MoveNext() || rows.Current.Text(1) != "project") throw Error("Excel 项目元数据缺失。");
                var id = rows.Current.Text(2); var sheetName = rows.Current.Text(3); var checksum = rows.Current.Text(4);
                if (!int.TryParse(rows.Current.Text(5), out var chunks) || chunks < 1 || chunks > MaximumProjectBytes / ChunkSize * 2 || !usedSheets.Add(sheetName) || !sheets.TryGetValue(sheetName, out var entry))
                    throw Error("项目工作表、元数据块数或身份无效。");
                var encoded = new StringBuilder();
                for (var c = 0; c < chunks; c++)
                {
                    if (!rows.MoveNext() || rows.Current.Text(1) != "data" || rows.Current.Text(2) != c.ToString(CultureInfo.InvariantCulture)) throw Error("Excel 元数据块丢失、重复或顺序已改变。");
                    encoded.Append(rows.Current.Text(3));
                    if (encoded.Length > MaximumProjectBytes * 4L / 3 + 4) throw Error("项目元数据超出大小上限。");
                }
                var project = DecodePayload(Convert.FromBase64String(encoded.ToString()), checksum);
                if (project.Id != id) throw Error("项目身份与元数据不符。");
                ReadProjectSheet(entry, project, shared, date1904, token); projects.Add(project);
            }
            if (rows.MoveNext()) throw Error("Excel 元数据包含额外记录。");
        }
        if (projects.Count == 0 && sheets.Count == 2 && sheets.TryGetValue("项目", out var emptySheet))
        {
            using var stream = emptySheet.Open(); using var reader = XmlInput(stream); var found = false;
            foreach (var row in ReadRows(reader, shared, token))
            {
                if (row.Cells.All(c => c.Value.Text.Length == 0)) continue;
                if (found || row.Text(1) != "empty" || row.Text(2) != "尚无项目" || row.Cells.Any(c => c.Key > 2 && c.Value.Text.Length != 0))
                    throw Error("空项目占位表已加入内容；请在应用中创建项目后重新导出。");
                found = true;
            }
            if (!found) throw Error("空项目占位行已删除。");
            usedSheets.Add("项目");
        }
        if (sheets.Keys.Any(name => !usedSheets.Contains(name))) throw Error("工作簿包含未登记的工作表；请删除新增表或从应用重新导出。");
        return projects;
    }

    private static void ReadProjectSheet(ZipArchiveEntry entry, Project project, IReadOnlyList<string> shared, bool date1904, CancellationToken token)
    {
        var groups = project.TaskGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var statuses = project.TaskGroups.SelectMany(g => g.Statuses.Select(s => (Group: g, Status: s))).ToDictionary(p => p.Status.Id, StringComparer.Ordinal);
        var tasks = project.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var seenGroups = new HashSet<string>(StringComparer.Ordinal); var seenStatuses = new HashSet<string>(StringComparer.Ordinal); var seenTasks = new HashSet<string>(StringComparer.Ordinal);
        var seenProject = false; var seenDescription = false; var seenHeader = false; var seenHelp = false;
        using var stream = entry.Open(); using var reader = XmlInput(stream);
        foreach (var row in ReadRows(reader, shared, token))
        {
            if (row.Cells.Any(c => c.Key > 9 && c.Value.Text.Length != 0)) throw Error("存在未支持的新增列；为避免忽略编辑，已取消导入。");
            switch (row.Text(1))
            {
                case "project":
                    if (seenProject || row.Text(2) != project.Id) throw Error("项目身份丢失或重复。");
                    seenProject = true; project.Name = Overlay(row.Text(3), project.Name, false); CheckEmptyCells(row, 4); break;
                case "description":
                    if (seenDescription) throw Error("项目说明行重复。"); seenDescription = true;
                    project.Description = Overlay(row.Text(3), project.Description); CheckEmptyCells(row, 4); break;
                case "help":
                    if (seenHelp) throw Error("说明行重复。"); seenHelp = true; CheckEmptyCells(row, 3); break;
                case "类型":
                    if (seenHeader) throw Error("标题行重复。"); seenHeader = true;
                    if (string.Join("|", Enumerable.Range(2, 8).Select(row.Text)) != "ID（保留）|名称 / 任务标题|任务组 ID|状态 ID / 状态类别|优先级|截止日期|截止时间|任务描述") throw Error("列标题或列顺序已改变。");
                    break;
                case "group":
                    if (!groups.TryGetValue(row.Text(2), out var group) || !seenGroups.Add(group.Id)) throw Error("任务组 ID 已改变、重复或被新增。");
                    group.Name = Overlay(row.Text(3), group.Name, false); CheckEmptyCells(row, 4); break;
                case "status":
                    if (!statuses.TryGetValue(row.Text(2), out var status) || !seenStatuses.Add(status.Status.Id) || status.Group.Id != row.Text(4) || status.Status.Category != row.Text(5))
                        throw Error("状态 ID、所属组或类别已改变。Excel 支持修改状态名称；请在应用中修改状态定义。");
                    status.Status.Name = Overlay(row.Text(3), status.Status.Name, false); CheckEmptyCells(row, 6); break;
                case "task":
                    if (!tasks.TryGetValue(row.Text(2), out var task) || !seenTasks.Add(task.Id)) throw Error("任务 ID 已改变、重复或被新增。");
                    task.Title = Overlay(row.Text(3), task.Title, false); task.Priority = row.Text(6); task.Description = Overlay(row.Text(9), task.Description);
                    var date = row.Text(7);
                    if (row.Cells.GetValueOrDefault(7)?.Numeric == true && date.Length != 0)
                    {
                        if (!double.TryParse(date, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || !double.IsFinite(serial) || serial != Math.Truncate(serial)) throw Error("截止日期不是有效的完整 Excel 日期。");
                        if (date1904) serial += 1462;
                        if (serial < 61 || serial > 2958465) throw Error("截止日期超出受支持的 Excel 日期范围。");
                        date = DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    var time = row.Text(8);
                    if (row.Cells.GetValueOrDefault(8)?.Numeric == true && time.Length != 0)
                    {
                        if (!double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || !double.IsFinite(fraction) || fraction < 0 || fraction >= 1)
                            throw Error("截止时间不是有效的 Excel 时间。");
                        var minutes = Math.Round(fraction * 1440);
                        if (Math.Abs(fraction * 1440 - minutes) > 0.000001 || minutes >= 1440)
                            throw Error("截止时间仅支持分钟精度；请使用 HH:mm。");
                        time = $"{(int)minutes / 60:D2}:{(int)minutes % 60:D2}";
                    }
                    task.DueDate = OptionalOverlay(date, task.DueDate); task.DueTime = OptionalOverlay(time, task.DueTime);
                    SetTaskPlacement(project, task, row.Text(4), row.Text(5)); break;
                default:
                    if (row.Cells.Any(c => c.Value.Text.Length != 0)) throw Error("工作表包含未识别或被修改的记录类型。");
                    break;
            }
        }
        if (!seenProject || !seenDescription || !seenHeader || !seenHelp || seenGroups.Count != groups.Count || seenStatuses.Count != statuses.Count || seenTasks.Count != tasks.Count)
            throw Error("项目、任务组、状态或任务行已删除；为避免丢失内容，已取消导入。");
    }

    private static void CheckEmptyCells(WorksheetRow row, int first)
    { if (row.Cells.Any(c => c.Key >= first && c.Value.Text.Length != 0)) throw Error("在未支持的单元格中发现内容；为避免忽略编辑，已取消导入。"); }

    private static IEnumerable<WorksheetRow> ReadRows(XmlReader reader, IReadOnlyList<string> shared, CancellationToken token)
    {
        var seenRows = new HashSet<int>(); var rowCount = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 32) throw Error("工作表 XML 层级过深。");
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row" || reader.NamespaceURI != SpreadsheetNs) continue;
            if (++rowCount > 1048576 || !int.TryParse(reader.GetAttribute("r"), out var index) || index is < 1 or > 1048576 || !seenRows.Add(index)) throw Error("工作表行号无效或重复。");
            using var subtree = reader.ReadSubtree(); var element = XElement.Load(subtree, LoadOptions.PreserveWhitespace);
            var cells = new Dictionary<int, CellValue>();
            foreach (var cell in element.Elements(S + "c"))
            {
                if (cell.Elements(S + "f").Any()) throw Error("导入不执行公式。请将公式转换为值后重试；以等号开头的原始文本可以正常往返。");
                var reference = (string?)cell.Attribute("r") ?? "";
                var match = Regex.Match(reference, "^([A-Z]{1,3})([1-9][0-9]{0,6})$");
                if (!match.Success || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) != index) throw Error("单元格地址无效。");
                var column = 0; foreach (var ch in match.Groups[1].Value) column = column * 26 + ch - 'A' + 1;
                if (column > 16384) throw Error("单元格列超出范围。");
                var type = (string?)cell.Attribute("t") ?? "n"; var value = cell.Element(S + "v")?.Value ?? "";
                if (type == "inlineStr") value = string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);
                else if (type == "s")
                {
                    if (!int.TryParse(value, out var stringIndex) || stringIndex < 0 || stringIndex >= shared.Count) throw Error("共享字符串索引无效。");
                    value = shared[stringIndex];
                }
                else if (type is not "n" and not "str" and not "d") throw Error("不支持的单元格类型：" + type);
                // Shared strings have already been decoded; inline rich-text runs must be
                // joined before a single decode so literal _xNNNN_ patterns stay literal.
                if (type != "s") value = DecodeExcelText(value);
                if (value.Length > 32767 || !cells.TryAdd(column, new(value, type == "n"))) throw Error("单元格重复或文本超过 Excel 上限。");
            }
            yield return new(index, cells);
        }
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry, CancellationToken token)
    {
        using var stream = entry.Open(); using var reader = XmlInput(stream); var result = new List<string>(); long characters = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si" || reader.NamespaceURI != SpreadsheetNs) continue;
            using var subtree = reader.ReadSubtree(); var item = XElement.Load(subtree);
            var text = string.Concat(item.Descendants(S + "t").Select(t => t.Value)); characters += text.Length;
            text = DecodeExcelText(text);
            if (text.Length > 32767 || characters > MaximumFileBytes || result.Count > 2000000) throw Error("共享字符串超过安全上限。");
            result.Add(text);
        }
        return result;
    }
    private static XDocument ReadSmallXml(ZipArchiveEntry entry, CancellationToken token)
    { using var stream = new MemoryStream(ReadEntry(entry, 4 * 1024 * 1024, token)); using var reader = XmlInput(stream, 4 * 1024 * 1024); return XDocument.Load(reader); }
    private static void StartWorksheet(XmlWriter writer) { writer.WriteStartDocument(); writer.WriteStartElement("worksheet", SpreadsheetNs); }
    private static void WriteRow(XmlWriter writer, int index, string[] values, int style, int height = 0, int dateColumn = -1)
    {
        writer.WriteStartElement("row", SpreadsheetNs); writer.WriteAttributeString("r", index.ToString());
        if (height > 0) { writer.WriteAttributeString("ht", height.ToString()); writer.WriteAttributeString("customHeight", "1"); }
        for (var c = 0; c < values.Length; c++)
        {
            var value = values[c]; if (value.Length == 0) continue;
            var date = default(DateTime); var isDate = c + 1 == dateColumn && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date >= new DateTime(1900, 3, 1);
            var time = default(TimeOnly); var isTime = dateColumn == 7 && c == 7 && TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
            writer.WriteStartElement("c", SpreadsheetNs); writer.WriteAttributeString("r", ColumnName(c + 1) + index);
            writer.WriteAttributeString("s", (isDate ? 5 : isTime ? 7 : dateColumn == 7 && c == 7 ? 6 : style == 1 && c < 2 ? 0 : style).ToString());
            writer.WriteAttributeString("t", isDate || isTime ? "n" : "inlineStr");
            if (isDate) writer.WriteElementString("v", SpreadsheetNs, date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
            else if (isTime) writer.WriteElementString("v", SpreadsheetNs, time.ToTimeSpan().TotalDays.ToString("R", CultureInfo.InvariantCulture));
            else
            {
                if (value.Length > 32767) throw Error("显示字段超过 Excel 单元格 32767 字符限制。");
                writer.WriteStartElement("is", SpreadsheetNs); writer.WriteStartElement("t", SpreadsheetNs); writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                writer.WriteString(EncodeExcelText(value)); writer.WriteEndElement(); writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }
    private static string ColumnName(int column)
    { var name = ""; do { column--; name = (char)('A' + column % 26) + name; column /= 26; } while (column > 0); return name; }
    private static void WriteXmlEntry(ZipArchive zip, string path, Action<XmlWriter> action)
    { using var stream = zip.CreateEntry(path, CompressionLevel.Fastest).Open(); using var writer = XmlOutput(stream); action(writer); }
    private static void Empty(XmlWriter writer, string ns, string name, params (string Name, string Value)[] attributes)
    { writer.WriteStartElement(name, ns); foreach (var attribute in attributes) writer.WriteAttributeString(attribute.Name, attribute.Value); writer.WriteEndElement(); }
    private static string EncodeExcelText(string text)
    {
        text = Regex.Replace(text, "_x[0-9A-Fa-f]{4}_", m => "_x005F_" + m.Value[1..]);
        return Regex.Replace(text, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\uFFFE\\uFFFF]", m => $"_x{(int)m.Value[0]:X4}_");
    }
    private static string DecodeExcelText(string text) => Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    private const string StylesXml = """
    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
      <numFmts count="2"><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/><numFmt numFmtId="165" formatCode="hh:mm"/></numFmts>
      <fonts count="4"><font><sz val="11"/><color rgb="FF1F2937"/><name val="Arial"/></font><font><b/><sz val="16"/><color rgb="FF111827"/><name val="Arial"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Arial"/></font><font><i/><sz val="11"/><color rgb="FF4B5563"/><name val="Arial"/></font></fonts>
      <fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF334155"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE2E8F0"/><bgColor indexed="64"/></patternFill></fill></fills>
      <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
      <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
      <cellXfs count="8"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="2" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0" applyFill="1" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf></cellXfs>
      <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
    </styleSheet>
    """;
}
