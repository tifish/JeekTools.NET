using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace JeekTools;

public sealed class ObjectGraphOptions
{
    /// <summary>Resolves a root name (the first path segment) to a live object.
    /// Throw with a helpful message for unknown roots.</summary>
    public required Func<string, object> ResolveRoot { get; init; }

    /// <summary>Short list of root names for parse errors, e.g. "App, MainWindow".</summary>
    public required string RootNamesHelp { get; init; }

    /// <summary>Handles '#Name' segments, e.g. finding a named control below a
    /// UI element. Return null when not found; leave unset if unsupported.</summary>
    public Func<object, string, object?>? FindNamedChild { get; init; }
}

/// <summary>
/// Reflection-based object-path engine for debug tooling: resolves paths like
/// "Root.Member[0][\"key\"].#Name" through properties, fields (non-public
/// included), list indexes, dictionary keys, and an optional named-child hook;
/// reads, writes, and invokes members; converts JSON arguments (including
/// {"$path": ...} live-object references) and formats results as JSON.
/// </summary>
public sealed class ObjectGraph(ObjectGraphOptions options)
{
    private static readonly JsonSerializerOptions ConvertOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        // MCP clients often send every scalar as a string; accept "42" for int etc.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private abstract record Segment;

    private sealed record MemberSegment(string Name) : Segment;

    private sealed record FindChildSegment(string Name) : Segment;

    private sealed record IndexSegment(int Index) : Segment;

    private sealed record KeySegment(string Key) : Segment;

    public object? Resolve(string path)
    {
        var segments = ParsePath(path);
        return ResolveSegments(segments, segments.Count);
    }

    /// <summary>Sets a property, field, or list element addressed by the path.</summary>
    public void SetValue(string path, JsonNode? valueNode)
    {
        var segments = ParsePath(path);
        if (segments.Count < 2)
            throw new InvalidOperationException("set_value needs a path with at least one member after the root.");

        var parent = ResolveSegments(segments, segments.Count - 1)
                     ?? throw new InvalidOperationException("The object owning the target member is null.");

        switch (segments[^1])
        {
            case MemberSegment member:
            {
                var type = parent.GetType();
                var property = FindProperty(type, member.Name);
                if (property != null)
                {
                    if (!property.CanWrite)
                        throw new InvalidOperationException($"Property '{member.Name}' on {type.Name} is read-only.");
                    property.SetValue(parent, ConvertJson(valueNode, property.PropertyType));
                    return;
                }

                var field = FindField(type, member.Name)
                            ?? throw new InvalidOperationException(
                                $"No property or field '{member.Name}' on {type.Name}. Use list_members to inspect.");
                field.SetValue(parent, ConvertJson(valueNode, field.FieldType));
                return;
            }
            case IndexSegment index when parent is IList list:
                list[index.Index] = ConvertJson(valueNode, ListElementType(parent.GetType()));
                return;
            default:
                throw new InvalidOperationException("set_value requires the path to end with a property, field, or list index.");
        }
    }

    /// <summary>Executes an ICommand property or calls a method addressed by the
    /// path. Returns the raw result (a Task is not awaited here) or a status
    /// string for command execution.</summary>
    public object? InvokeMember(string path, JsonArray callArgs)
    {
        var segments = ParsePath(path);
        if (segments.Count < 2 || segments[^1] is not MemberSegment member)
            throw new InvalidOperationException("invoke requires a path ending with a command property or method name.");

        var target = ResolveSegments(segments, segments.Count - 1)
                     ?? throw new InvalidOperationException("The object owning the member is null.");
        var type = target.GetType();

        var property = FindProperty(type, member.Name);
        if (property != null && typeof(ICommand).IsAssignableFrom(property.PropertyType))
        {
            var command = (ICommand?)property.GetValue(target)
                          ?? throw new InvalidOperationException($"Command '{member.Name}' is null.");
            var parameter = callArgs.Count > 0 ? ConvertJson(callArgs[0], typeof(object)) : null;
            if (!command.CanExecute(parameter))
                return "CanExecute returned false; command not executed.";
            command.Execute(parameter);
            return "Command executed.";
        }

        var candidates = new List<MethodInfo>();
        for (var t = type; t != null; t = t.BaseType)
        {
            candidates.AddRange(t
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == member.Name && m.GetParameters().Length == callArgs.Count));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No command or method '{member.Name}' taking {callArgs.Count} argument(s) on {type.Name}. Use list_members to inspect.");

        Exception? conversionError = null;
        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            var converted = new object?[parameters.Length];
            try
            {
                for (var i = 0; i < parameters.Length; i++)
                    converted[i] = ConvertJson(callArgs[i], parameters[i].ParameterType);
            }
            catch (Exception ex)
            {
                conversionError = ex;
                continue;
            }

            return method.Invoke(target, converted);
        }

