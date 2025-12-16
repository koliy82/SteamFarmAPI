using SteamAPI.Models.Mongo;
using SteamAPI.Models.Mongo.Models;

namespace SteamAPI.Services
{
    public partial class SteamService
    {
        public class AuthDTO {
            public SteamAccount Account { get; set; }
            public QrLoginSession QrSession { get; set; }
        }
    }
}
