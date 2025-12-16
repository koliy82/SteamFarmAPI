using Microsoft.AspNetCore.Mvc;
using SteamAPI.Models.Mongo.Models;
using SteamAPI.Services;

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
            // TODO Разделить Steam сессию на:
            // Auth сессию при создании аккаунта и Steam сессию
            // при получении пула с qr либо успеха с password auth (его нада тоже придумать)
            // telegram id -> auth_session | account id -> accounts_session
            // create - > tg_id -> auth_session: wait, complete, expired
            // create -> if wait return old url
            // create -> if complete create new auth_session
            // create -> if expired generate new auth_session
            var account = new SteamAccount(telegramId);
            var auth = await _steamService.AddAccountAsync(account);
            return Ok(auth);
        }
    }
}
