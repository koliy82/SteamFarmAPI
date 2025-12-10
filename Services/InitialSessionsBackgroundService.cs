namespace SteamAPI.Services
{
    public class InitialSessionsBackgroundService : BackgroundService
    {
        private readonly SteamService _steamService;
        private readonly ILogger<InitialSessionsBackgroundService> _logger;

        public InitialSessionsBackgroundService(SteamService steamService, ILogger<InitialSessionsBackgroundService> logger)
        {
            _steamService = steamService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("InitialSessionsBackgroundService: starting initial sessions load.");

            try
            {
                await _steamService.InitialStart();
                _logger.LogInformation("InitialSessionsBackgroundService: initial sessions load completed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("InitialSessionsBackgroundService: cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InitialSessionsBackgroundService: InitialStart failed.");
            }

        }
    }
}