using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace PureProject.Core;

/// <summary>
/// A conservative retention weight, not a measurement of process private bytes.
/// Includes UTF-16 text, collection entries and extension payloads. Immutable
/// published versions can cache it without keeping those versions alive.
/// </summary>
public static class ModelMemoryEstimate
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static long ForProject(Project project)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Count(project);

        long Count(object? value)
        {
            if (value is null) return 0;
            if (value is string text) return visited.Add(text) ? Align(32L + text.Length * 2L) : 0;
            if (value is JsonElement json)
                return json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? 0 : 128L + json.GetRawText().Length * 4L;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal) return 16;
            if (!type.IsValueType && !visited.Add(value)) return 0;
            if (value is IDictionary dictionary)
            {
                long bytes = 64L + dictionary.Count * 64L;
                foreach (DictionaryEntry entry in dictionary) bytes += Count(entry.Key) + Count(entry.Value);
                return bytes;
            }
            if (value is IEnumerable sequence)
            {
                long bytes = 64;
                foreach (var item in sequence) bytes += 16 + Count(item);
                return bytes;
            }
            var properties = Properties.GetOrAdd(type, key => key.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0).ToArray());
            long total = 64L + properties.Length * 8L;
            foreach (var property in properties) total += Count(property.GetValue(value));
            return total;
        }
    }

    private static long Align(long bytes) => (bytes + 7) & ~7L;
}
