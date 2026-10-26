using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Core.Json;

/// <summary>
/// 把 <c>arguments.game</c> 中“字符串或对象”的混合数组统一读成 <see cref="Models.ArgumentEntry"/>。
/// </summary>
public sealed class ArgumentEntryConverter : JsonConverter<Models.ArgumentEntry>
{
    public override Models.ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new Models.ArgumentEntry { Value = reader.GetString() };
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var entry = new Models.ArgumentEntry();
        if (root.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                entry.Value = value.GetString();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                entry.Values = value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }

        if (root.TryGetProperty("rules", out var rules))
        {
            entry.Rules = rules.Deserialize<List<Models.Rule>>(options);
        }

        return entry;
    }

    public override void Write(Utf8JsonWriter writer, Models.ArgumentEntry value, JsonSerializerOptions options)
    {
        if (value.Rules is null && value.Values is null)
        {
            writer.WriteStringValue(value.Value);
            return;
        }

        writer.WriteStartObject();
        if (value.Value is not null)
        {
            writer.WriteString("value", value.Value);
        }
        else if (value.Values is not null)
        {
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value.Values, options);
        }

        if (value.Rules is not null)
        {
            writer.WritePropertyName("rules");
            JsonSerializer.Serialize(writer, value.Rules, options);
        }

        writer.WriteEndObject();
    }
}
