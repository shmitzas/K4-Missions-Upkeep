using Dapper;
using Dommel;
using K4Missions.Database.Migrations;
using Microsoft.Extensions.Logging;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Handles mission persistence in database (MySQL, PostgreSQL, SQLite)
	/// </summary>
	public sealed class DatabaseService(string connectionName)
	{
		private readonly string _connectionName = connectionName;

		internal const string TableName = "k4_missions";

		/// <summary>True if database is configured and ready</summary>
		public bool IsEnabled { get; private set; }

		/// <summary>
		/// Initialize database tables using FluentMigrator
		/// </summary>
		public async Task InitializeAsync()
		{
			try
			{
				// Run FluentMigrator migrations
				using var connection = Core.Database.GetConnection(_connectionName);
				MigrationRunner.RunMigrations(connection);

				IsEnabled = true;
				Core.Logger.LogInformation("Database initialized with migrations. Table: {Table}", TableName);
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to initialize database. Missions will not persist.");
				IsEnabled = false;
			}
		}

		/// <summary>
		/// Load player's missions from database
		/// </summary>
		public async Task<List<DbMission>> GetPlayerMissionsAsync(ulong steamId)
		{
			if (!IsEnabled)
				return [];

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var results = await connection.SelectAsync<DbMission>(m => m.SteamId64 == (long)steamId);
				return results.ToList();
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to load missions for {SteamId}", steamId);
				return [];
			}
		}

		/// <summary>
		/// Add a new mission to player's record
		/// </summary>
		public async Task<int> AddMissionAsync(ulong steamId, PlayerMission mission, DateTime? expiresAt)
		{
			if (!IsEnabled)
				return -1;

			try
			{
				var dbMission = DbMission.FromPlayerMission(steamId, mission, expiresAt);

				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var id = await connection.InsertAsync(dbMission);
				return Convert.ToInt32(id);
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to add mission for {SteamId}", steamId);
				return -1;
			}
		}

		/// <summary>
		/// Batch update multiple missions
		/// </summary>
		public async Task UpdateMissionsAsync(IEnumerable<PlayerMission> missions)
		{
			if (!IsEnabled)
				return;

			var missionList = missions.Where(m => m.Id > 0).ToList();
			if (missionList.Count == 0)
				return;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				foreach (var mission in missionList)
				{
					await connection.ExecuteAsync(
						$"UPDATE {TableName} SET progress = @Progress, completed = @Completed WHERE id = @Id",
						new { mission.Id, mission.Progress, Completed = mission.IsCompleted });
				}
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to batch update missions");
			}
		}

		/// <summary>
		/// Remove missions by IDs
		/// </summary>
		public async Task RemoveMissionsAsync(IEnumerable<int> missionIds)
		{
			if (!IsEnabled)
				return;

			var ids = missionIds.Where(id => id > 0).ToList();
			if (ids.Count == 0)
				return;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				await connection.DeleteMultipleAsync<DbMission>(m => ids.Contains(m.Id));
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to remove missions");
			}
		}

		/// <summary>
		/// Mark a mission as completed. Idempotent: returns true on successful persistence
		/// regardless of whether the row was already marked completed in the database.
		/// Only returns false on a hard failure (disabled / invalid id / exception).
		/// </summary>
		public async Task<bool> CompleteMissionAsync(int missionId)
		{
			if (!IsEnabled || missionId <= 0)
				return false;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var affected = await connection.ExecuteAsync(
					$"UPDATE {TableName} SET completed = @Completed WHERE id = @Id",
					new { Id = missionId, Completed = true });

				// affected == 0 means the row no longer exists (e.g. expired/reset cleanup).
				return affected > 0;
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to complete mission {MissionId}", missionId);
				return false;
			}
		}

		/// <summary>
		/// Backfill <c>reward_commands</c> for incomplete mission rows that were inserted
		/// before <c>missions.json</c> had reward commands configured (or that lost them
		/// due to a previous schema/serialization bug). Rows whose template can no longer
		/// be located are left untouched.
		/// </summary>
		public async Task<int> BackfillMissingRewardCommandsAsync(MissionLoader missionLoader)
		{
			if (!IsEnabled)
				return 0;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var stale = (await connection.QueryAsync<DbMission>(
					$"SELECT * FROM {TableName} WHERE completed = @Completed AND (reward_commands IS NULL OR reward_commands = '')",
					new { Completed = false })).ToList();

				if (stale.Count == 0)
					return 0;

				var repaired = 0;
				foreach (var row in stale)
				{
					var template = missionLoader.FindTemplate(row.Event, row.Target, row.Phrase, row.RewardPhrase, row.Amount);
					if (template == null || template.RewardCommands.Count == 0)
						continue;

					var joined = string.Join("|", template.RewardCommands);
					var affected = await connection.ExecuteAsync(
						$"UPDATE {TableName} SET reward_commands = @RewardCommands WHERE id = @Id",
						new { Id = row.Id, RewardCommands = joined });

					if (affected > 0)
						repaired++;
				}

				if (repaired > 0)
				{
					Core.Logger.LogInformation(
						"Backfilled reward_commands for {Repaired}/{Total} stale mission rows.",
						repaired, stale.Count);
				}

				return repaired;
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to backfill missing reward_commands");
				return 0;
			}
		}

		/// <summary>
		/// Persist a per-player set of event_properties backfills resolved from the
		/// current <c>missions.json</c> template. Used by the player load path to
		/// repair legacy rows whose event_properties column is NULL.
		/// </summary>
		public async Task BackfillEventPropertiesAsync(IEnumerable<(int Id, Dictionary<string, System.Text.Json.JsonElement> Props)> updates)
		{
			if (!IsEnabled)
				return;

			var list = updates.Where(u => u.Id > 0 && u.Props != null && u.Props.Count > 0).ToList();
			if (list.Count == 0)
				return;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				foreach (var (id, props) in list)
				{
					var json = DbMission.SerializeEventProperties(props);
					if (string.IsNullOrEmpty(json))
						continue;

					await connection.ExecuteAsync(
						$"UPDATE {TableName} SET event_properties = @EventProperties WHERE id = @Id AND (event_properties IS NULL OR event_properties = '')",
						new { Id = id, EventProperties = json });
				}
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to backfill event_properties");
			}
		}

		/// <summary>
		/// Startup sweep that backfills <c>event_properties</c> for any incomplete mission
		/// rows whose column is NULL/empty but whose current template defines property
		/// filters. Prevents legacy rows from matching unrelated events
		/// (e.g. AK kills counting toward an "SSG08 headshot" mission).
		/// </summary>
		public async Task<int> BackfillMissingEventPropertiesAsync(MissionLoader missionLoader)
		{
			if (!IsEnabled)
				return 0;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var stale = (await connection.QueryAsync<DbMission>(
					$"SELECT * FROM {TableName} WHERE completed = @Completed AND (event_properties IS NULL OR event_properties = '')",
					new { Completed = false })).ToList();

				if (stale.Count == 0)
					return 0;

				var repaired = 0;
				foreach (var row in stale)
				{
					var template = missionLoader.FindTemplate(row.Event, row.Target, row.Phrase, row.RewardPhrase, row.Amount);
					if (template?.EventProperties == null || template.EventProperties.Count == 0)
						continue;

					var json = DbMission.SerializeEventProperties(template.EventProperties);
					if (string.IsNullOrEmpty(json))
						continue;

					var affected = await connection.ExecuteAsync(
						$"UPDATE {TableName} SET event_properties = @EventProperties WHERE id = @Id",
						new { Id = row.Id, EventProperties = json });

					if (affected > 0)
						repaired++;
				}

				if (repaired > 0)
				{
					Core.Logger.LogInformation(
						"Backfilled event_properties for {Repaired}/{Total} stale mission rows.",
						repaired, stale.Count);
				}

				return repaired;
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to backfill missing event_properties");
				return 0;
			}
		}

		/// <summary>
		/// Get all steam IDs with expired missions
		/// </summary>
		public async Task<List<ulong>> GetPlayersWithExpiredMissionsAsync()
		{
			if (!IsEnabled)
				return [];

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				var expiredMissions = await connection.SelectAsync<DbMission>(m => m.ExpiresAt != null && m.ExpiresAt < DateTime.Now);
				return expiredMissions.Select(m => (ulong)m.SteamId64).Distinct().ToList();
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to get players with expired missions");
				return [];
			}
		}

		/// <summary>
		/// Cleanup expired missions from database
		/// </summary>
		public async Task CleanupExpiredMissionsAsync()
		{
			if (!IsEnabled)
				return;

			try
			{
				using var connection = Core.Database.GetConnection(_connectionName);
				connection.Open();

				await connection.DeleteMultipleAsync<DbMission>(m => m.ExpiresAt != null && m.ExpiresAt < DateTime.Now);
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to cleanup expired missions");
			}
		}
	}
}
