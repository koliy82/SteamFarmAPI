using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using SteamAPI.Models.Sessions;

namespace SteamAPI.Models.Mongo.Models
{
    public class FarmLog : IEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;
        [BsonElement("steam_id")]
        public string SteamId { get; set; }
        [BsonElement("telegram_id")]
        public long TelegramId { get; set; }
        [BsonElement("steam_name")]

        public string? SteamName { get; set; }
        [BsonElement("state")]
        public SessionStatus State { get; set; } = SessionStatus.Unknown;
        [BsonElement("reason")]
        public string? Reason { get; set; }
        public DateTime date { get; set; } = DateTime.Now;
    }
}