        throw new InvalidOperationException($"Could not convert arguments for '{member.Name}'.", conversionError);
    }

    /// <summary>Text listing of public properties, fields, and methods at a path.</summary>
    public string ListMembers(string path)
    {
        var target = Resolve(path) ?? throw new InvalidOperationException($"'{path}' is null.");
        var type = target.GetType();
        var sb = new StringBuilder();
        sb.AppendLine(type.FullName);

        sb.AppendLine();
        sb.AppendLine("Properties:");
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            string value;
            try
            {
                value = Summary(property.GetValue(target));
            }
            catch (Exception ex)
            {
                value = $"<threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}>";
            }

            var access = property.CanWrite ? "get/set" : "get";
            sb.AppendLine($"- {property.Name}: {TypeName(property.PropertyType)} ({access}) = {value}");
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(f => f.Name)
            .ToList();
        if (fields.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fields:");
            foreach (var field in fields)
                sb.AppendLine($"- {field.Name}: {TypeName(field.FieldType)} = {Summary(field.GetValue(target))}");
        }

        sb.AppendLine();
        sb.AppendLine("Methods:");
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .OrderBy(m => m.Name)
            .Take(300);
        foreach (var method in methods)
        {
            var parameters = string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));
            sb.AppendLine($"- {method.Name}({parameters}): {TypeName(method.ReturnType)}");
        }

        return sb.ToString();
    }

    private object? ResolveSegments(List<Segment> segments, int count)
    {
        object? current = null;
        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (i == 0)
            {
                current = options.ResolveRoot(((MemberSegment)segment).Name);
                continue;
            }

            if (current == null)
                throw new InvalidOperationException($"Path is null before '{DescribeSegment(segment)}'.");
            current = Step(current, segment);
        }

        return current;
    }

    private object? Step(object target, Segment segment)
    {
        switch (segment)
        {
            case MemberSegment member:
            {
                var type = target.GetType();
                var property = FindProperty(type, member.Name);
                if (property != null)
                    return property.GetValue(target);
                var field = FindField(type, member.Name);
                if (field != null)
                    return field.GetValue(target);
                throw new InvalidOperationException(
                    $"No property or field '{member.Name}' on {type.Name}. Use list_members to inspect.");
            }
            case IndexSegment index:
                return target switch
                {
                    IList list when index.Index >= 0 && index.Index < list.Count => list[index.Index],
                    IList list => throw new InvalidOperationException($"Index {index.Index} out of range (Count={list.Count})."),
                    IEnumerable enumerable => enumerable.Cast<object?>().ElementAt(index.Index),
                    _ => throw new InvalidOperationException($"{target.GetType().Name} is not indexable."),
                };
            case KeySegment key:
                return target switch
                {
                    IDictionary dictionary when dictionary.Contains(key.Key) => dictionary[key.Key],
                    IDictionary => throw new InvalidOperationException($"Key '{key.Key}' not found."),
                    _ => throw new InvalidOperationException($"{target.GetType().Name} is not a dictionary."),
                };
            case FindChildSegment find:
            {
                if (options.FindNamedChild is null)
                    throw new InvalidOperationException("'#Name' segments are not supported here.");
                return options.FindNamedChild(target, find.Name)
                       ?? throw new InvalidOperationException(
                           $"No child named '{find.Name}' under {target.GetType().Name}.");
            }
            default:
                throw new InvalidOperationException("Unknown path segment.");
        }
    }

    private List<Segment> ParsePath(string path)
    {
        var segments = new List<Segment>();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
            }
            else if (c == '#')
            {
                i++;
                var start = i;
                while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_'))
                    i++;
                if (i == start)
                    throw new FormatException($"Expected a control name after '#' at position {start} in '{path}'.");
                segments.Add(new FindChildSegment(path[start..i]));
            }
            else if (c == '[')
            {
                i++;
                if (i < path.Length && path[i] is '"' or '\'')
                {
                    var quote = path[i++];
                    var start = i;
                    while (i < path.Length && path[i] != quote)
                        i++;
                    if (i >= path.Length)
                        throw new FormatException($"Unterminated string indexer in '{path}'.");
                    segments.Add(new KeySegment(path[start..i]));
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < path.Length && path[i] != ']')
                        i++;
                    if (!int.TryParse(path[start..i].Trim(), out var index))
                        throw new FormatException($"Invalid index '{path[start..i]}' in '{path}'.");
                    segments.Add(new IndexSegment(index));
                }

                if (i >= path.Length || path[i] != ']')
                    throw new FormatException($"Expected ']' in '{path}'.");
                i++;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_'))
                    i++;
                segments.Add(new MemberSegment(path[start..i]));
            }
            else
            {
                throw new FormatException($"Unexpected character '{c}' at position {i} in '{path}'.");
            }
        }

        if (segments.Count == 0 || segments[0] is not MemberSegment)
            throw new FormatException($"Path must start with a root name ({options.RootNamesHelp}).");

        return segments;
    }

    private static string DescribeSegment(Segment segment) => segment switch
    {
        MemberSegment member => "." + member.Name,
        FindChildSegment find => "#" + find.Name,
        IndexSegment index => $"[{index.Index}]",
        KeySegment key => $"[\"{key.Key}\"]",
        _ => "?",
    };

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
        }

        return null;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
        }

        return null;
    }

    private static Type ListElementType(Type type)
    {
        var listInterface = type.GetInterfaces()
            .Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return listInterface?.GetGenericArguments()[0] ?? typeof(object);
    }

    public object? ConvertJson(JsonNode? node, Type targetType)
    {
        if (node == null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                throw new InvalidOperationException($"Cannot assign null to {TypeName(targetType)}.");
            return null;
        }

        // Some clients deliver structured values as JSON strings (same quirk as
        // the string-encoded numbers/bools below); unwrap a string-encoded
        // $path reference so it hits the branch that follows.
        if (node is JsonValue stringValue
            && stringValue.TryGetValue<string>(out var rawText)
            && rawText.TrimStart().StartsWith('{')
            && rawText.Contains("$path", StringComparison.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(rawText) is JsonObject parsed
                    && parsed.Count == 1
                    && parsed.ContainsKey("$path"))
                {
                    node = parsed;
                }
            }
            catch (JsonException)
            {
                // Not actually JSON — leave the string value untouched.
            }
        }

        // {"$path": "Root.Member[0]"} passes the live object at that path, so
        // setters and methods can receive real instances from the object graph
        // (references cannot round-trip through JSON). Runs on the UI thread,
        // like every ConvertJson call site.
        if (node is JsonObject pathReference
            && pathReference.Count == 1
            && pathReference.TryGetPropertyValue("$path", out var pathNode)
            && pathNode is JsonValue pathValue
            && pathValue.TryGetValue<string>(out var referencePath))
        {
            var resolved = Resolve(referencePath);
            if (resolved is null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    throw new InvalidOperationException($"Cannot assign null to {TypeName(targetType)}.");
                return null;
            }

            if (!targetType.IsInstanceOfType(resolved))
            {
                throw new InvalidOperationException(
                    $"'$path' resolved to {TypeName(resolved.GetType())}, which is not assignable to {TypeName(targetType)}.");
            }

            return resolved;
        }

        if (targetType == typeof(object))
        {
            return node switch
            {
                JsonValue value when value.TryGetValue<bool>(out var b) => b,
                JsonValue value when value.TryGetValue<long>(out var l) => l,
                JsonValue value when value.TryGetValue<double>(out var d) => d,
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                _ => node.Deserialize<object>(ConvertOptions),
            };
        }

        // Same string-leniency for bools ("true"), which NumberHandling doesn't cover.
        var boolTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (boolTarget == typeof(bool) && node is JsonValue v && v.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsedBool))
            return parsedBool;

        return node.Deserialize(targetType, ConvertOptions);
    }

    public static JsonNode? FormatValue(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b;
            case sbyte or byte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value));
            case ulong ul:
                return JsonValue.Create(ul);
            case float f:
                return float.IsFinite(f) ? JsonValue.Create(f) : f.ToString();
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : d.ToString();
            case decimal m:
                return JsonValue.Create(m);
            case char c:
                return c.ToString();
        }

        var type = value.GetType();
        if (type.IsEnum || value is DateTime or DateTimeOffset or TimeSpan or Guid or Uri or Type or Delegate)
            return value.ToString();
        if (type.IsValueType)
            return value.ToString(); // Rect, Size, Thickness, Color, ... read well as strings.

        if (depth <= 0)
            return Summary(value);

        switch (value)
        {
            case IDictionary dictionary:
            {
                var result = new JsonObject();
                var i = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (i++ >= 50)
                    {
                        result["…"] = $"truncated; {dictionary.Count} entries total";
                        break;
                    }

                    result[entry.Key?.ToString() ?? "null"] = FormatValue(entry.Value, depth - 1);
                }

                return result;
            }
            case IEnumerable enumerable:
            {
                var result = new JsonArray();
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i++ >= 50)
                    {
                        result.Add("… truncated at 50 items");
                        break;
                    }

                    result.Add(FormatValue(item, depth - 1));
                }

                return result;
            }
        }

        var obj = new JsonObject { ["$type"] = TypeName(type) };
        var count = 0;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (count++ >= 80)
            {
                obj["…"] = "more properties omitted";
                break;
            }

            try
            {
                obj[property.Name] = FormatValue(property.GetValue(value), depth - 1);
            }
            catch (Exception ex)
            {
                obj[property.Name] = $"<threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}>";
            }
        }

        return obj;
    }

    public static string Summary(object? value)
    {
        if (value == null)
            return "null";

        var type = value.GetType();
        if (value is string s)
            return $"\"{Truncate(s, 120)}\"";
        if (type.IsPrimitive || type.IsEnum || type.IsValueType || value is decimal)
            return value.ToString() ?? "";
        if (value is ICollection collection)
            return $"{TypeName(type)} (Count={collection.Count})";

        var text = value.ToString();
        return text == null || text == type.ToString()
            ? TypeName(type)
            : $"{TypeName(type)} \"{Truncate(text, 120)}\"";
    }

    public static string TypeName(Type type)
    {
        if (type == typeof(void))
            return "void";
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return TypeName(underlying) + "?";
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
