namespace SteamAPI.Middleware
{
    public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        private readonly string[] _protectedPrefixes = ["/"];

        // cache for keys read from file to avoid reading file on every request
        private HashSet<string>? _allowedKeysFromFile;
        private DateTime _keysFileWriteTimeUtc = DateTime.MinValue;
        private readonly object _keysLock = new();

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            bool requiresAuth = _protectedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!requiresAuth)
            {
                await next(context);
                return;
            }

            var keyFile = Environment.GetEnvironmentVariable("API_KEY_FILE");
            if (string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile))
            {
                logger.LogError("API key file not configured or missing. Blocking request.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("API key file not configured on server.");
                return;
            }

            try
            {
                var writeTime = File.GetLastWriteTimeUtc(keyFile);
                // reload if cache empty or file changed
                if (_allowedKeysFromFile == null || writeTime > _keysFileWriteTimeUtc)
                {
                    var lines = File.ReadAllLines(keyFile);
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var line in lines)
                    {
                        var l = line?.Trim();
                        if (string.IsNullOrEmpty(l)) continue;
                        if (l.StartsWith('#')) continue;
                        keys.Add(l);
                    }

                    lock (_keysLock)
                    {
                        _allowedKeysFromFile = keys;
                        _keysFileWriteTimeUtc = writeTime;
                    }

                    logger.LogInformation("Loaded {Count} API keys from file {KeyFile}", keys.Count, keyFile);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read API key file '{KeyFile}'", keyFile);
                // if reading fails, block
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("Failed to read API key file.");
                return;
            }

            if (_allowedKeysFromFile == null || _allowedKeysFromFile.Count == 0)
            {
                logger.LogError("No API keys found in key file. Blocking request.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("No API keys configured on server.");
                return;
            }

            string? providedKey = null;

            if (context.Request.Headers.TryGetValue("X-API-KEY", out var headerValues))
                providedKey = headerValues.FirstOrDefault();

            if (string.IsNullOrEmpty(providedKey) && context.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var auth = authHeader.FirstOrDefault();
                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = auth["Bearer ".Length..].Trim();
                }
            }

            if (string.IsNullOrEmpty(providedKey) && context.Request.Query.TryGetValue("api_key", out var queryKey))
            {
                providedKey = queryKey.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(providedKey) && _allowedKeysFromFile.Contains(providedKey))
            {
                await next(context);
                return;
            }

            logger.LogWarning($"Unauthorized request to {path} from: {context.Request.HttpContext.Connection.RemoteIpAddress}");
            
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
        }
    }
}
