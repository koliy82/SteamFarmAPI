using MongoDB.Bson;
using System.Text.Json;

namespace SteamAPI.Utils
{
    public class ObjectConverter
    {
        public static object? ConvertJsonElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    if (el.TryGetDouble(out var d)) return d;
                    return el.GetDecimal();
                case JsonValueKind.True:
                case JsonValueKind.False: return el.GetBoolean();
                case JsonValueKind.Object:
                case JsonValueKind.Array: return BsonDocument.Parse(el.GetRawText());
                case JsonValueKind.Null: return null;
                default: return el.GetRawText();
            }
        }
    }
}
