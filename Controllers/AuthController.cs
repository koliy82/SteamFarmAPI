using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SteamAPI.Models;
using SteamAPI.Services;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamAPI.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly SteamService _steamService;
        private readonly ILogger<AccountsController> logger;

        public AuthController(SteamService steamService, ILogger<AccountsController> logger)
        {
            _steamService = steamService;
            this.logger = logger;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateAccount(long telegramId)
        {
            logger.LogInformation("create");
            var account = new SteamAccount(telegramId);
            var auth = await _steamService.AddAccountAsync(account);
            return Ok(auth);
        }
    }

}
