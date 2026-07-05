using System.Text.Json;

namespace RuleForge.Studio.Core.Authoring;

public enum SchemaFieldType { String, Number, Boolean, Array, Object, Unknown }

/// <summary>A selectable field from a rule's request schema: its JSONPath and type.</summary>
public sealed record SchemaField(string Name, string Path, SchemaFieldType Type)
{
    public string Display => $"{Path}   ·   {Type.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Turns a rule's <c>inputSchema</c> (JSON Schema) into a flat list of pickable fields — the data
/// behind the schema-aware field picker. Top-level properties plus one level into array-of-object
/// items (e.g. <c>$.pax[*].type</c>).
/// </summary>
public static class SchemaFields
{
    public static IReadOnlyList<SchemaField> FromInputSchema(JsonElement schema)
    {
        var list = new List<SchemaField>();
        if (schema.ValueKind != JsonValueKind.Object) return list;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return list;

        foreach (var p in props.EnumerateObject())
        {
            var type = TypeOf(p.Value);
            list.Add(new SchemaField(p.Name, "$." + p.Name, type));

            if (type == SchemaFieldType.Array
                && p.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("properties", out var itemProps) && itemProps.ValueKind == JsonValueKind.Object)
            {
                foreach (var ip in itemProps.EnumerateObject())
                    list.Add(new SchemaField($"{p.Name}[].{ip.Name}", $"$.{p.Name}[*].{ip.Name}", TypeOf(ip.Value)));
            }
        }
        return list;
    }

    private static SchemaFieldType TypeOf(JsonElement fieldSchema)
    {
        if (fieldSchema.ValueKind != JsonValueKind.Object || !fieldSchema.TryGetProperty("type", out var t))
            return SchemaFieldType.Unknown;

        var s = t.ValueKind switch
        {
            JsonValueKind.String => t.GetString(),
            JsonValueKind.Array when t.GetArrayLength() > 0 => t[0].GetString(),
            _ => null,
        };

        return s switch
        {
            "string" => SchemaFieldType.String,
            "integer" or "number" => SchemaFieldType.Number,
            "boolean" => SchemaFieldType.Boolean,
            "array" => SchemaFieldType.Array,
            "object" => SchemaFieldType.Object,
            _ => SchemaFieldType.Unknown,
        };
    }
}
