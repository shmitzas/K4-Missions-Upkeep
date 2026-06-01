using System.Text.Json;

namespace K4Missions;

/// <summary>
/// Base mission definition loaded from missions.json
/// </summary>
public sealed class MissionDefinition
{
	public string Event { get; set; } = string.Empty;

	public Dictionary<string, JsonElement>? EventProperties { get; set; }

	public string Target { get; set; } = string.Empty;

	public List<string> RewardCommands { get; set; } = [];

	public int Amount { get; set; }

	public string Phrase { get; set; } = string.Empty;

	public string RewardPhrase { get; set; } = string.Empty;

	public string? Flag { get; set; }

	public string? MapName { get; set; }

	/// <summary>
	/// Creates a player mission instance from this definition
	/// </summary>
	public PlayerMission CreatePlayerMission(DateTime? expiresAt = null)
	{
		return new PlayerMission
		{
			Event = Event,
			Target = Target,
			Amount = Amount,
			Phrase = Phrase,
			RewardPhrase = RewardPhrase,
			RewardCommands = RewardCommands,
			EventProperties = EventProperties,
			MapName = MapName,
			Flag = Flag,
			Progress = 0,
			IsCompleted = false,
			ExpiresAt = expiresAt
		};
	}
}

/// <summary>
/// A mission assigned to a specific player with progress tracking
/// </summary>
public sealed class PlayerMission
{
	/// <summary>Database ID for this mission assignment</summary>
	public int Id { get; set; } = -1;

	/// <summary>Event type for this mission</summary>
	public string Event { get; set; } = string.Empty;

	/// <summary>Target field from the event</summary>
	public string Target { get; set; } = string.Empty;

	/// <summary>Amount required to complete</summary>
	public int Amount { get; set; }

	/// <summary>Phrase key for localization</summary>
	public string Phrase { get; set; } = string.Empty;

	/// <summary>Reward phrase key for localization</summary>
	public string RewardPhrase { get; set; } = string.Empty;

	/// <summary>Commands to execute on completion</summary>
	public List<string> RewardCommands { get; set; } = [];

	/// <summary>Event property filters</summary>
	public Dictionary<string, JsonElement>? EventProperties { get; set; }

	/// <summary>Map restriction</summary>
	public string? MapName { get; set; }

	/// <summary>Permission flag required</summary>
	public string? Flag { get; set; }

	/// <summary>Current progress towards completion</summary>
	public int Progress { get; set; }

	/// <summary>Whether this mission has been completed</summary>
	public bool IsCompleted { get; set; }

	/// <summary>When this mission expires (null for PerMap/Instant modes)</summary>
	public DateTime? ExpiresAt { get; set; }

	/// <summary>
	/// Checks if this mission matches the given event and target
	/// </summary>
	public bool Matches(string eventType, string target, string? currentMap, Dictionary<string, object?>? eventProperties)
	{
		if (IsCompleted)
			return false;

		if (!string.Equals(Event, eventType, StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(Target, target, StringComparison.OrdinalIgnoreCase))
			return false;

		// Check map restriction
		if (MapName != null && MapName != currentMap)
			return false;

		// Check event properties if defined. Fail-closed: if this mission has any
		// property filters configured, the incoming event MUST carry properties
		// that satisfy every filter. Otherwise an event with no properties (or a
		// mission that lost its filters via a stale DB row) would silently match
		// any event of the same type/target (e.g. an AK kill counting toward an
		// "SSG08 headshot" mission).
		if (EventProperties != null && EventProperties.Count > 0)
		{
			if (eventProperties == null)
				return false;

			if (!MatchesEventProperties(eventProperties))
				return false;
		}

		return true;
	}

	private bool MatchesEventProperties(Dictionary<string, object?> eventProperties)
	{
		if (EventProperties == null)
			return true;

		foreach (var (key, missionValue) in EventProperties)
		{
			if (!eventProperties.TryGetValue(key, out var eventValue) || eventValue == null)
				return false;

			if (!ComparePropertyValue(missionValue, eventValue))
				return false;
		}

		return true;
	}

	private static bool ComparePropertyValue(JsonElement missionValue, object eventValue)
	{
		return missionValue.ValueKind switch
		{
			// Boolean comparison - exact match required
			JsonValueKind.True or JsonValueKind.False =>
				eventValue is bool eventBool && missionValue.GetBoolean() == eventBool,

			// Integer comparison - event value must be >= mission value
			// Supports all integer primitives: byte, sbyte, short, ushort, int, uint, long, ulong
			JsonValueKind.Number when missionValue.TryGetInt64(out var missionLong) =>
				CompareNumericValue(eventValue, missionLong),

			// Floating point comparison - event value must be >= mission value
			// Supports: float, double, decimal
			JsonValueKind.Number when missionValue.TryGetDouble(out var missionDouble) =>
				CompareFloatingPointValue(eventValue, missionDouble),

			// String comparison - case-insensitive contains match
			JsonValueKind.String when missionValue.GetString() is { } missionString =>
				eventValue is string eventString &&
				eventString.Contains(missionString, StringComparison.OrdinalIgnoreCase),

			_ => false
		};
	}

	/// <summary>
	/// Compare integer-based numeric values (supports all integer primitives)
	/// </summary>
	private static bool CompareNumericValue(object eventValue, long missionValue)
	{
		return eventValue switch
		{
			byte v => v >= missionValue,
			sbyte v => v >= missionValue,
			short v => v >= missionValue,
			ushort v => v >= missionValue,
			int v => v >= missionValue,
			uint v => v >= missionValue,
			long v => v >= missionValue,
			ulong v => v >= (ulong)missionValue,
			float v => v >= missionValue,
			double v => v >= missionValue,
			decimal v => v >= missionValue,
			_ => false
		};
	}

	/// <summary>
	/// Compare floating point values (supports float, double, decimal)
	/// </summary>
	private static bool CompareFloatingPointValue(object eventValue, double missionValue)
	{
		return eventValue switch
		{
			float v => v >= missionValue,
			double v => v >= missionValue,
			decimal v => (double)v >= missionValue,
			byte v => v >= missionValue,
			sbyte v => v >= missionValue,
			short v => v >= missionValue,
			ushort v => v >= missionValue,
			int v => v >= missionValue,
			uint v => v >= missionValue,
			long v => v >= missionValue,
			ulong v => v >= missionValue,
			_ => false
		};
	}
}
