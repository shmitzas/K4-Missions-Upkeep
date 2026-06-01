using Microsoft.Extensions.Logging;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Handles mission expiration and reset logic
	/// </summary>
	public sealed class ResetService(DatabaseService database, PlayerManager playerManager, WebhookService? webhookService = null)
	{
		private readonly DatabaseService _database = database;
		private readonly PlayerManager _playerManager = playerManager;
		private readonly WebhookService? _webhookService = webhookService;

		private CancellationTokenSource? _expirationCheckCts;

		/// <summary>
		/// Start the periodic expiration check timer
		/// </summary>
		public void StartExpirationTimer()
		{
			if (Config.CurrentValue.ResetMode is ResetMode.Instant or ResetMode.PerMap)
				return;

			_expirationCheckCts = Core.Scheduler.RepeatBySeconds(60f, () =>
			{
				_ = Task.Run(async () =>
				{
					try { await CheckForExpiredMissionsAsync(); }
					catch (Exception ex) { Core.Logger.LogError(ex, "Error in expiration timer"); }
				});
			});
		}

		/// <summary>
		/// Stop the expiration check timer
		/// </summary>
		public void StopExpirationTimer()
		{
			_expirationCheckCts?.Cancel();
			_expirationCheckCts = null;
		}

		/// <summary>
		/// Calculate expiration date based on reset mode
		/// </summary>
		public DateTime? CalculateExpirationDate()
		{
			return Config.CurrentValue.ResetMode switch
			{
				ResetMode.Daily => DateTime.Now.Date.AddDays(1).AddSeconds(-1), // End of today 23:59:59

				ResetMode.Weekly => GetEndOfWeek(),

				ResetMode.Monthly => new DateTime(
					DateTime.Now.Year,
					DateTime.Now.Month,
					DateTime.DaysInMonth(DateTime.Now.Year, DateTime.Now.Month),
					23, 59, 59),

				_ => null // Instant and PerMap don't use expiration
			};
		}

		private static DateTime GetEndOfWeek()
		{
			var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)DateTime.Now.DayOfWeek + 7) % 7;
			if (daysUntilSunday == 0)
				daysUntilSunday = 7; // If today is Sunday, go to next Sunday

			return DateTime.Now.Date.AddDays(daysUntilSunday).AddSeconds(-1);
		}

		/// <summary>
		/// Check for and handle expired missions
		/// </summary>
		public async Task CheckForExpiredMissionsAsync()
		{
			try
			{
				var playersWithExpired = await _database.GetPlayersWithExpiredMissionsAsync();

				if (playersWithExpired.Count == 0)
					return;

				// Clean up in database
				await _database.CleanupExpiredMissionsAsync();

				// Handle online players
				foreach (var steamId in playersWithExpired)
				{
					var player = _playerManager.GetPlayer(steamId);
					if (player == null || !player.IsValid)
						continue;

					Core.Scheduler.NextWorldUpdate(() =>
					{
						if (player.IsValid)
						{
							_playerManager.HandleExpiredMissions(player);
						}
					});
				}

				// Send webhook notification
				await SendResetWebhookAsync();
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Error checking for expired missions");
			}
		}

		/// <summary>
		/// Send reset webhook notification
		/// </summary>
		private async Task SendResetWebhookAsync()
		{
			if (_webhookService == null || string.IsNullOrEmpty(Config.CurrentValue.WebhookUrl))
				return;

			var nextReset = CalculateExpirationDate() ?? DateTime.Now.AddDays(1);
			await _webhookService.SendResetAsync(Config.CurrentValue.WebhookUrl, Config.CurrentValue.ResetMode, nextReset);
		}

		/// <summary>
		/// Handle mission completion based on reset mode
		/// </summary>
		public void OnMissionCompleted(MissionPlayer player, PlayerMission mission)
		{
			if (Config.CurrentValue.ResetMode != ResetMode.Instant)
				return;

			// For instant mode, remove completed mission and assign a new one
			Core.Scheduler.NextWorldUpdate(() =>
			{
				if (!player.IsValid)
					return;

				_playerManager.RemoveMission(player, mission);
				_playerManager.EnsureCorrectMissionCount(player);
			});
		}

		/// <summary>
		/// Get time remaining until expiration
		/// </summary>
		public static (int Days, int Hours, int Minutes) GetTimeUntilExpiration(DateTime expiresAt)
		{
			var remaining = expiresAt - DateTime.Now;

			if (remaining.TotalSeconds <= 0)
				return (0, 0, 0);

			var totalHours = (int)remaining.TotalHours;
			return (totalHours / 24, totalHours % 24, remaining.Minutes);
		}
	}
}
