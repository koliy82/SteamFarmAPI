using MongoDB.Driver;
using SteamAPI.Models.Mongo.Models;

namespace SteamAPI.Models.Mongo.Repositories
{
    public class Repository<T>(IMongoDatabase db, MongoSettings settings) where T : IEntity
    {
        public IMongoCollection<T> Coll = null!;

        public async Task<T?> FindByIdAsync(string id)
        {
            return await Coll.Find(x => x.Id == id).FirstOrDefaultAsync();
        }
    }
}
