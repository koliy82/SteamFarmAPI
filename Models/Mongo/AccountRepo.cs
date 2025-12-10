using MongoDB.Driver;
using SteamFarmApi.Configurations;

namespace SteamAPI.Models.Mongo
{
    public class AccountRepo : Repository<SteamAccount>
    {
        public AccountRepo(IMongoDatabase db, MongoSettings settings) : base(db, settings)
        {
            Coll = db.GetCollection<SteamAccount>(settings.SteamAccountsCollName);
        }
    }
}
