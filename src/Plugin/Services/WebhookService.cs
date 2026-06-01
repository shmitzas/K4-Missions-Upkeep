using System.Text;
using Microsoft.Extensions.Logging;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Handles Discord webhook notifications
	/// </summary>
	public sealed class WebhookService(string moduleDirectory) : IDisposable
	{
		private readonly HttpClient _httpClient = new();
		private readonly string _moduleDirectory = moduleDirectory;

		/// <summary>
		/// Send a webhook notification
		/// </summary>
		public async Task SendAsync(string webhookUrl, string templateFile, Dictionary<string, string> replacements)
		{
			if (string.IsNullOrEmpty(webhookUrl))
				return;

			var templatePath = Path.Combine(_moduleDirectory, "discord", templateFile);
			if (!File.Exists(templatePath))
			{
				Core.Logger.LogWarning("Webhook template not found: {Path}", templatePath);
				return;
			}

			try
			{
				var json = await File.ReadAllTextAsync(templatePath);

				foreach (var (key, value) in replacements)
				{
					json = json.Replace($"{{{key}}}", EscapeJson(value));
				}

				var content = new StringContent(json, Encoding.UTF8, "application/json");
				var response = await _httpClient.PostAsync(webhookUrl, content);

				if (!response.IsSuccessStatusCode)
				{
					Core.Logger.LogWarning("Webhook request failed: {Status}", response.StatusCode);
				}
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to send webhook");
			}
		}

		/// <summary>
		/// Send mission completion notification
		/// </summary>
		public Task SendMissionCompleteAsync(string webhookUrl, MissionPlayer player, PlayerMission mission, Func<string, string> localize)
		{
			var playerName = player.Player.Controller?.PlayerName ?? "Unknown";
			return SendAsync(webhookUrl, "complete.json", new Dictionary<string, string>
			{
				{ "name", playerName },
				{ "steamid", player.SteamId.ToString() },
				{ "profile", $"[{playerName}](https://steamcommunity.com/profiles/{player.SteamId})" },
				{ "mission", localize(mission.Phrase) },
				{ "reward", localize(mission.RewardPhrase) }
			});
		}

		/// <summary>
		/// Send all missions complete notification
		/// </summary>
		public Task SendAllMissionsCompleteAsync(string webhookUrl, MissionPlayer player, int totalMissions)
		{
			var playerName = player.Player.Controller?.PlayerName ?? "Unknown";
			return SendAsync(webhookUrl, "complete_all.json", new Dictionary<string, string>
			{
				{ "name", playerName },
				{ "steamid", player.SteamId.ToString() },
				{ "profile", $"[{playerName}](https://steamcommunity.com/profiles/{player.SteamId})" },
				{ "total_missions", totalMissions.ToString() }
			});
		}

		/// <summary>
		/// Send reset notification
		/// </summary>
		public Task SendResetAsync(string webhookUrl, ResetMode resetMode, DateTime nextReset)
		{
			return SendAsync(webhookUrl, "reset.json", new Dictionary<string, string>
			{
				{ "reset_mode", resetMode.ToString() },
				{ "next_reset", nextReset.ToString("yyyy-MM-dd HH:mm:ss") }
			});
		}

		private static string EscapeJson(string value)
		{
			return value
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"")
				.Replace("\n", "\\n")
				.Replace("\r", "\\r")
				.Replace("\t", "\\t");
		}

		public void Dispose()
		{
			_httpClient.Dispose();
		}
	}
}
