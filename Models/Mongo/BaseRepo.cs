using MongoDB.Driver;
using SteamFarmApi.Configurations;

namespace SteamAPI.Models.Mongo
{
    public class Repository<T>(IMongoDatabase db, MongoSettings settings) where T : IEntity
    {
        public IMongoCollection<T> Coll = null!;
        public IMongoDatabase Db { get; } = db;
        public MongoSettings Settings { get; } = settings;

        public async Task<T?> FindByIdAsync(string id)
        {
            return await Coll.Find(x => x.Id == id).FirstOrDefaultAsync();
        }
    }
}
