using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace K4Missions;

/// <summary>
/// Database mission record - Dommel entity for k4_missions table
/// </summary>
[Table("k4_missions")]
public sealed class DbMission
{
	[Key]
	[Column("id")]
	public int Id { get; set; }

	[Column("steamid64")]
	public long SteamId64 { get; set; }

	[Column("event")]
	public string Event { get; set; } = string.Empty;

	[Column("target")]
	public string Target { get; set; } = string.Empty;

	[Column("amount")]
	public int Amount { get; set; }

	[Column("phrase")]
	public string Phrase { get; set; } = string.Empty;

	[Column("reward_phrase")]
	public string RewardPhrase { get; set; } = string.Empty;

	[Column("reward_commands")]
	public string RewardCommands { get; set; } = string.Empty;

	[Column("progress")]
	public int Progress { get; set; }

	[Column("completed")]
	public bool Completed { get; set; }

	[Column("expires_at")]
	public DateTime? ExpiresAt { get; set; }

	[Column("event_properties")]
	public string? EventPropertiesJson { get; set; }

	[Column("map_name")]
	public string? MapName { get; set; }

	/// <summary>
	/// Parse reward commands from pipe-separated string
	/// </summary>
	public List<string> GetRewardCommandsList() =>
		RewardCommands.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

	/// <summary>
	/// Parse event properties from JSON string
	/// </summary>
	public Dictionary<string, JsonElement>? GetEventProperties()
	{
		if (string.IsNullOrEmpty(EventPropertiesJson))
			return null;

		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(EventPropertiesJson);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Serialize event properties to JSON string
	/// </summary>
	public static string? SerializeEventProperties(Dictionary<string, JsonElement>? eventProperties)
	{
		if (eventProperties == null || eventProperties.Count == 0)
			return null;

		return JsonSerializer.Serialize(eventProperties);
	}

	/// <summary>
	/// Create a DbMission from a PlayerMission
	/// </summary>
	public static DbMission FromPlayerMission(ulong steamId, PlayerMission mission, DateTime? expiresAt) => new()
	{
		SteamId64 = (long)steamId,
		Event = mission.Event,
		Target = mission.Target,
		Amount = mission.Amount,
		Phrase = mission.Phrase,
		RewardPhrase = mission.RewardPhrase,
		RewardCommands = string.Join("|", mission.RewardCommands),
		Progress = mission.Progress,
		Completed = mission.IsCompleted,
		ExpiresAt = expiresAt,
		EventPropertiesJson = SerializeEventProperties(mission.EventProperties),
		MapName = mission.MapName
	};
}
