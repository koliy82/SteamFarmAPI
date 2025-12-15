using Microsoft.Extensions.Logging;
using SteamAPI.Controllers;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamAPI.Models.Mongo;
using MongoDB.Driver;

namespace SteamAPI.Models
{
    public class SteamSession
    {
        private SteamClient _steamClient;
        private CallbackManager _manager;
        private SteamUser? _steamUser;
        private SteamAccount _accountData;
        private bool _isRunning;
        private CancellationTokenSource _cts;

        public SteamFriends? _steamFriends;
        public SessionStatus status = SessionStatus.Unknown;
        private readonly ILogger<AccountsController> logger;
        private readonly AccountRepo _accRepo;
        private readonly QrRepo? _qrRepo;
        private readonly Action<SteamSession>? _onAuthenticated;

        public SteamSession(SteamAccount account, ILogger<AccountsController> logger, AccountRepo accRepo, QrRepo? qrRepo = null, Action<SteamSession>? onAuthenticated = null)
        {
            this.logger = logger;
            _accountData = account;
            _accRepo = accRepo;
            _qrRepo = qrRepo;
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

            Start();
        }

        public void Start()
        {
            if (_isRunning) return;
            _steamClient.Connect();
            _isRunning = true;
            _cts = new CancellationTokenSource();
            logger.LogInformation($"[{_accountData.Username}] Connecting...");
            
            Task.Run(() =>
            {
                while (_isRunning && !_cts.Token.IsCancellationRequested)
                {
                    _manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
            }, _cts.Token);
        }
        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
        }

        public void Delete()
        {
            Stop();
            _steamUser.LogOff();
            _steamClient.Disconnect();
        }

        public void UpdateGames(List<object> gameIds)
        {
            _accountData.GameIds = gameIds;
            if (_steamClient.IsConnected)
            {
                SendGamesPlayed();
            }
        }

        public void UpdateStatus(EPersonaState state)
        {
            _accountData.PersonaState = state;
            if (_steamClient.IsConnected)
            {
                _steamFriends?.SetPersonaState(state);
            }
        }

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            logger.LogInformation($"Steam server for {_accountData.Username} Connected. Logging in...");
            LogIn();
        }

        private void LogIn()
        {
            logger.LogInformation($"{_accountData.Username} logging in try...");
            if (_accountData.RefreshToken == null)
            {
                logger.LogInformation($"{_accountData.Username} Refresh token is null, need auth.");
                status = SessionStatus.NeedAuth;
                return;
            }

            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _accountData.Username,
                AccessToken = _accountData.RefreshToken,
                ShouldRememberPassword = true
            });
        }

        async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                logger.LogInformation($"[{_accountData.Username}] Logon failed: {callback.Result}");
                return;
            }

            logger.LogInformation($"[{_accountData.Username}] Successfully logged on!");

            _steamFriends?.SetPersonaState(_accountData.PersonaState);

            if (_accountData.GameIds.Count > 0)
            {
                SendGamesPlayed();
            }
            status = SessionStatus.Active;
        }

        private void SendGamesPlayed()
        {
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);

            foreach (var gameId in _accountData.GameIds)
            {
                if (gameId is string gameName)
                {
                    playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = 15190414816125648896,
                        game_extra_info = gameName,
                    });
                    continue;
                } else if (gameId is ulong)
                {
                    playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = (ulong)gameId,
                    });
                }
            }

            _steamClient.Send(playGame);
            logger.LogInformation($"[{_accountData.Username}] Farming started for games: {string.Join(",", _accountData.GameIds)}");
        }

        async void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            logger.LogInformation($"[{_accountData.Username}] Disconnected.");
            if (_isRunning)
            {
                await Task.Delay(10000).ContinueWith(_ => _steamClient.Connect());
            }
        }

        async void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            logger.LogInformation($"[{_accountData.Username}] Logged off: {callback.Result}");
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

                var newQrSession = new QrLoginSession(_accountData.Id)
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
                        _accountData.Username = pollResponse.AccountName;
                        _accountData.RefreshToken = pollResponse.RefreshToken;

                        var update = Builders<SteamAccount>.Update
                            .Set(x => x.Username, _accountData.Username)
                            .Set(x => x.RefreshToken, _accountData.RefreshToken)
                            .SetOnInsert(x => x.TelegramId, _accountData.TelegramId)
                            .SetOnInsert(x => x.GameIds, _accountData.GameIds)
                            .SetOnInsert(x => x.IsFarming, _accountData.IsFarming)
                            .SetOnInsert(x => x.PersonaState, _accountData.PersonaState);

                        var filter = Builders<SteamAccount>.Filter.Eq(x => x.Id, _accountData.Id);
                        var options = new UpdateOptions { IsUpsert = true };

                        try
                        {
                            await _accRepo.Coll.UpdateOneAsync(filter, update, options);
                            logger.LogInformation($"[{_accountData.Id}] Account upserted in MongoDB with username: {_accountData.Username}");
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
                                .Set(x => x.Username, _accountData.Username)
                                .Set(x => x.RefreshToken, _accountData.RefreshToken);

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
