using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed partial class ProjectExchangeService
{
    private static void ExportMindMap(Stream output, IReadOnlyList<Project> projects, CancellationToken token)
    {
        using var writer = XmlOutput(output);
        writer.WriteStartDocument(); writer.WriteStartElement("map"); writer.WriteAttributeString("version", "1.0.1");
        // Freeplane's supported registry setting hides technical attributes without removing them.
        writer.WriteStartElement("attribute_registry"); writer.WriteAttributeString("SHOW_ATTRIBUTES", "hide"); writer.WriteEndElement();
        Node(writer, "PureProject 项目", "root");
        Attribute(writer, "pp:format", "PureProject/2"); Attribute(writer, "pp:projects", projects.Count.ToString(CultureInfo.InvariantCulture));
        Attribute(writer, "导入说明", "支持修改项目/组/状态/任务名称、任务描述/优先级/日期和子任务；任务可在现有状态间移动。请保留所有节点和 pp 属性；删除/新增/跨项目移动节点会拒绝导入。其他属性完整保留。进入完成状态时记录导入时间。为兼容 Freeplane，emoji 等字符显示为 Unicode 转义；字面反斜杠需双写。属性默认隐藏，可在 Freeplane 中切换显示。");
        long total = 0;
        for (var p = 0; p < projects.Count; p++)
        {
            token.ThrowIfCancellationRequested(); var project = projects[p];
            var bytes = Payload(project); total += bytes.Length;
            if (total > MaximumFileBytes / 2) throw Error("思维导图项目数据过大；请选择 .pureproject 格式。");
            var data = Convert.ToBase64String(bytes);
            Node(writer, project.Name, "p" + p); Identity(writer, "project", project.Id);
            Attribute(writer, "pp:text_encoding", "unicode-scalars/1");
            Attribute(writer, "pp:sha256", Hash(bytes)); Attribute(writer, "pp:chunks", ((data.Length + ChunkSize - 1) / ChunkSize).ToString(CultureInfo.InvariantCulture));
            Attribute(writer, "pp:description", Preview(project.Description));
            for (var offset = 0; offset < data.Length; offset += ChunkSize)
                Attribute(writer, $"pp:data:{offset / ChunkSize:D6}", data.Substring(offset, Math.Min(ChunkSize, data.Length - offset)));
            var byStatus = project.Tasks.ToLookup(t => t.StatusId, StringComparer.Ordinal);
            for (var g = 0; g < project.TaskGroups.Count; g++)
            {
                var group = project.TaskGroups[g]; Node(writer, group.Name, $"p{p}g{g}"); Identity(writer, "group", group.Id);
                for (var s = 0; s < group.Statuses.Count; s++)
                {
                    var status = group.Statuses[s]; Node(writer, status.Name, $"p{p}g{g}s{s}"); Identity(writer, "status", status.Id);
                    foreach (var task in byStatus[status.Id])
                    {
                        token.ThrowIfCancellationRequested(); Node(writer, task.Title, "t" + Guid.NewGuid().ToString("N")); Identity(writer, "task", task.Id);
                        Attribute(writer, "pp:description", Preview(task.Description)); Attribute(writer, "pp:priority", task.Priority);
                        Attribute(writer, "pp:due_date", Preview(task.DueDate ?? "")); Attribute(writer, "pp:due_time", Preview(task.DueTime ?? ""));
                        for (var subIndex = 0; subIndex < task.Subtasks.Count; subIndex++)
                        {
                            var subtask = task.Subtasks[subIndex];
                            Node(writer, Preview(subtask.Title), "s" + Guid.NewGuid().ToString("N")); Identity(writer, "subtask", subtask.Id);
                            Attribute(writer, "pp:index", subIndex.ToString(CultureInfo.InvariantCulture));
                            Attribute(writer, "pp:done", subtask.Done ? "true" : "false"); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
    }

    private static IReadOnlyList<Project> ImportMindMap(Stream input, CancellationToken token)
    {
        using var reader = XmlInput(input, MaximumFileBytes);
        reader.MoveToContent();
        if (reader.Name != "map") throw Error("不是 FreeMind/Freeplane .mm 文件。");
        var projects = new List<Project>(); var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var encodedProjects = new List<bool>();
        var rootFound = false;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 64) throw Error("思维导图层级过深。");
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth == 1 && reader.Name == "node")
            {
                if (rootFound) throw Error("思维导图有多个根节点。"); rootFound = true;
            }
            else if (reader.Depth == 2 && reader.Name == "attribute")
            {
                if (!attributes.TryAdd(reader.GetAttribute("NAME") ?? "", reader.GetAttribute("VALUE") ?? "")) throw Error("思维导图根属性重复。");
            }
            else if (reader.Depth == 2 && reader.Name == "node")
            {
                if (projects.Count >= 2000) throw Error("项目数超过 2000。");
                using var subtree = reader.ReadSubtree();
                var element = XElement.Load(subtree, LoadOptions.PreserveWhitespace);
                projects.Add(ReadMindMapProject(element, token, out var encoded)); encodedProjects.Add(encoded);
            }
        }
        var format = attributes.GetValueOrDefault("pp:format");
        if (!rootFound || format is not ("PureProject/1" or "PureProject/2") || encodedProjects.Any(encoded => encoded != (format == "PureProject/2")) || !int.TryParse(attributes.GetValueOrDefault("pp:projects"), out var count) || count != projects.Count)
            throw Error("仅支持本应用导出的完整 .mm 文件；格式标记、项目数或项目节点已改变。");
        return projects;
    }

    private static Project ReadMindMapProject(XElement node, CancellationToken token, out bool encoded)
    {
        var attributes = NodeAttributes(node);
        encoded = attributes.TryGetValue("pp:text_encoding", out var encoding);
        if (encoded && encoding != "unicode-scalars/1") throw Error("不支持的思维导图文本编码。");
        if (encoded) DecodeMapAttributes(attributes);
        if (attributes.GetValueOrDefault("pp:type") != "project" || !int.TryParse(attributes.GetValueOrDefault("pp:chunks"), out var chunks) || chunks < 1 || chunks > MaximumProjectBytes / ChunkSize * 2)
            throw Error("思维导图项目元数据缺失或无效。");
        var payload = new StringBuilder();
        for (var i = 0; i < chunks; i++) payload.Append(RequiredAttribute(attributes, $"pp:data:{i:D6}"));
        if (attributes.Keys.Count(key => key.StartsWith("pp:data:", StringComparison.Ordinal)) != chunks) throw Error("项目包含未登记的额外元数据块。");
        if (payload.Length > MaximumProjectBytes * 4L / 3 + 4) throw Error("项目元数据超出大小上限。");
        var project = DecodePayload(Convert.FromBase64String(payload.ToString()), RequiredAttribute(attributes, "pp:sha256"));
        CheckIdentity(attributes, "project", project.Id);
        project.Name = MapOverlay(NodeTitle(node), project.Name, encoded); project.Description = MapOverlay(RequiredAttribute(attributes, "pp:description"), project.Description, encoded, true, true);
        var groups = project.TaskGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var tasks = project.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var seenGroups = new HashSet<string>(StringComparer.Ordinal); var seenStatuses = new HashSet<string>(StringComparer.Ordinal); var seenTasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var groupNode in node.Elements("node"))
        {
            var groupAttributes = NodeAttributes(groupNode, encoded); var groupId = RequiredAttribute(groupAttributes, "pp:id");
            if (!groups.TryGetValue(groupId, out var group) || !seenGroups.Add(groupId)) throw Error("任务组丢失身份、重复或被新增。");
            CheckIdentity(groupAttributes, "group", group.Id); group.Name = MapOverlay(NodeTitle(groupNode), group.Name, encoded);
            var statuses = group.Statuses.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var statusNode in groupNode.Elements("node"))
            {
                var statusAttributes = NodeAttributes(statusNode, encoded); var statusId = RequiredAttribute(statusAttributes, "pp:id");
                if (!statuses.TryGetValue(statusId, out var status) || !seenStatuses.Add(statusId)) throw Error("状态被新增、重复或移动到其他任务组。");
                CheckIdentity(statusAttributes, "status", status.Id); status.Name = MapOverlay(NodeTitle(statusNode), status.Name, encoded);
                foreach (var taskNode in statusNode.Elements("node"))
                {
                    token.ThrowIfCancellationRequested();
                    var taskAttributes = NodeAttributes(taskNode, encoded); var taskId = RequiredAttribute(taskAttributes, "pp:id");
                    if (!tasks.TryGetValue(taskId, out var task) || !seenTasks.Add(taskId)) throw Error("任务被新增、重复或移动到其他项目。");
                    CheckIdentity(taskAttributes, "task", task.Id); task.Title = MapOverlay(NodeTitle(taskNode), task.Title, encoded);
                    task.Description = MapOverlay(RequiredAttribute(taskAttributes, "pp:description"), task.Description, encoded, true, true);
                    task.Priority = RequiredAttribute(taskAttributes, "pp:priority");
                    task.DueDate = MapOptionalOverlay(RequiredAttribute(taskAttributes, "pp:due_date"), task.DueDate, encoded);
                    task.DueTime = MapOptionalOverlay(RequiredAttribute(taskAttributes, "pp:due_time"), task.DueTime, encoded);
                    SetTaskPlacement(project, task, group.Id, status.Id);
                    var seenSubtasks = new HashSet<int>();
                    foreach (var subNode in taskNode.Elements("node"))
                    {
                        var subAttributes = NodeAttributes(subNode, encoded); var subId = RequiredAttribute(subAttributes, "pp:id");
                        if (!int.TryParse(RequiredAttribute(subAttributes, "pp:index"), out var subIndex) || subIndex < 0 || subIndex >= task.Subtasks.Count || !seenSubtasks.Add(subIndex) || subNode.Elements("node").Any()) throw Error("子任务节点被新增、重复或改变层级。");
                        var subtask = task.Subtasks[subIndex];
                        if (subtask.Id != subId) throw Error("子任务身份已改变。");
                        CheckIdentity(subAttributes, "subtask", subId); subtask.Title = MapOverlay(NodeTitle(subNode), subtask.Title, encoded, true);
                        subtask.Done = RequiredAttribute(subAttributes, "pp:done") switch { "true" => true, "false" => false, _ => throw Error("子任务 pp:done 必须为 true 或 false。") };
                    }
                    if (seenSubtasks.Count != task.Subtasks.Count) throw Error("子任务节点已删除；为避免丢失内容，已取消导入。");
                }
                // Every status remains represented, even when it has no tasks.
            }
            if (statuses.Keys.Any(id => !seenStatuses.Contains(id))) throw Error("状态节点已删除。");
        }
        if (seenGroups.Count != groups.Count || seenTasks.Count != tasks.Count) throw Error("任务组或任务节点已删除；为避免丢失内容，已取消导入。");
        return project;
    }

    private static Dictionary<string, string> NodeAttributes(XElement node, bool encoded = false)
    {
        if (node.Elements("richcontent").Any()) throw Error("富文本节点不能无损映射；请使用节点的纯文本标题和 pp:description 属性。");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in node.Elements("attribute"))
            if (!attributes.TryAdd((string?)attribute.Attribute("NAME") ?? "", (string?)attribute.Attribute("VALUE") ?? "")) throw Error("节点属性重复。");
        var type = attributes.GetValueOrDefault("pp:type");
        string[] allowed = type switch
        {
            "project" => ["pp:type", "pp:id", "pp:sha256", "pp:chunks", "pp:description", "pp:text_encoding"],
            "group" or "status" => ["pp:type", "pp:id"],
            "task" => ["pp:type", "pp:id", "pp:description", "pp:priority", "pp:due_date", "pp:due_time"],
            "subtask" => ["pp:type", "pp:id", "pp:index", "pp:done"],
            _ => throw Error("节点类型无效。")
        };
        foreach (var key in attributes.Keys)
            if (!allowed.Contains(key, StringComparer.Ordinal) && !(type == "project" && key.StartsWith("pp:data:", StringComparison.Ordinal)))
                throw Error("不支持新增节点属性：" + key);
        if (encoded) DecodeMapAttributes(attributes);
        return attributes;
    }
    private static string NodeTitle(XElement node) => (string?)node.Attribute("TEXT") ?? throw Error("节点缺少纯文本标题。");
    private static void CheckIdentity(Dictionary<string, string> attributes, string type, string id)
    { if (RequiredAttribute(attributes, "pp:type") != type || RequiredAttribute(attributes, "pp:id") != id) throw Error("节点类型或 ID 已改变。"); }
    private static string RequiredAttribute(Dictionary<string, string> attributes, string key) => attributes.TryGetValue(key, out var value) ? value : throw Error("节点缺少属性：" + key);
    private static string? OptionalOverlay(string value, string? original) => value == (original ?? "") ? original : string.IsNullOrEmpty(value) ? null : value;
    private static void Node(XmlWriter writer, string title, string id)
    {
        writer.WriteStartElement("node"); writer.WriteAttributeString("TEXT", MapText(title)); writer.WriteAttributeString("ID", id);
        writer.WriteAttributeString("FORMAT", "NO_FORMAT");
        if (id != "root") writer.WriteAttributeString("FOLDED", "true");
    }
    private static void Attribute(XmlWriter writer, string name, string value)
    { writer.WriteStartElement("attribute"); writer.WriteAttributeString("NAME", name); writer.WriteAttributeString("VALUE", name.StartsWith("pp:data:", StringComparison.Ordinal) ? value : MapText(value)); writer.WriteEndElement(); }
    private static void Identity(XmlWriter writer, string type, string id)
    {
        // IDs use the same reversible codec as display text, and are decoded before matching.
        XmlConvert.VerifyXmlChars(id);
        Attribute(writer, "pp:type", type); Attribute(writer, "pp:id", id);
    }
    private static string LegacyMapText(string value) => System.Text.RegularExpressions.Regex.Replace(value, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\uFFFE\\uFFFF]", m => $"⟦U+{(int)m.Value[0]:X4}⟧");
    private static string MapText(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\\') text.Append("\\\\");
            else if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                text.Append("\\u{").Append(char.ConvertToUtf32(ch, value[++i]).ToString("X", CultureInfo.InvariantCulture)).Append('}');
            else if (char.IsSurrogate(ch)) throw Error("文本包含无效 Unicode 代理项。");
            else if (!XmlConvert.IsXmlChar(ch)) text.Append("\\u{").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('}');
            else text.Append(ch);
        }
        return text.ToString();
    }
    private static string DecodeMapText(string value)
    {
        if (!value.Contains('\\')) return value;
        var text = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { text.Append(value[i]); continue; }
            if (i + 1 < value.Length && value[i + 1] == '\\') { text.Append('\\'); i++; continue; }
            var end = value.IndexOf('}', i + 1);
            if (i + 3 >= value.Length || value[i + 1] != 'u' || value[i + 2] != '{' || end - i is < 7 or > 9 ||
                !int.TryParse(value.AsSpan(i + 3, end - i - 3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar) ||
                !System.Text.Rune.IsValid(scalar)) throw Error("思维导图 Unicode 转义无效；请保留 \\u{HEX} 字符格式，字面反斜杠需双写。");
            text.Append(char.ConvertFromUtf32(scalar)); i = end;
        }
        return text.ToString();
    }
    private static void DecodeMapAttributes(Dictionary<string, string> attributes)
    { foreach (var key in attributes.Keys.ToArray()) attributes[key] = DecodeMapText(attributes[key]); }
    private static string MapOverlay(string visible, string original, bool encoded, bool preview = false, bool alreadyDecoded = false)
    {
        var expected = preview ? Preview(original) : original;
        if (encoded)
        {
            var decoded = alreadyDecoded ? visible : DecodeMapText(visible);
            return decoded == expected ? original : decoded;
        }
        expected = LegacyMapText(expected);
        if (visible == expected) return original;
        // Some Freeplane versions rewrite supplementary scalars as their low 16 bits.
        // Legacy files lack an unambiguous edit codec: refuse that signature instead of
        // silently replacing intact canonical metadata with damaged visible text.
        foreach (var rune in expected.EnumerateRunes())
            if (rune.Value > 0xffff && visible.Contains((char)(rune.Value & 0xffff)) && !expected.Contains((char)(rune.Value & 0xffff)))
                throw Error("检测到旧版思维导图的 Unicode 字符损坏；完整元数据仍保留。请从原项目重新导出新版 .mm 后编辑，已取消导入以保护原文。");
        return visible;
    }
    private static string? MapOptionalOverlay(string visible, string? original, bool encoded)
    {
        var value = MapOverlay(visible, original ?? "", encoded, true, true);
        return value == (original ?? "") ? original : string.IsNullOrEmpty(value) ? null : value;
    }
}
