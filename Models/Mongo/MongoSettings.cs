namespace SteamAPI.Models.Mongo
{
    public class MongoSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string SteamAccountsCollName { get; set; } = string.Empty;
        public string FarmLogCollName {  get; set; } = string.Empty;
        public string QrCollName { get; set; } = string.Empty;
    }
}