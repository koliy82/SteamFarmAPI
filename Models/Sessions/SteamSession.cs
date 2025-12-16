using SteamAPI.Controllers;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamAPI.Models.Mongo;
using MongoDB.Driver;
using SteamAPI.Models.Mongo.Repositories;
using SteamAPI.Models.Mongo.Models;

namespace SteamAPI.Models.Sessions
{
    public class SteamSession
    {
        private SteamClient _steamClient;
        private CallbackManager _manager;
        private SteamUser? _steamUser;
        public SteamAccount accountData;
        private bool _isRunning;
        private CancellationTokenSource? _cts;

        public SteamFriends? _steamFriends;
        public SessionStatus status = SessionStatus.Unknown;
        private readonly ILogger<AccountsController> logger;
        private readonly AccountRepo _accRepo;
        private readonly QrRepo _qrRepo;
        private readonly FarmLogRepo _logRepo;
        private readonly Action<SteamSession>? _onAuthenticated;

        public SteamSession(SteamAccount account, ILogger<AccountsController> logger, AccountRepo accRepo, QrRepo qrRepo, FarmLogRepo logRepo, Action<SteamSession>? onAuthenticated = null)
        {
            this.logger = logger;
            accountData = account;
            _accRepo = accRepo;
            _qrRepo = qrRepo;
            _logRepo = logRepo;
            _onAuthenticated = onAuthenticated;
            _steamClient = new SteamClient();
            _manager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            if (_steamUser == null) {
                throw new ArgumentNullException($"Account {account.Username} SteamUser handler is null");
            }
            _steamFriends = _steamClient.GetHandler<SteamFriends>();
            if (_steamFriends == null)
            {
                throw new ArgumentNullException($"Account {account.Username} SteamFriend handler is null");
            }
            _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            Init();
        }

        public void Init()
        {
            if (_isRunning) return;
            _steamClient.Connect();
            _isRunning = true;
            _cts = new CancellationTokenSource();
            logger.LogInformation($"[{accountData.Username}] Connecting...");
            
            Task.Run(() =>
            {
                while (_isRunning && !_cts.Token.IsCancellationRequested)
                {
                    _manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
            }, _cts.Token);
        }

        public async Task Start()
        {
            if (_steamClient.IsConnected && accountData.IsFarming)
            {
                await SendGamesPlayed();
            }
        }

        public async Task Stop()
        {
            if (_steamClient.IsConnected)
            {
                var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
                _steamClient.Send(playGame);
                logger.LogInformation($"[{accountData.Username}] Farming stopped for games: {string.Join(",", accountData.GameIds)}");
                await _logRepo.Coll.InsertOneAsync(new FarmLog
                {
                    Reason = "User stop",
                    State = status,
                    SteamId = accountData.Id,
                    SteamName = accountData.Username,
                    TelegramId = accountData.TelegramId,
                });
            }
        }

        public async Task Delete()
        {
            status = SessionStatus.Deleted;
            await Stop();
            _isRunning = false;
            _cts?.Cancel();
            _steamUser?.LogOff();
            _steamClient.Disconnect();
        }

        public async Task UpdateGames(List<object> gameIds)
        {
            accountData.GameIds = gameIds;
            await Start();
        }

        public void UpdateStatus(EPersonaState state)
        {
            accountData.PersonaState = state;
            if (_steamClient.IsConnected)
            {
                _steamFriends?.SetPersonaState(accountData.PersonaState);
            }
        }

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            logger.LogInformation($"Steam server for {accountData.Username} Connected. Logging in...");
            LogIn();
        }

