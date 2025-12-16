using MongoDB.Driver;
using SteamAPI.Controllers;
using SteamAPI.Models.Mongo;
using SteamAPI.Models.Mongo.Repositories;
using System.Collections.Concurrent;

namespace SteamAPI.Services
{
    public class QrLoginService
    {
        private readonly QrRepo qrRepo;
        private readonly AccountRepo accRepo;
        private readonly ConcurrentDictionary<string, QrLoginSession> _active = new();

        private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(10);


        private readonly ILogger<AccountsController> logger;
        public QrLoginService(QrRepo qrRepo, AccountRepo accRepo, ILogger<AccountsController> logger)
        {
            this.qrRepo = qrRepo;
            this.accRepo = accRepo;
            this.logger = logger;
        }

        // NOTE: QR flow is implemented in `SteamSession.GenerateQrCode` and orchestrated via `SteamService.AddAccountAsync`.
        // This service kept for backward-compatibility; call SteamService.AddAccountAsync(account) to obtain `QrLoginSession`.
        public Task<QrLoginSession> GenerateQrCode()
        {
            throw new NotImplementedException("Use SteamService.AddAccountAsync which creates a SteamSession and handles QR polling.");
        }
    }
}
