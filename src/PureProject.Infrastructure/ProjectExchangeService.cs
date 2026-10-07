using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using PureProject.Core;

namespace PureProject.Infrastructure;

public enum ProjectExchangeFormat { PureProject, FreeMind, Excel }

public sealed class ProjectExchangeException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Bounded, lossless interchange of canonical project models. Streams remain open.</summary>
public sealed partial class ProjectExchangeService
{
    public const int FormatVersion = 1;
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumProjectBytes = 64 * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const int ChunkSize = 24000;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128,
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false
    };

    public static ProjectExchangeFormat FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pureproject" => ProjectExchangeFormat.PureProject,
        ".mm" => ProjectExchangeFormat.FreeMind,
        ".xlsx" => ProjectExchangeFormat.Excel,
        _ => throw new ProjectExchangeException("支持的交换文件扩展名为 .pureproject、.mm 和 .xlsx。")
    };

    public void ExportFile(string path, IEnumerable<Project> projects, CancellationToken cancellationToken = default)
    {
        var format = FromPath(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Export(file, projects, format, cancellationToken);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<Project> ImportFile(string path, CancellationToken cancellationToken = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Import(file, FromPath(path), cancellationToken);
    }

    public void Export(Stream destination, IEnumerable<Project> projects, ProjectExchangeFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(projects);
        var list = projects.Take(2001).ToList();
        if (list.Count > 2000 || list.Any(p => p is null)) throw Error("项目数超过 2000 或包含空项目。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in list)
        {
            cancellationToken.ThrowIfCancellationRequested(); PmSerializer.EnsureValid(project);
            if (!ids.Add(project.Id)) throw Error("项目 ID 重复。");
        }
        using var bounded = new ExchangeWriteStream(destination, cancellationToken);
        switch (format)
        {
            case ProjectExchangeFormat.PureProject: ExportPackage(bounded, list, cancellationToken); break;
            case ProjectExchangeFormat.FreeMind: ExportMindMap(bounded, list, cancellationToken); break;
            case ProjectExchangeFormat.Excel: ExportWorkbook(bounded, list, cancellationToken); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public IReadOnlyList<Project> Import(Stream source, ProjectExchangeFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.CanSeek && source.Length - source.Position > MaximumFileBytes) throw Error("文件超过 256 MB 上限。");
        try
        {
            var projects = format switch
            {
                ProjectExchangeFormat.PureProject => ImportPackage(source, cancellationToken),
                ProjectExchangeFormat.FreeMind => ImportMindMap(source, cancellationToken),
                ProjectExchangeFormat.Excel => ImportWorkbook(source, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested(); PmSerializer.EnsureValid(project);
                if (!ids.Add(project.Id)) throw Error("项目 ID 重复。");
            }
            return projects;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException or FormatException or OverflowException)
        { throw Error("交换文件无效或不完整：" + ex.Message, ex); }
    }

    private sealed record Manifest(string Format, int Version, List<ManifestProject> Projects);
    private sealed record ManifestProject(string Id, string Entry, string Sha256);

    private static void ExportPackage(Stream output, IReadOnlyList<Project> projects, CancellationToken token)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
        var manifest = new Manifest("PureProject", FormatVersion, []);
        long total = 0;
        for (var i = 0; i < projects.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = Payload(projects[i]); total += bytes.Length;
            if (total > MaximumExpandedBytes) throw Error("项目总数据超过 512 MB 上限。");
            var name = $"projects/{i:D4}.json";
            using (var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open()) stream.Write(bytes);
            manifest.Projects.Add(new(projects[i].Id, name, Hash(bytes)));
        }
        // The importer budgets every expanded ZIP entry, including this exact manifest.
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (total + manifestBytes.Length > MaximumExpandedBytes) throw Error("项目数据及清单总量超过 512 MB 上限。");
        using var metadata = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
        metadata.Write(manifestBytes);
    }

    private static IReadOnlyList<Project> ImportPackage(Stream input, CancellationToken token)
    {
        using var zip = OpenZip(input);
        var entries = ValidateZip(zip);
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(RequiredEntry(entries, "manifest.json"), 4 * 1024 * 1024, token), JsonOptions);
        if (manifest is null || manifest.Format != "PureProject" || manifest.Version != FormatVersion || manifest.Projects is null || manifest.Projects.Count > 2000)
            throw Error("不支持的 .pureproject 格式或版本。");
        var result = new List<Project>(); var used = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var item in manifest.Projects)
        {
            token.ThrowIfCancellationRequested();
            if (item is null || !used.Add(item.Entry)) throw Error("项目清单包含重复条目。");
            var bytes = ReadEntry(RequiredEntry(entries, item.Entry), MaximumProjectBytes, token);
            var project = DecodePayload(bytes, item.Sha256);
            if (project.Id != item.Id) throw Error("项目 ID 与清单不符。");
            result.Add(project);
        }
        if (entries.Keys.Any(k => !used.Contains(k))) throw Error("项目包包含未列入清单的内容。");
        return result;
    }

    private static byte[] Payload(Project project)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions);
        if (bytes.Length > MaximumProjectBytes) throw Error("单项目数据超过 64 MB 上限。");
        // Apply the same strict structural contract in both directions before committing a file.
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128, AllowDuplicateProperties = false });
        _ = PmSerializer.ParseCanonicalProject(document.RootElement);
        return bytes;
    }
    private static Project DecodePayload(byte[] bytes, string checksum)
    {
        if (bytes.Length > MaximumProjectBytes || !string.Equals(Hash(bytes), checksum, StringComparison.OrdinalIgnoreCase))
            throw Error("项目元数据校验失败；为避免丢失属性，已取消导入。");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        return PmSerializer.ParseCanonicalProject(doc.RootElement);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ProjectExchangeException Error(string message, Exception? inner = null) => new(message, inner);

    private static ZipArchive OpenZip(Stream input)
    {
        // ZipArchive copies non-seekable inputs into an unbounded buffer, so require a bounded seekable stream.
        if (!input.CanSeek) throw Error("ZIP 导入需要可定位的文件或内存流。");
        return new ZipArchive(input, ZipArchiveMode.Read, true);
    }
    private static Dictionary<string, ZipArchiveEntry> ValidateZip(ZipArchive zip)
    {
        if (zip.Entries.Count > MaximumEntries) throw Error("ZIP 条目过多。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Length > 240 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is ".." or ".") || !entries.TryAdd(name, entry))
                throw Error("ZIP 路径无效或重复。");
            total = checked(total + entry.Length);
            if (entry.Length > MaximumExpandedBytes || total > MaximumExpandedBytes || (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 2000))
                throw Error("ZIP 展开尺寸或压缩比例超过安全上限。");
        }
        return entries;
    }
    private static ZipArchiveEntry RequiredEntry(Dictionary<string, ZipArchiveEntry> entries, string name) =>
        entries.TryGetValue(name, out var entry) ? entry : throw Error($"文件缺少 {name}。");

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximum, CancellationToken token)
    {
        if (entry.Length > maximum) throw Error("ZIP 条目超出大小上限。");
        using var input = entry.Open(); using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[65536]; int count;
        while ((count = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + count > maximum) throw Error("ZIP 条目超出大小上限。");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) throw Error("ZIP 条目长度不符。");
        return output.ToArray();
    }
    private static XmlReader XmlInput(Stream stream, long limit = MaximumExpandedBytes) => new ExchangeXmlReader(XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit,
        MaxCharactersFromEntities = 1024, IgnoreComments = true, CloseInput = false
    }));
    private static XmlWriter XmlOutput(Stream stream) => XmlWriter.Create(stream, new XmlWriterSettings
    { Encoding = new UTF8Encoding(false), CloseOutput = false, CheckCharacters = true, NewLineHandling = NewLineHandling.Entitize });

    private static string Preview(string value) => value.Length <= 200 ? value : "⟦长文本预览；未改保留全文，编辑则替换⟧\n" + ScalarPrefix(value, 70) + "…";
    private static string ScalarPrefix(string value, int maximum)
    {
        var length = Math.Min(value.Length, maximum);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }
    private static string Overlay(string visible, string original, bool preview = true)
    {
        var expected = preview ? Preview(original) : original;
        // Older XLSX exports used XmlWriter's newline replacement. Preserve canonical data
        // when their unedited display differs only by that known CR/CRLF normalization.
        return visible == expected || visible == expected.Replace("\r\n", "\n").Replace('\r', '\n') ? original : visible;
    }

    private static void SetTaskPlacement(Project project, ProjectTask task, string groupId, string statusId)
    {
        if (task.TaskGroupId == groupId && task.StatusId == statusId) return;
        var group = project.TaskGroups.SingleOrDefault(g => g.Id == groupId) ?? throw Error("任务组 ID 不存在：" + groupId);
        var status = group.Statuses.SingleOrDefault(s => s.Id == statusId) ?? throw Error("状态 ID 不属于选定的任务组：" + statusId);
        task.TaskGroupId = groupId; task.StatusId = statusId; task.StatusBeforeClosedId = null;
        task.CompletedAt = status.Category == "done" ? task.CompletedAt ?? DateTimeOffset.UtcNow.ToString("O") : null;
    }

    private sealed class ExchangeWriteStream(Stream inner, CancellationToken token) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (written + buffer.Length > MaximumFileBytes) throw Error("导出文件超过 256 MB 上限；请分批导出项目。");
            inner.Write(buffer); written += buffer.Length;
        }
        public override void WriteByte(byte value) { Span<byte> one = stackalloc byte[1]; one[0] = value; Write(one); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        // Base Stream.Dispose does not dispose the caller-owned inner stream.
    }

    // XmlReaderSettings limits aggregate expansion; this wrapper also stops deeply nested trees
    // and oversized individual values before LINQ to XML allocates their whole subtree.
    private sealed class ExchangeXmlReader(XmlReader inner) : XmlReader
    {
        public override bool Read()
        {
            var result = inner.Read();
            if (inner.Depth > 64 || inner.Value.Length > 65536 || inner.AttributeCount > 64) throw Error("XML 层级、属性数或单个值超出安全上限。");
            for (var i = 0; i < inner.AttributeCount; i++) if (inner.GetAttribute(i).Length > 65536) throw Error("XML 属性值超出安全上限。");
            return result;
        }
        public override int AttributeCount => inner.AttributeCount;
        public override string BaseURI => inner.BaseURI;
        public override int Depth => inner.Depth;
        public override bool EOF => inner.EOF;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override string LocalName => inner.LocalName;
        public override string NamespaceURI => inner.NamespaceURI;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlNodeType NodeType => inner.NodeType;
        public override string Prefix => inner.Prefix;
        public override ReadState ReadState => inner.ReadState;
        public override string Value => inner.Value;
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override void ResolveEntity() => inner.ResolveEntity();
        public override void Close() => inner.Close();
    }
}