        private void LogIn()
        {
            logger.LogInformation($"{accountData.Username} logging in try...");
            if (status == SessionStatus.Active) { return; }
            if (accountData.RefreshToken == null)
            {
                logger.LogInformation($"{accountData.Username} Refresh token is null, need auth.");
                status = SessionStatus.NeedAuth;
                return;
            }

            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountData.Username,
                AccessToken = accountData.RefreshToken,
                ShouldRememberPassword = true
            });
        }

        async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                logger.LogInformation($"[{accountData.Username}] Logon failed: {callback.Result}");
                return;
            }

            logger.LogInformation($"[{accountData.Username}] Successfully logged on!");

            _steamFriends?.SetPersonaState(accountData.PersonaState);

            status = SessionStatus.Active;
            if (accountData.GameIds.Count > 0)
            {
                await Start();
            }
        }

        private async Task SendGamesPlayed()
        {
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
            foreach (var gameId in accountData.GameIds)
            {
                if (gameId is string gameName)
                {
                    playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = 15190414816125648896,
                        game_extra_info = gameName,
                    });
                    continue;
                }
                if (gameId is ulong ul)
                {
                    playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = ul });
                    continue;
                }
                try
                {
                    playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = Convert.ToUInt64(gameId),
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"[{accountData.Username}] Failed to add game ID: {gameId}");
                }
            }
            _steamClient.Send(playGame);
            await _logRepo.Coll.InsertOneAsync(new FarmLog
            {
                Reason = "Send games",
                State = status,
                SteamId = accountData.Id,
                SteamName = accountData.Username,
                TelegramId = accountData.TelegramId
            });
            logger.LogInformation($"[{accountData.Username}] Farming started for games: {string.Join(",", accountData.GameIds)}");
        }

        async void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            logger.LogInformation($"[{accountData.Username}] Disconnected.");
            if (_isRunning)
            {
                await Task.Delay(10000).ContinueWith(_ => _steamClient.Connect());
            }
        }

        async void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            logger.LogInformation($"[{accountData.Username}] Logged off: {callback.Result}");
            status = SessionStatus.NeedAuth;
        }

        public async Task<QrLoginSession> GenerateQrCode()
        {
            
            while (_steamClient.IsConnected == false)
            {
                logger.LogWarning("Wait connect...");
                await Task.Delay(1000);
            }
            logger.LogDebug("Connected, try get qr code...");
            try
            {
                var authSession = await _steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                // authSession.ChallengeURL — строка, которую нужно показать в QR
                var challenge = authSession.ChallengeURL;
                logger.LogDebug(challenge);

                var newQrSession = new QrLoginSession(accountData.Id)
                {
                    CreatedAt = DateTime.UtcNow,
                    ChallengeUrl = challenge,
                    Status = "waiting"
                };

                // save to mongo (if repo available) so client can poll status by id
                if (_qrRepo != null)
                {
                    try
                    {
                        await _qrRepo.Coll.InsertOneAsync(newQrSession);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to insert QrLoginSession into MongoDB");
                    }
                }

                // После получения автоматически стартуем background task ожидания результата.
                // Если poll завершится — обновим DB.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var pollTask = authSession.PollingWaitForResultAsync();
                        var timeout = TimeSpan.FromMinutes(10);

                        var completed = await Task.WhenAny(pollTask, Task.Delay(timeout));
                        if (completed != pollTask)
                        {
                            // timeout
                            logger.LogInformation($"QR poll timeout for session {newQrSession.Id}");
                            if (_qrRepo != null)
                            {
                                var updateExpired = Builders<QrLoginSession>.Update
                                    .Set(x => x.Status, "expired");
                                try
                                {
                                    await _qrRepo.Coll.UpdateOneAsync(x => x.Id == newQrSession.Id, updateExpired);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Failed to mark QrLoginSession as expired");
                                }
                            }

                            // no Cancel method available on authSession; just return
                            return;
                        }

                        var pollResponse = await pollTask;

                        // update account data in memory and in mongo (upsert)
                        accountData.Username = pollResponse.AccountName;
                        accountData.RefreshToken = pollResponse.RefreshToken;

                        var update = Builders<SteamAccount>.Update
                            .Set(x => x.Username, accountData.Username)
                            .Set(x => x.RefreshToken, accountData.RefreshToken)
                            .SetOnInsert(x => x.TelegramId, accountData.TelegramId)
                            .SetOnInsert(x => x.GameIds, accountData.GameIds)
                            .SetOnInsert(x => x.IsFarming, accountData.IsFarming)
                            .SetOnInsert(x => x.PersonaState, accountData.PersonaState);

                        var filter = Builders<SteamAccount>.Filter.Eq(x => x.Id, accountData.Id);
                        var options = new UpdateOptions { IsUpsert = true };

                        try
                        {
                            await _accRepo.Coll.UpdateOneAsync(filter, update, options);
                            logger.LogInformation($"[{accountData.Id}] Account upserted in MongoDB with username: {accountData.Username}");
                        }
                        catch (Exception dbEx)
                        {
                            logger.LogError(dbEx, "Failed to upsert account in MongoDB");
                        }

                        // update qr session document as completed
                        if (_qrRepo != null)
                        {
                            var qrUpdate = Builders<QrLoginSession>.Update
                                .Set(x => x.Status, "completed")
                                .Set(x => x.Username, accountData.Username)
                                .Set(x => x.RefreshToken, accountData.RefreshToken);

                            try
                            {
                                await _qrRepo.Coll.UpdateOneAsync(x => x.Id == newQrSession.Id, qrUpdate);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed to update QrLoginSession as completed");
                            }
                        }

                        // Try to login with new credentials
                        LogIn();

                        // Notify owner service that this session is now authenticated (so it can be added to active sessions)
                        try
                        {
                            _onAuthenticated?.Invoke(this);
                        }
                        catch (Exception cbEx)
                        {
                            logger.LogError(cbEx, "onAuthenticated callback failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogInformation(ex.ToString());
                        // mark qr session as error
                        if (_qrRepo != null)
                        {
                            var qrUpdate = Builders<QrLoginSession>.Update
                                .Set(x => x.Status, $"error: {ex.Message}");
                            try
                            {
                                await _qrRepo.Coll.UpdateOneAsync(x => x.Id == newQrSession.Id, qrUpdate);
                            }
                            catch (Exception innerEx)
                            {
                                logger.LogError(innerEx, "Failed to mark QrLoginSession as error");
                            }
                        }
                    }
                });

                return newQrSession;
            }
            catch (Exception e)
            {
                throw new Exception(e.Message);
            }
        }
    }
}
