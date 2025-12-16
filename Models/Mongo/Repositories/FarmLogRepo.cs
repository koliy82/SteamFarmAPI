using MongoDB.Driver;
using SteamAPI.Models.Mongo.Models;
using SteamFarmApi.Configurations;

namespace SteamAPI.Models.Mongo.Repositories
{
    public class FarmLogRepo : Repository<FarmLog>
    {
        public FarmLogRepo(IMongoDatabase db, MongoSettings settings) : base(db, settings)
        {
            Coll = db.GetCollection<FarmLog>(settings.FarmLogCollName);
        }
    }
}
