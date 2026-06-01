namespace K4Missions;

/// <summary>
/// Defines when missions reset/expire
/// </summary>
public enum ResetMode
{
	/// <summary>Missions reset when the map changes</summary>
	PerMap,

	/// <summary>Missions reset at midnight each day</summary>
	Daily,

	/// <summary>Missions reset at the end of each week (Sunday)</summary>
	Weekly,

	/// <summary>Missions reset at the end of each month</summary>
	Monthly,

	/// <summary>Completed missions are immediately replaced with new ones</summary>
	Instant
}
