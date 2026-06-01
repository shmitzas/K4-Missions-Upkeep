using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Manages player mission data and progress tracking
	/// </summary>
	public sealed class PlayerManager(DatabaseService database, MissionLoader missionLoader, Func<DateTime?> calculateExpiration, Func<MissionPlayer, bool> checkVipStatus)
	{
		private readonly ConcurrentDictionary<ulong, MissionPlayer> _players = new();
		private readonly DatabaseService _database = database;
		private readonly MissionLoader _missionLoader = missionLoader;
		private readonly Func<DateTime?> _calculateExpiration = calculateExpiration;
		private readonly Func<MissionPlayer, bool> _checkVipStatus = checkVipStatus;

		private string _currentMapName = string.Empty;

		/// <summary>
		/// Current number of valid (non-bot, non-spectator) players.
		/// Cached and updated on player add/remove for performance.
		/// </summary>
		private volatile int _activePlayerCount;
		public int ActivePlayerCount => _activePlayerCount;

		private void RefreshActivePlayerCount()
		{
			_activePlayerCount = _players.Values.Count(p => p.IsValid);
		}

		/// <summary>
		/// All registered players
		/// </summary>
		public IEnumerable<MissionPlayer> AllPlayers => _players.Values;

		/// <summary>
		/// Set the current map name
		/// </summary>
		public void SetCurrentMap(string mapName)
		{
			_currentMapName = mapName;
		}

		/// <summary>
		/// Get or create a player record
		/// </summary>
		public MissionPlayer GetOrCreatePlayer(IPlayer player)
		{
			var created = false;
			var missionPlayer = _players.GetOrAdd(player.SteamID, _ =>
			{
				created = true;
				return new MissionPlayer
				{
					SteamId = player.SteamID,
					Player = player
				};
			});

			if (created)
			{
				RefreshActivePlayerCount();

				// Load player data asynchronously
				_ = Task.Run(async () =>
				{
					try { await LoadPlayerDataAsync(missionPlayer); }
					catch (Exception ex) { Core.Logger.LogError(ex, "Failed to load player data for {SteamId}", missionPlayer.SteamId); }
				});
			}

			return missionPlayer;
		}

		/// <summary>
		/// Get existing player record
		/// </summary>
		public MissionPlayer? GetPlayer(IPlayer player)
		{
			return _players.TryGetValue(player.SteamID, out var missionPlayer) ? missionPlayer : null;
		}

		/// <summary>
		/// Get player by SteamID
		/// </summary>
		public MissionPlayer? GetPlayer(ulong steamId)
		{
			return _players.TryGetValue(steamId, out var missionPlayer) ? missionPlayer : null;
		}

		/// <summary>
		/// Remove player from tracking
		/// </summary>
		public void RemovePlayer(ulong steamId)
		{
			if (_players.TryRemove(steamId, out var player))
			{
				RefreshActivePlayerCount();

				if (player.IsLoaded)
				{
					List<PlayerMission> snapshot;
					lock (player.MissionLock)
					{
						snapshot = [.. player.Missions];
					}

					// Save missions on disconnect
					_ = Task.Run(async () =>
					{
						try { await _database.UpdateMissionsAsync(snapshot); }
						catch (Exception ex) { Core.Logger.LogError(ex, "Failed to save missions on disconnect for {SteamId}", steamId); }
					});
				}
			}
		}

		/// <summary>
		/// Load player's missions from database
		/// </summary>
		private async Task LoadPlayerDataAsync(MissionPlayer player)
		{
			try
			{
				var savedMissions = await _database.GetPlayerMissionsAsync(player.SteamId);

				var loadedMissions = new List<PlayerMission>();
				var eventPropertyBackfills = new List<(int Id, Dictionary<string, JsonElement> Props)>();

				foreach (var dbMission in savedMissions)
				{
					// Skip expired missions
					if (dbMission.ExpiresAt.HasValue && dbMission.ExpiresAt.Value < DateTime.Now)
					{
						await _database.RemoveMissionsAsync([dbMission.Id]);
						continue;
					}

					var eventProperties = dbMission.GetEventProperties();

					// Backfill EventProperties from the current template when the persisted
					// row has none. This protects legacy rows assigned before the
					// event_properties column existed (or any row where the JSON is missing)
					// from being treated as filter-less and matching every event.
					if (eventProperties == null)
					{
						var template = _missionLoader.FindTemplate(
							dbMission.Event, dbMission.Target, dbMission.Phrase, dbMission.RewardPhrase, dbMission.Amount);

						if (template?.EventProperties != null && template.EventProperties.Count > 0)
						{
							eventProperties = template.EventProperties;
							eventPropertyBackfills.Add((dbMission.Id, template.EventProperties));
						}
					}

					loadedMissions.Add(new PlayerMission
					{
						Id = dbMission.Id,
						Event = dbMission.Event,
						Target = dbMission.Target,
						Amount = dbMission.Amount,
						Phrase = dbMission.Phrase,
						RewardPhrase = dbMission.RewardPhrase,
						RewardCommands = dbMission.GetRewardCommandsList(),
						Progress = dbMission.Progress,
						IsCompleted = dbMission.Completed,
						ExpiresAt = dbMission.ExpiresAt,
						EventProperties = eventProperties,
						MapName = dbMission.MapName
					});
				}

				if (eventPropertyBackfills.Count > 0)
				{
					try { await _database.BackfillEventPropertiesAsync(eventPropertyBackfills); }
					catch (Exception ex) { Core.Logger.LogError(ex, "Failed to persist event_properties backfill for {SteamId}", player.SteamId); }
				}

				lock (player.MissionLock)
				{
					player.Missions.AddRange(loadedMissions);
				}

				player.IsLoaded = true;

				// Check VIP status and ensure correct mission count on main thread
				Core.Scheduler.NextWorldUpdate(() =>
				{
					if (!player.IsValid)
						return;

					player.IsVip = _checkVipStatus(player);
					EnsureCorrectMissionCount(player);
				});
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to load missions for {SteamId}", player.SteamId);
				player.IsLoaded = true; // Mark as loaded to prevent retry loops
			}
		}

		/// <summary>
		/// Ensure player has the correct number of missions
		/// </summary>
		public void EnsureCorrectMissionCount(MissionPlayer player)
		{
			if (!player.IsLoaded)
				return;

			var vipCount = Config.CurrentValue.MissionAmountVip;
			var requiredCount = (player.IsVip && vipCount > 0) ? vipCount : Config.CurrentValue.MissionAmountNormal;
			int currentCount;
			lock (player.MissionLock)
			{
				currentCount = player.Missions.Count;
			}

			if (currentCount > requiredCount)
			{
				RemoveExcessMissions(player, currentCount - requiredCount);
			}
			else if (currentCount < requiredCount)
			{
				AssignRandomMissions(player, requiredCount - currentCount);
			}
		}

		/// <summary>
		/// Assign random missions to player
		/// </summary>
		private void AssignRandomMissions(MissionPlayer player, int count)
		{
			// Create a simple key for duplicate detection: Event|Target|Amount|Phrase
			static string GetMissionKey(string evt, string target, int amount, string phrase)
				=> $"{evt}|{target}|{amount}|{phrase}";

			HashSet<string> existingKeys;
			lock (player.MissionLock)
			{
				existingKeys = player.Missions
					.Select(m => GetMissionKey(m.Event, m.Target, m.Amount, m.Phrase))
					.ToHashSet();
			}

			var availableMissions = _missionLoader.GetAvailableMissions(player, flag =>
				Core.Permission.PlayerHasPermission(player.SteamId, flag))
				.Where(m => !existingKeys.Contains(GetMissionKey(m.Event, m.Target, m.Amount, m.Phrase)))
				.ToList();

			if (availableMissions.Count == 0)
				return;

			var random = new Random();
			var missionsToAdd = new List<(PlayerMission Mission, DateTime? ExpiresAt)>();

			for (var i = 0; i < count && availableMissions.Count > 0; i++)
			{
				var index = random.Next(availableMissions.Count);
				var definition = availableMissions[index];
				availableMissions.RemoveAt(index);

				var expiresAt = _calculateExpiration();
				var mission = definition.CreatePlayerMission(expiresAt);
				missionsToAdd.Add((mission, expiresAt));
			}

			var steamId = player.SteamId;
			_ = Task.Run(async () =>
			{
				try
				{
					var addedCount = 0;
					foreach (var (mission, expiresAt) in missionsToAdd)
					{
						var missionId = await _database.AddMissionAsync(steamId, mission, expiresAt);
						if (missionId > 0)
						{
							mission.Id = missionId;
							lock (player.MissionLock)
							{
								player.Missions.Add(mission);
							}
							addedCount++;
						}
					}

					if (addedCount > 0)
					{
						Core.Scheduler.NextWorldUpdate(() => NotifyNewMissions(player, addedCount));
					}
				}
				catch (Exception ex)
				{
					Core.Logger.LogError(ex, "Failed to assign missions for {SteamId}", steamId);
				}
			});
		}

		/// <summary>
		/// Remove excess missions from player
		/// </summary>
		private void RemoveExcessMissions(MissionPlayer player, int count)
		{
			List<PlayerMission> toRemove;
			lock (player.MissionLock)
			{
				toRemove = player.Missions
					.OrderByDescending(m => m.Id)
					.Take(count)
					.ToList();

				foreach (var mission in toRemove)
				{
					player.Missions.Remove(mission);
				}
			}

			_ = Task.Run(async () =>
			{
				try { await _database.RemoveMissionsAsync(toRemove.Select(m => m.Id)); }
				catch (Exception ex) { Core.Logger.LogError(ex, "Failed to remove excess missions"); }
			});
		}

		/// <summary>
		/// Notify player of new missions
		/// </summary>
		private void NotifyNewMissions(MissionPlayer player, int count)
		{
			if (!player.IsValid)
				return;

			var command = Config.CurrentValue.MissionCommands.FirstOrDefault() ?? "missions";
			var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
			player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.new_mission", count, command]}");
		}

		/// <summary>
		/// Process an event for all players
		/// </summary>
		public void ProcessEvent(string eventType, string target, IPlayer player, Dictionary<string, object?>? eventProperties = null)
		{
			// Check minimum player requirement
			if (ActivePlayerCount < Config.CurrentValue.MinimumPlayers)
				return;

			var missionPlayer = GetPlayer(player);
			if (missionPlayer == null || !missionPlayer.IsLoaded)
				return;

			ProcessEventForPlayer(missionPlayer, eventType, target, eventProperties);
		}

		/// <summary>
		/// Process an event for a specific player
		/// </summary>
		private void ProcessEventForPlayer(MissionPlayer player, string eventType, string target, Dictionary<string, object?>? eventProperties)
		{
			List<PlayerMission> matchingMissions;
			lock (player.MissionLock)
			{
				matchingMissions = player.Missions
					.Where(m => m.Matches(eventType, target, _currentMapName, eventProperties))
					.ToList();
			}

			foreach (var mission in matchingMissions)
			{
				mission.Progress++;

				if (mission.Progress >= mission.Amount)
				{
					CompleteMission(player, mission);
				}
			}
		}

		/// <summary>
		/// Complete a mission and give rewards.
		/// Reward commands are re-resolved from the current <c>missions.json</c> template
		/// so JSON edits take effect on in-flight player missions. Rewards always run
		/// for the in-memory transition from incomplete -> complete, even if the DB row
		/// was already marked completed by a racing save (otherwise players would silently
		/// lose rewards).
		/// </summary>
		public void CompleteMission(MissionPlayer player, PlayerMission mission)
		{
			if (mission.IsCompleted)
				return;

			mission.IsCompleted = true;

			// Re-resolve reward commands from the current template. Falls back to the
			// values stored on the mission (loaded from DB at assignment time) if the
			// template can no longer be located.
			var template = _missionLoader.FindTemplate(
				mission.Event, mission.Target, mission.Phrase, mission.RewardPhrase, mission.Amount);

			var rewardCommands = (template != null && template.RewardCommands.Count > 0)
				? template.RewardCommands
				: mission.RewardCommands;

			_ = Task.Run(async () =>
			{
				try
				{
					// Best-effort persistence. We no longer bail on a false return:
					// the in-memory transition is authoritative for reward delivery.
					await _database.CompleteMissionAsync(mission.Id);

					Core.Scheduler.NextWorldUpdate(() =>
					{
						if (!player.IsValid)
							return;

						if (rewardCommands.Count == 0)
						{
							Core.Logger.LogWarning(
								"Mission {MissionId} ({Event}/{Target}) completed for {SteamId} but has no reward commands configured.",
								mission.Id, mission.Event, mission.Target, player.SteamId);
						}

						// Execute reward commands
						foreach (var command in rewardCommands)
						{
							var replaced = ReplacePlaceholders(player.Player, command);
							try
							{
								Core.Engine.ExecuteCommand(replaced);
							}
							catch (Exception ex)
							{
								Core.Logger.LogError(ex,
									"Reward command failed for mission {MissionId} ({SteamId}): {Command}",
									mission.Id, player.SteamId, replaced);
							}
						}

						// Notify player
						var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
						player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.complete_mission", mission.Phrase, mission.RewardPhrase]}");

						// Fire completion event
						OnMissionCompleted?.Invoke(player, mission);
					});
				}
				catch (Exception ex)
				{
					Core.Logger.LogError(ex, "Failed to complete mission {MissionId}", mission.Id);
				}
			});
		}

		/// <summary>
		/// Remove a specific mission from player
		/// </summary>
		public void RemoveMission(MissionPlayer player, PlayerMission mission)
		{
			lock (player.MissionLock)
			{
				player.Missions.Remove(mission);
			}

			_ = Task.Run(async () =>
			{
				try { await _database.RemoveMissionsAsync([mission.Id]); }
				catch (Exception ex) { Core.Logger.LogError(ex, "Failed to remove mission {MissionId}", mission.Id); }
			});
		}

		/// <summary>
		/// Increment playtime missions for all active players
		/// </summary>
		public void ProcessPlayTime()
		{
			if (ActivePlayerCount < Config.CurrentValue.MinimumPlayers)
				return;

			foreach (var player in _players.Values.Where(p => p.IsValid && p.IsLoaded))
			{
				List<PlayerMission> playtimeMissions;
				lock (player.MissionLock)
				{
					playtimeMissions = player.Missions
						.Where(m => m.Event == "PlayTime" && !m.IsCompleted)
						.ToList();
				}

				foreach (var mission in playtimeMissions)
				{
					mission.Progress++;

					if (mission.Progress >= mission.Amount)
					{
						CompleteMission(player, mission);
					}
				}
			}
		}

		/// <summary>
		/// Save all player missions to database
		/// </summary>
		public async Task SaveAllPlayersAsync()
		{
			foreach (var player in _players.Values.Where(p => p.IsLoaded))
			{
				List<PlayerMission> snapshot;
				lock (player.MissionLock)
				{
					if (player.Missions.Count == 0)
						continue;
					snapshot = [.. player.Missions];
				}

				await _database.UpdateMissionsAsync(snapshot);
			}
		}

		/// <summary>
		/// Handle map change
		/// </summary>
		public void OnMapChange(string newMapName)
		{
			SetCurrentMap(newMapName);
			RefreshActivePlayerCount();

			// Re-check mission counts (VIP status may have changed)
			foreach (var player in _players.Values.Where(p => p.IsLoaded && p.IsValid))
			{
				player.IsVip = _checkVipStatus(player);
				EnsureCorrectMissionCount(player);
			}
		}

		/// <summary>
		/// Handle expired missions for a player
		/// </summary>
		public void HandleExpiredMissions(MissionPlayer player)
		{
			List<PlayerMission> expired;
			lock (player.MissionLock)
			{
				expired = player.Missions.Where(m => m.ExpiresAt.HasValue && m.ExpiresAt.Value < DateTime.Now).ToList();
				if (expired.Count == 0)
					return;

				foreach (var mission in expired)
				{
					player.Missions.Remove(mission);
				}
			}

			var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
			player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.dailyreset"]}");

			EnsureCorrectMissionCount(player);
		}

		/// <summary>
		/// Clear all player data
		/// </summary>
		public void Clear()
		{
			_players.Clear();
		}

		/// <summary>
		/// Replace placeholders in command strings
		/// </summary>
		private static string ReplacePlaceholders(IPlayer player, string command)
		{
			var replacements = new Dictionary<string, string>
			{
				{ "{slot}", player.Slot.ToString() },
				{ "{userid}", player.PlayerID.ToString() },
				{ "{name}", player.Controller?.PlayerName ?? "Unknown" },
				{ "{steamid64}", player.SteamID.ToString() },
				{ "{steamid}", player.SteamID.ToString() },
				{ "u0022", "\"" },
			};

			foreach (var (key, value) in replacements)
			{
				command = command.Replace(key, value);
			}

			return command;
		}

		/// <summary>
		/// Event fired when a mission is completed
		/// </summary>
		public event Action<MissionPlayer, PlayerMission>? OnMissionCompleted;
	}
}
