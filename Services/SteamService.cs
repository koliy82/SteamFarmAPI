using MongoDB.Driver;
using SteamAPI.Controllers;
using SteamAPI.Models;
using SteamAPI.Models.Mongo;
using SteamKit2;
using MongoDB.Bson;
using System.Collections.Concurrent;

namespace SteamAPI.Services
{
    public partial class SteamService
    {
        public AccountRepo accRepo;
        private readonly QrRepo qrRepo;
        // Активные сессии фарма: AccountId -> Session
        private readonly ConcurrentDictionary<string, SteamSession> _activeSessions = new();

        private readonly ILogger<AccountsController> logger;
        public SteamService(AccountRepo accRepo, QrRepo qrRepo, ILogger<AccountsController> logger) {
            this.accRepo = accRepo;
            this.qrRepo = qrRepo;
            this.logger = logger;
        }

        // Запуск фарма для аккаунта из БД
        public async Task StartFarmingAsync(string accountId)
        {
            var account = await accRepo.FindByIdAsync(accountId);
            if (account == null || string.IsNullOrEmpty(account.RefreshToken)) return;

            if (_activeSessions.ContainsKey(accountId)) return;

            // create session without onAuthenticated because it's already known
            var session = new SteamSession(account, logger, accRepo, qrRepo);
            _activeSessions[accountId] = session;
            session.Start();

            // Обновляем статус в БД
            var update = Builders<SteamAccount>.Update.Set(x => x.IsFarming, true);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);
        }

        public async Task InitialStart()
        {
            var accounts = await accRepo.Coll.Find(_ => true).ToListAsync();
            foreach (var account in accounts)
            {
                var session = new SteamSession(account, logger, accRepo, qrRepo);
                _activeSessions.TryAdd(account.Id, session);
                if (account.IsFarming)
                {
                    session.Start();
                }
            }
        }

        public async Task StopFarmingAsync(string accountId)
        {
            if (_activeSessions.TryGetValue(accountId, out var session))
            {
                session.Stop();
            }

            var update = Builders<SteamAccount>.Update.Set(x => x.IsFarming, false);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);
        }

        public async Task UpdateGamesAsync(string accountId, List<object> games)
        {
            var update = Builders<SteamAccount>.Update.Set(x => x.GameIds, games);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);

            if (_activeSessions.TryGetValue(accountId, out var session))
            {
                session.UpdateGames(games);
            }
        }

        public async Task UpdateStatusAsync(string accountId, EPersonaState status)
        {
            var update = Builders<SteamAccount>.Update.Set(x => x.PersonaState, status);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);

            if (_activeSessions.TryGetValue(accountId, out var session))
            {
                session.UpdateStatus(status);
            }
        }
        public async Task<AuthDTO> AddAccountAsync(SteamAccount account)
        {
            // Do not insert account into DB until successful QR authentication.
            // Ensure an Id exists so the session can upsert the DB record on successful auth.
            if (string.IsNullOrEmpty(account.Id))
            {
                account.Id = ObjectId.GenerateNewId().ToString();
            }

            // create session and provide onAuthenticated callback so it will be added to active sessions
            var newSession = new SteamSession(account, logger, accRepo, qrRepo: qrRepo, onAuthenticated: s => {
                _activeSessions[account.Id] = s;
            });

            var qrCode = await newSession.GenerateQrCode();
            // session will be added to _activeSessions when pollResponse arrives and onAuthenticated callback is invoked
            return new AuthDTO{
                Account = account,
                QrSession = qrCode
            };
        }
        public async Task DeleteAccountAsync(string accountId)
        {
            if (_activeSessions.TryRemove(accountId, out var session))
            {
                session.Stop();
            }
            await StopFarmingAsync(accountId);
            await accRepo.Coll.DeleteOneAsync(x => x.Id == accountId);
        }
    }
}
