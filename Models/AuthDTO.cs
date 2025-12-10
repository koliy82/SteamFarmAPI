using SteamAPI.Models;
using SteamAPI.Models.Mongo;

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
