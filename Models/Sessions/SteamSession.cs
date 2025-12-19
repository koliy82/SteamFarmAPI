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
        // Токен для управления отложенными попытками переподключения (чтобы можно было их отменять)
        private CancellationTokenSource? _reconnectCts;
        // Защита от частых переподключений
        private int _reconnectAttempts = 0;
        private DateTime _reconnectWindowStart = DateTime.MinValue;
        private static readonly int MAX_RECONNECT_ATTEMPTS = 2; // максимум попыток в окне (reduced to quickly suppress loops)
        private static readonly TimeSpan RECONNECT_WINDOW = TimeSpan.FromSeconds(30);
        // Когда true — запрещаем автоматическое переподключение (например, после LoggedInElsewhere или Stop)
        private bool _suppressAutoReconnect = false;
        // Для защиты от гонки: время последнего LoggedOff с LoggedInElsewhere
        private DateTime _lastLoggedOffUtc = DateTime.MinValue;
        // Отслеживание быстрого разрыва после логина
        private DateTime _lastLoggedOnUtc = DateTime.MinValue;
        private int _immediateDisconnects = 0;
        // Если пользователь явно запросил рестарт фарма — разрешаем только ручной старт и блокируем автоматические переподключения
        private bool _manualResumeRequested = false;
        private static readonly int IMMEDIATE_DISCONNECT_WINDOW_SECONDS = 10;
        private static readonly int IMMEDIATE_DISCONNECT_MAX = 3;

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

            // не запускаем Init() автоматически в конструкторе — запуск/подключение контролируется сервисом
            // Init();
         }

         public void Init()
         {
             if (_isRunning) return;
             // ручной/инициализационный запуск — разрешаем автоподключения
             _suppressAutoReconnect = false;
             logger.LogDebug($"[{accountData.Username}] Init(): _suppressAutoReconnect={_suppressAutoReconnect}, _isRunning={_isRunning}, account.IsFarming={accountData.IsFarming}");
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
            // Если сессия была остановлена или вообще не подключена — инициируем подключение
            if (status == SessionStatus.Stopped || !_steamClient.IsConnected)
            {
                logger.LogDebug($"[{accountData.Username}] Start(): status={status}, _steamClient.IsConnected={_steamClient.IsConnected}, _suppressAutoReconnect={_suppressAutoReconnect}");
                if (_suppressAutoReconnect)
                {
                    logger.LogInformation($"[{accountData.Username}] Start suppressed (auto-reconnect disabled). Waiting for manual resume.");
                    return;
                }

                Init();
            }
 
             // После подключения — отправляем список игр
             if (_steamClient.IsConnected && accountData.IsFarming)
             {
                 await SendGamesPlayed();
             }
         }

        // Явное пользовательское возобновление фарма (нажатие кнопки в UI)
        public async Task RequestStartFarming()
         {
             // Пользователь запросил старт — снимаем запрет на авто-подключение и запускаем фарм
             _suppressAutoReconnect = false;
            // пометить что это ручной рестарт — автопереподключения должны приостановиться
            _manualResumeRequested = true;
             accountData.IsFarming = true;
             // При ручном старте сбрасываем счётчики переподключений
             _reconnectAttempts = 0;
             _reconnectWindowStart = DateTime.MinValue;
             _immediateDisconnects = 0;
             _lastLoggedOnUtc = DateTime.MinValue;
             // отменим любые ожидающие переподключения, чтобы не было гонки
             try { _reconnectCts?.Cancel(); } catch { }
              status = SessionStatus.Unknown; // сбросим статус, чтобы Start() мог инициировать Init()
              logger.LogInformation($"[{accountData.Username}] RequestStartFarming(): user requested resume. _suppressAutoReconnect set to false. account.IsFarming={accountData.IsFarming}");
              await Start();
         }
        public async Task Stop(LogReason reason)
        {
            // при явной остановке запрещаем авто-переподключение
            _suppressAutoReconnect = true;
            // отменяем любые запланированные переподключения
            try { _reconnectCts?.Cancel(); } catch { }
             // Если соединение есть — попытаемся очистить список играющих, зафиксировать лог и поставить статус Stopped
            try
            {
                if (_steamClient.IsConnected)
                {
                    var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
                    // отправка пустого списка игр — остановить фарм
                    _steamClient.Send(playGame);
                }

                logger.LogInformation($"[{accountData.Username}] Farming stopped for games: {string.Join(",", accountData.GameIds)}, reason: {reason}");
                await _logRepo.Coll.InsertOneAsync(new FarmLog
                {
                    Reason = reason,
                    State = status,
                    SteamId = accountData.Id,
                    SteamName = accountData.Username,
                    TelegramId = accountData.TelegramId,
                });

                // Обновим флаг в памяти и в БД — аккаунт больше не фармит
                accountData.IsFarming = false;
                status = SessionStatus.Stopped;

                var filter = Builders<SteamAccount>.Filter.Eq(x => x.Id, accountData.Id);
                var update = Builders<SteamAccount>.Update
                    .Set(x => x.IsFarming, accountData.IsFarming)
                    .Set(x => x.PersonaState, accountData.PersonaState);

                try
                {
                    await _accRepo.Coll.UpdateOneAsync(filter, update);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"[{accountData.Username}] Failed to update IsFarming in DB on Stop");
                }

                // Завершим внутренние таски и разорвём соединение чтобы не было неожиданных переподключений
                _isRunning = false;
                _cts?.Cancel();
                try { _reconnectCts?.Cancel(); } catch { }

                 try { _steamUser?.LogOff(); } catch { /* ignore */ }
                 try { _steamClient.Disconnect(); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"[{accountData.Username}] Error while stopping farming");
            }
        }
 
         public async Task Delete()
         {
             status = SessionStatus.Deleted;
             _suppressAutoReconnect = true;
            try { _reconnectCts?.Cancel(); } catch { }
             await Stop(LogReason.UserDelete);
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
            logger.LogDebug($"[{accountData.Username}] OnConnected callback: {callback}");
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

        private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            logger.LogDebug($"[{accountData.Username}] OnLoggedOn: result={callback.Result}");
            if (callback.Result == EResult.TryAnotherCM)
            {
                // не выключаем флаг _isRunning тут — будет контролировать цикл переподключений
                logger.LogWarning($"[{accountData.Username}] Logon result: TryAnotherCM — попросили подключиться к другому CM. Запланирована серия переподключений.");
                _ = Task.Run(async () =>
                {
                    const int maxAttempts = 5;
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            logger.LogInformation($"[{accountData.Username}] Попытка переподключения #{attempt}...");

                            // Принудительно разрываем текущее соединение — при новом Connect SteamKit2 попытается выбрать другой CM
                            try { _steamClient.Disconnect(); } catch { /* ignore */ }

                            var delayMs = 1000 * (int)Math.Pow(2, attempt - 1); // 1s,2s,4s,8s,16s
                            await Task.Delay(delayMs);

                            if (_suppressAutoReconnect) break;

                            _steamClient.Connect();

                            // Подождём короткий промежуток — если логин прошёл, OnLoggedOn для OK сработает и status сменится
                            await Task.Delay(5000);

                            if (status != SessionStatus.Active) continue;
                            _isRunning = true;
                            logger.LogInformation($"[{accountData.Username}] Успешно подключились к другому CM.");
                            return;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"[{accountData.Username}] Ошибка при попытке переподключения к другому CM");
                        }
                    }

                    logger.LogWarning($"[{accountData.Username}] Не удалось подключиться к другому CM после нескольких попыток.");

                    // Если не удалось — пометим что нужна авторизация/вмешательство
                    status = SessionStatus.TryAnotherCM;
                });
                return;
            }

            if (callback.Result != EResult.OK)
            {
                logger.LogInformation($"[{accountData.Username}] Logon failed: {callback.Result}");
                logger.LogInformation($"[{accountData.Username}] callback: {callback}");
                return;
            }

            logger.LogInformation($"[{accountData.Username}] Successfully logged on!");

            // отметим время успешного логина — нужно для детекции быстрого разрывов
            _lastLoggedOnUtc = DateTime.UtcNow;
            _immediateDisconnects = 0;
            // Сбросим счётчики попыток переподключения — успешный логин означает стабильное соединение
            _reconnectAttempts = 0;
            _reconnectWindowStart = DateTime.MinValue;
            // отменим любые ожидающие переподключения, чтобы не сработали старые таски
            try { _reconnectCts?.Cancel(); } catch { }
             logger.LogDebug($"[{accountData.Username}] OnLoggedOn: reset reconnect counters");
            // успешный логин — ручной рестарт завершён
            _manualResumeRequested = false;

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
                Reason = LogReason.GamesSend,
                State = status,
                SteamId = accountData.Id,
                SteamName = accountData.Username,
                TelegramId = accountData.TelegramId
            });
            logger.LogInformation($"[{accountData.Username}] Farming started for games: {string.Join(",", accountData.GameIds)}");
        }

        async void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            logger.LogInformation($"[{accountData.Username}] Disconnected. callback: {callback}");
            // не переподключаемся если явная остановка/LoggedInElsewhere/удаление (suppress)
            // Защита от гонки: если недавно был LoggedOff с LoggedInElsewhere, не подключаемся снова
            var now = DateTime.UtcNow;
            // если дисконнект случился очень скоро после успешного логина — признаём это "моментальным" и считаем попытку
            if (_lastLoggedOnUtc != DateTime.MinValue && (now - _lastLoggedOnUtc) < TimeSpan.FromSeconds(IMMEDIATE_DISCONNECT_WINDOW_SECONDS))
            {
                _immediateDisconnects++;
                logger.LogWarning($"[{accountData.Username}] Immediate disconnect detected ({_immediateDisconnects}) after last login ({(now - _lastLoggedOnUtc).TotalSeconds}s)");
                // При первом и последующих быстрых разрывах не планируем немедленный reconnect — даём пользователю или серверу время стабилизироваться.
                if (_immediateDisconnects >= IMMEDIATE_DISCONNECT_MAX)
                {
                    logger.LogWarning($"[{accountData.Username}] Too many immediate disconnects ({_immediateDisconnects}), suppressing auto-reconnect. Require manual resume.");
                    _suppressAutoReconnect = true;
                    status = SessionStatus.TryAnotherCM;
                    try { _reconnectCts?.Cancel(); } catch { }
                    try
                    {
                        await _logRepo.Coll.InsertOneAsync(new FarmLog
                        {
                            Reason = LogReason.TryAnotherCM,
                            State = status,
                            SteamId = accountData.Id,
                            SteamName = accountData.Username,
                            TelegramId = accountData.TelegramId,
                        });
                    }
                    catch { }
                    return;
                }
                // Для первых быстрых разрывов — не делать reconnect, просто выйдем и подождём ручного рестарта.
                logger.LogInformation($"[{accountData.Username}] Skipping reconnect due to immediate disconnect (count {_immediateDisconnects}). Manual resume required.");
                return;
            }

             if (_lastLoggedOffUtc != DateTime.MinValue && (now - _lastLoggedOffUtc) < TimeSpan.FromSeconds(5))
             {
                 logger.LogInformation($"[{accountData.Username}] Recent LoggedOff detected, skipping auto-reconnect.");
                 return;
             }

            // планируем отложенное переподключение, которое можно отменить по флагу
            if (_isRunning && !_suppressAutoReconnect)
             {
                // Если был запрошен ручной рестарт — не планируем автоматически reconnect, чтобы избежать конфликтов
                if (_manualResumeRequested)
                {
                    logger.LogInformation($"[{accountData.Username}] Manual resume requested — skipping automatic reconnect scheduling.");
                    return;
                }
                 try
                 {
                    // отменяем предыдущий токен и создаём новый
                    try { _reconnectCts?.Cancel(); } catch { }
                    _reconnectCts = new CancellationTokenSource();
                    var token = _reconnectCts.Token;

                    logger.LogInformation($"[{accountData.Username}] OnDisconnected(): scheduling reconnect. attempts={_reconnectAttempts}, windowStart={_reconnectWindowStart}, _isRunning={_isRunning}, _suppressAutoReconnect={_suppressAutoReconnect}, lastLoggedOff={_lastLoggedOffUtc}, account.IsFarming={accountData.IsFarming}");

                    // контроль частых переподключений: обновим окно и счётчик
                     var nowPrep = DateTime.UtcNow;
                     if (_reconnectWindowStart == DateTime.MinValue || (nowPrep - _reconnectWindowStart) > RECONNECT_WINDOW)
                     {
                         _reconnectWindowStart = nowPrep;
                         _reconnectAttempts = 0;
                     }
                     _reconnectAttempts++;
                     var attemptNumber = _reconnectAttempts; // фиксируем для локального таска
                     logger.LogInformation($"[{accountData.Username}] OnDisconnected(): scheduling reconnect. attemptNumber={attemptNumber}, windowStart={_reconnectWindowStart}, _isRunning={_isRunning}, _suppressAutoReconnect={_suppressAutoReconnect}, lastLoggedOff={_lastLoggedOffUtc}, account.IsFarming={accountData.IsFarming}");

                    if (attemptNumber > MAX_RECONNECT_ATTEMPTS)
                     {
                         logger.LogWarning($"[{accountData.Username}] Too many reconnect attempts ({_reconnectAttempts}) in window {RECONNECT_WINDOW}. Suppressing further auto-reconnect.");
                         _suppressAutoReconnect = true;
                         status = SessionStatus.TryAnotherCM;
                         try { _reconnectCts?.Cancel(); } catch { }
                         // Запишем событие в лог, чтобы клиент увидел причину
                         try
                         {
                             await _logRepo.Coll.InsertOneAsync(new FarmLog
                             {
                                 Reason = LogReason.TryAnotherCM,
                                 State = status,
                                 SteamId = accountData.Id,
                                 SteamName = accountData.Username,
                                 TelegramId = accountData.TelegramId,
                             });
                         }
                         catch { /* ignore DB errors */ }
                         return;
                     }

                    // увеличиваем задержку экспоненциально в зависимости от числа попыток (используем локальную переменную)
                    var delaySeconds = Math.Min(10 * (int)Math.Pow(2, Math.Max(0, attemptNumber - 1)), 60);
                    logger.LogInformation($"[{accountData.Username}] OnDisconnected: waiting {delaySeconds}s before reconnect (attempt {attemptNumber})");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ContinueWith(t => { }, TaskScheduler.Default);

                    if (token.IsCancellationRequested) return;

                    // повторно проверим флаги перед Connect
                    var now2 = DateTime.UtcNow;
                    if (_lastLoggedOffUtc != DateTime.MinValue && (now2 - _lastLoggedOffUtc) < TimeSpan.FromSeconds(5))
                    {
                        logger.LogInformation($"[{accountData.Username}] Recent LoggedOff detected (post-delay), skipping auto-reconnect.");
                        return;
                    }
                    if (_suppressAutoReconnect) return;

                    // Также проверим в базе: если IsFarming было выключено (например, LoggedInElsewhere обновил DB), не подключаемся
                    try
                    {
                        var accFromDb = await _accRepo.Coll.Find(x => x.Id == accountData.Id).FirstOrDefaultAsync();
                        if (accFromDb != null && !accFromDb.IsFarming)
                        {
                            logger.LogInformation($"[{accountData.Username}] DB shows IsFarming=false, skipping auto-reconnect.");
                            return;
                        }
                        logger.LogDebug($"[{accountData.Username}] DB IsFarming={accFromDb?.IsFarming}");
                    }
                    catch (Exception dbEx)
                    {
                        logger.LogError(dbEx, $"[{accountData.Username}] Failed to read account state from DB before reconnect. Proceeding with reconnect.");
                    }

                    logger.LogInformation($"[{accountData.Username}] Performing Connect (attempt {attemptNumber})");
                    if (_steamClient.IsConnected)
                    {
                        logger.LogInformation($"[{accountData.Username}] Skipping scheduled Connect because client already connected (attempt {attemptNumber})");
                    }
                    else
                    {
                        logger.LogInformation($"[{accountData.Username}] Performing Connect (attempt {attemptNumber})");
                        _steamClient.Connect();
                    }
                 }
                 catch (Exception ex)
                 {
                     logger.LogError(ex, $"[{accountData.Username}] Error in scheduled reconnect");
                 }
             }
          }
 
        async void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            logger.LogInformation($"[{accountData.Username}] Logged off: {callback.Result}");
            if (callback.Result == EResult.LoggedInElsewhere)
            {
                // Пользователь залогинился в аккаунт где-то ещё — нужно остановить фарм и не пытаться автоматически возобновлять
                _isRunning = false;
                _cts?.Cancel();
                _suppressAutoReconnect = true;
                _lastLoggedOffUtc = DateTime.UtcNow;
                logger.LogInformation($"[{accountData.Username}] OnLoggedOff: set _suppressAutoReconnect=true, _lastLoggedOffUtc={_lastLoggedOffUtc}");

                // Обновляем состояние и помечаем, что аккаунт не фармит
                status = SessionStatus.Stopped;
                accountData.IsFarming = false;

                // Попытаемся сохранить изменение в MongoDB
                try
                {
                    var filter = Builders<SteamAccount>.Filter.Eq(x => x.Id, accountData.Id);
                    var update = Builders<SteamAccount>.Update
                        .Set(x => x.IsFarming, accountData.IsFarming)
                        .Set(x => x.PersonaState, accountData.PersonaState);
                    await _accRepo.Coll.UpdateOneAsync(filter, update);
                    logger.LogDebug($"[{accountData.Username}] OnLoggedOff: updated DB IsFarming=false");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"[{accountData.Username}] Failed to update IsFarming in DB on LoggedInElsewhere");
                }

                // Запишем лог о причине и отключимся — чтобы не началось авто-подключение
                try
                {
                    await _logRepo.Coll.InsertOneAsync(new FarmLog
                    {
                        Reason = LogReason.LoggedInElsewhere,
                        State = status,
                        SteamId = accountData.Id,
                        SteamName = accountData.Username,
                        TelegramId = accountData.TelegramId,
                    });
                    logger.LogDebug($"[{accountData.Username}] OnLoggedOff: inserted FarmLog LoggedInElsewhere");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"[{accountData.Username}] Failed to insert FarmLog for LoggedInElsewhere");
                }

                try { _steamUser?.LogOff(); } catch { /* ignore */ }
                try { _steamClient.Disconnect(); } catch { /* ignore */ }

                // отменим любые ожидающие переподключения
                try { _reconnectCts?.Cancel(); } catch { }
                // manual resume неактуален после LoggedInElsewhere
                _manualResumeRequested = false;

                  return;
             }
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
                
                await _qrRepo.Coll.InsertOneAsync(newQrSession);

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
