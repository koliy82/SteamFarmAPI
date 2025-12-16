using MongoDB.Driver;
using SteamAPI.Models.Mongo.Repositories;
using SteamFarmApi.Configurations;

namespace SteamAPI.Models.Mongo
{
    public class QrRepo : Repository<QrLoginSession>
    {
        public QrRepo(IMongoDatabase db, MongoSettings settings) : base(db, settings)
        {
            Coll = db.GetCollection<QrLoginSession>(settings.QrCollName);
        }
    }
}
