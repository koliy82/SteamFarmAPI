using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using SteamAPI.Models.Mongo.Models;

namespace SteamAPI.Models.Mongo
{
    public class QrLoginSession(string steamId): IEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;
        public string SteamId { get; set; } = steamId;

        public string? Username { get; set; }
        public string ChallengeUrl { get; set; } = "";
        public string Status { get; set; } = "waiting";
        public string? RefreshToken { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
