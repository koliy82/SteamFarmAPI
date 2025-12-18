using System.Security.Cryptography;

namespace SteamAPI.Services
{
    public class InitialBackgroundService(
        SteamService steamService, 
        ILogger<InitialBackgroundService> logger, 
        IHostEnvironment env
    ) : BackgroundService 
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("InitialBackgroundService: starting initial sessions load.");

            try
            {
                EnsureApiKeyFileExists();

                await steamService.InitialStart();
                logger.LogInformation("InitialBackgroundService: initial sessions load completed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("InitialBackgroundService: cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "InitialBackgroundService: InitialStart failed.");
            }

        }

        private void EnsureApiKeyFileExists()
        {
            try
            {
                var keyFilePath = Environment.GetEnvironmentVariable("API_KEY_FILE");
                if (string.IsNullOrWhiteSpace(keyFilePath))
                {
                    keyFilePath = Path.Combine(env.ContentRootPath, "api_keys.txt");
                    Environment.SetEnvironmentVariable("API_KEY_FILE", keyFilePath);
                }

                var dir = Path.GetDirectoryName(keyFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(keyFilePath))
                {
                    var keys = new List<string>();
                    for (int i = 0; i < 5; i++)
                    {
                        var bytes = RandomNumberGenerator.GetBytes(32);
                        var key = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                        keys.Add(key);
                    }

                    var header = new[] { "# API keys file. One key per line. Lines starting with # are ignored.", $"# Generated: {DateTime.UtcNow:O}", "" };
                    File.WriteAllLines(keyFilePath, header.Concat(keys));

                    logger.LogInformation("API key file created at {Path} with {Count} keys", keyFilePath, keys.Count);
                }
                else
                {
                    logger.LogInformation("API key file exists at {Path}", keyFilePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure API key file exists");
            }
        }
    }
}