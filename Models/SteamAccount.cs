using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using SteamAPI.Models.Mongo;
using SteamKit2;

namespace SteamAPI.Models
{
    public class SteamAccount(long telegramId) : IEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;
        [BsonElement("telegram_id")]
        public long TelegramId { get; set; } = telegramId;
        [BsonElement("username")]
        public string? Username { get; set; }
        [BsonElement("password")]
        public string? Password { get; set; }
        [BsonElement("refresh_token")]
        public string? RefreshToken { get; set; }
        [BsonElement("game_ids")]
        public List<object> GameIds { get; set; } = ["Famoria bot", 570];
        [BsonElement("is_farming")]
        public bool IsFarming { get; set; } = true;
        [BsonElement("persona_state")]
        public EPersonaState PersonaState { get; set; } = EPersonaState.Online;
    }
}
