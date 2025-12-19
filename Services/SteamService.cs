using MongoDB.Bson;
using MongoDB.Driver;
using SteamAPI.Controllers;
using SteamAPI.Models.Mongo;
using SteamAPI.Models.Mongo.Models;
using SteamAPI.Models.Mongo.Repositories;
using SteamAPI.Models.Sessions;
using SteamAPI.Utils;
using SteamKit2;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamAPI.Services
{
    public partial class SteamService(AccountRepo accRepo, QrRepo qrRepo, FarmLogRepo farmRepo, ILogger<AccountsController> logger)
    {
        public AccountRepo accRepo = accRepo;
        private readonly QrRepo qrRepo = qrRepo;
        private readonly FarmLogRepo farmRepo = farmRepo;
        // Активные сессии фарма: AccountId -> Session
        private readonly ConcurrentDictionary<string, SteamSession> _activeSessions = new();

        private readonly ILogger<AccountsController> logger = logger;

        // Запуск фарма для аккаунта из БД
        public async Task StartFarmingAsync(string accountId)
        {
            var account = await accRepo.FindByIdAsync(accountId);
            if (account == null || string.IsNullOrEmpty(account.RefreshToken)) return;
            var session = _activeSessions[accountId];
            if (session == null)
            {
                session = new SteamSession(account, logger, accRepo, qrRepo, farmRepo);
                _activeSessions[accountId] = session;
            }
            session.accountData.IsFarming = true;
            await session.Start();

            // Обновляем статус в БД
            var update = Builders<SteamAccount>.Update.Set(x => x.IsFarming, true);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);
        }

        public async Task InitialStart()
        {
            var accounts = await accRepo.Coll.Find(_ => true).ToListAsync();
            foreach (var account in accounts)
            {
                var session = new SteamSession(account, logger, accRepo, qrRepo, farmRepo);
                _activeSessions.TryAdd(account.Id, session);
                if (account.IsFarming)
                {
                    session.Init();
                }
            }
        }

        public async Task StopFarmingAsync(string accountId)
        {
            if (_activeSessions.TryGetValue(accountId, out var session))
            {
                await session.Stop(LogReason.UserStop);
            }
            var update = Builders<SteamAccount>.Update.Set(x => x.IsFarming, false);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);
        }

        public async Task UpdateGamesAsync(string accountId, List<object> games)
        {
            var converted = games.Select(g => g is JsonElement je ? ObjectConverter.ConvertJsonElement(je) : g).ToList();
            if (converted == null || converted.Any(g => g == null))
            {
                logger.LogWarning("Failed to convert some game IDs for account {AccountId}", accountId);
            }
            var update = Builders<SteamAccount>.Update.Set(x => x.GameIds, converted!);
            await accRepo.Coll.UpdateOneAsync(x => x.Id == accountId, update);

            if (_activeSessions.TryGetValue(accountId, out var session))
            {
                await session.UpdateGames(converted!);
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
            var newSession = new SteamSession(account, logger, accRepo, qrRepo: qrRepo, farmRepo, onAuthenticated: s => {
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
                await session.Delete();
            }
            await StopFarmingAsync(accountId);
            await accRepo.Coll.DeleteOneAsync(x => x.Id == accountId);
        }
    }
}
