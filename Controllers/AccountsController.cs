using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using SteamAPI.Services;
using SteamKit2;

namespace SteamAPI.Controllers
{
    [ApiController]
    [Route("api/steam")]
    public class AccountsController : ControllerBase
    {
        private readonly SteamService _steamService;
        private readonly ILogger<AccountsController> logger;

        public AccountsController(SteamService steamService, ILogger<AccountsController> logger)
        {
            _steamService = steamService;
            this.logger = logger;
        }

        [HttpGet("{telegramId}")]
        public async Task<IActionResult> GetAccounts(long telegramId)
        {
            logger.LogInformation("get id");
            return Ok(await _steamService.accRepo.Coll.Find(x => x.TelegramId == telegramId).ToListAsync());
        }

        [HttpPut("{accountId}/games")]
        public async Task<IActionResult> UpdateGames(string accountId, [FromBody] List<object> gameIds)
        {
            if (ObjectId.TryParse(accountId, out _) == false) return BadRequest("accountId not valid");
            await _steamService.UpdateGamesAsync(accountId, gameIds);           
            return Ok(new { message = "Game list updated" });
        }

        [HttpPut("{accountId}/status/{statusId}")]
        public async Task<IActionResult> UpdateStatus(string accountId, int statusId)
        {
            if (ObjectId.TryParse(accountId, out _) == false) return BadRequest("accountId not valid");
            var status = (EPersonaState)statusId;
            await _steamService.UpdateStatusAsync(accountId, status);
            return Ok(new { message = $"Status updated: {status}" });
        }

        [HttpDelete("{accountId}")]
        public async Task<IActionResult> DeleteAccount(string accountId)
        {
            if (ObjectId.TryParse(accountId, out _) == false) return BadRequest("accountId not valid");
            await _steamService.DeleteAccountAsync(accountId);
            return Ok(new { message = "Account deleted" });
        }

        [HttpPost("{accountId}/stop")]
        public async Task<IActionResult> StopFarming(string accountId)
        {
            if (ObjectId.TryParse(accountId, out _) == false) return BadRequest("accountId not valid");
            await _steamService.StopFarmingAsync(accountId);
            return Ok(new { message = "Farming stopped" });
        }

        [HttpPost("{accountId}/start")]
        public async Task<IActionResult> StartFarming(string accountId)
        {
            if (ObjectId.TryParse(accountId, out _) == false) return BadRequest("accountId not valid");
            await _steamService.StartFarmingAsync(accountId);
            return Ok(new { message = "Farming started" });
        }
    }
}
