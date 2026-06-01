namespace K4Missions;

public sealed class PluginConfig
{
	/// <summary>
	/// Database connection name from SwiftlyS2 database.json
	/// </summary>
	public string DatabaseConnection { get; set; } = "host";

	/// <summary>
	/// Commands to check missions (without prefix)
	/// </summary>
	public List<string> MissionCommands { get; set; } = ["mission", "missions"];

	/// <summary>
	/// Domain in player name that grants VIP status
	/// </summary>
	public string? VipNameDomain { get; set; } = null;

	/// <summary>
	/// Minimum players required for mission progress
	/// </summary>
	public int MinimumPlayers { get; set; } = 4;

	/// <summary>
	/// Number of missions for normal players
	/// </summary>
	public int MissionAmountNormal { get; set; } = 11;

	/// <summary>
	/// Number of missions for VIP players
	/// </summary>
	public int MissionAmountVip { get; set; } = 0;

	/// <summary>
	/// Flags that grant VIP status (any one of these)
	/// </summary>
	public List<string> VipFlags { get; set; } = ["vipcore.access"];

	/// <summary>
	/// Enable debug logging for events, you can create missions by this values
	/// </summary>
	public bool EventDebugLogs { get; set; } = false;

	/// <summary>
	/// Allow mission progress during warmup
	/// </summary>
	public bool AllowProgressDuringWarmup { get; set; } = false;

	/// <summary>
	/// Mission reset mode
	/// </summary>
	public ResetMode ResetMode { get; set; } = ResetMode.Weekly;

	/// <summary>
	/// Discord webhook URL for notifications (empty = disabled)
	/// </summary>
	public string WebhookUrl { get; set; } = "";
}
