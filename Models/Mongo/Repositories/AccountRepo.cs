using MongoDB.Driver;
using SteamAPI.Models.Mongo.Models;

namespace SteamAPI.Models.Mongo.Repositories
{
    public class AccountRepo : Repository<SteamAccount>
    {
        public AccountRepo(IMongoDatabase db, MongoSettings settings) : base(db, settings)
        {
            Coll = db.GetCollection<SteamAccount>(settings.SteamAccountsCollName);
        }
    }
}
