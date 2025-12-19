using System.Reflection;
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
        public string SteamId { get; set; } = string.Empty;
        [BsonElement("telegram_id")]
        public long TelegramId { get; set; } = 0;
        [BsonElement("steam_name")]

        public string? SteamName { get; set; }
        [BsonElement("state")]
        public SessionStatus State { get; set; } = SessionStatus.Unknown;
        [BsonElement("reason")]
        public LogReason Reason { get; set; }
        [BsonElement("date")]
        public DateTime Date { get; set; } = DateTime.Now;
    }
    
    public enum LogReason
    {
        GamesSend,
        UserStop,
        UserDelete,
        AuthError,
        ConnectionError,
        UnknownError,
        LoggedInElsewhere,
    }
}


