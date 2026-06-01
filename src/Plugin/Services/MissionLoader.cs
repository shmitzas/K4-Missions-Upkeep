using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Loads and manages mission definitions from missions.json
	/// </summary>
	public sealed class MissionLoader
	{
		private readonly List<MissionDefinition> _missions = [];

		/// <summary>
		/// Load missions from resources/missions.json
		/// </summary>
		public void LoadFromFile(string moduleDirectory)
		{
			var filePath = Path.Combine(moduleDirectory, "resources", "missions.json");

			if (!File.Exists(filePath))
			{
				Core.Logger.LogError("missions.json not found at {Path}.", filePath);
				return;
			}

			try
			{
				var jsonString = File.ReadAllText(filePath);
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true,
					ReadCommentHandling = JsonCommentHandling.Skip
				};

				var loadedMissions = JsonSerializer.Deserialize<List<MissionDefinition>>(jsonString, options);

				if (loadedMissions == null || loadedMissions.Count == 0)
				{
					Core.Logger.LogError("No missions found in missions.json");
					return;
				}

				_missions.Clear();
				_missions.AddRange(loadedMissions);

				Core.Logger.LogInformation("Loaded {Count} missions from configuration.", _missions.Count);
			}
			catch (JsonException ex)
			{
				Core.Logger.LogError(ex, "Failed to parse missions.json");
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to load missions.json");
			}
		}

		/// <summary>
		/// Get all loaded missions
		/// </summary>
		public IReadOnlyList<MissionDefinition> GetAllMissions() => _missions;

		/// <summary>
		/// Get all missions that match a specific event type
		/// </summary>
		public IEnumerable<MissionDefinition> GetByEvent(string eventType)
		{
			return _missions.Where(m => m.Event == eventType);
		}

		/// <summary>
		/// Get missions available to a player (respecting flag restrictions)
		/// </summary>
		public IEnumerable<MissionDefinition> GetAvailableMissions(MissionPlayer player, Func<string, bool> hasPermission)
		{
			return _missions.Where(m => m.Flag == null || hasPermission(m.Flag));
		}

		/// <summary>
		/// Find the current template that matches the given mission identity.
		/// Used to re-resolve reward commands at completion time so that
		/// missions.json edits take effect on in-flight player missions.
		/// </summary>
		public MissionDefinition? FindTemplate(string eventType, string target, string phrase, string rewardPhrase, int amount)
		{
			// Strongest match first (all fields), then progressively relax.
			var exact = _missions.FirstOrDefault(m =>
				string.Equals(m.Event, eventType, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(m.Target, target, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(m.Phrase, phrase, StringComparison.Ordinal) &&
				string.Equals(m.RewardPhrase, rewardPhrase, StringComparison.Ordinal) &&
				m.Amount == amount);
			if (exact != null)
				return exact;

			// Fall back to Event + Target + Phrase + Amount (reward phrase may have been retuned).
			return _missions.FirstOrDefault(m =>
				string.Equals(m.Event, eventType, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(m.Target, target, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(m.Phrase, phrase, StringComparison.Ordinal) &&
				m.Amount == amount);
		}
	}
}
