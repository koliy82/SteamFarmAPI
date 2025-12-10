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

        public long TelegramId { get; set; } = telegramId;
        public string? Username { get; set; }
        public string? Password { get; set; }

        public string? RefreshToken { get; set; }

        public List<uint> GameIds { get; set; } = [570];
        public bool IsFarming { get; set; } = true;
        public EPersonaState PersonaState { get; set; } = EPersonaState.Online;
    }
}
