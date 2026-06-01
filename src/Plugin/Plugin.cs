using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Translation;

namespace K4Missions;

[PluginMetadata(Id = "k4.missions", Version = "1.0.6", Name = "K4 - Missions", Author = "K4ryuu", Description = "A dynamic mission system for Counter-Strike 2 using SwiftlyS2 framework.")]
public sealed partial class Plugin(ISwiftlyCore core) : BasePlugin(core)
{
	/// <summary>Static Core reference for nested classes</summary>
	public static new ISwiftlyCore Core { get; private set; } = null!;

	public static IOptionsMonitor<PluginConfig> Config { get; private set; } = null!;
	private DatabaseService _database = null!;
	private MissionLoader _missionLoader = null!;
	private PlayerManager _playerManager = null!;
	private ResetService _resetService = null!;
	private WebhookService? _webhookService;

	private CancellationTokenSource? _playtimeTimerCts;
	private readonly Dictionary<string, HashSet<string>> _registeredEvents = [];

	// Reflection caches to avoid repeated reflection on every game event
	private static readonly ConcurrentDictionary<Type, PropertyInfo?> _accessorPropertyCache = new();
	private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _playerPropertyCache = new();
	private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _eventPropertiesCache = new();

	public override void Load(bool hotReload)
	{
		Core = base.Core;

		LoadConfiguration();
		InitializeServices();
		RegisterMissionEvents();
		RegisterCommands();
		RegisterEventHandlers();

		if (hotReload)
		{
			HandleHotReload();
		}
	}

	public override void Unload()
	{
		_playtimeTimerCts?.Cancel();
		_playtimeTimerCts = null;

		_resetService.StopExpirationTimer();
		_webhookService?.Dispose();

		try
		{
			Task.Run(async () => await _playerManager.SaveAllPlayersAsync()).Wait(TimeSpan.FromSeconds(5));
		}
		catch (Exception ex)
		{
			Core.Logger.LogError(ex, "Failed to save players during unload");
		}

		_playerManager.Clear();
	}

	private static void LoadConfiguration()
	{
		const string ConfigFileName = "config.json";
		const string ConfigSection = "K4Missions";

		Core.Configuration
			.InitializeJsonWithModel<PluginConfig>(ConfigFileName, ConfigSection)
			.Configure(builder =>
			{
				builder.AddJsonFile(ConfigFileName, optional: false, reloadOnChange: true);
			});

		ServiceCollection services = new();
		services.AddSwiftly(Core)
			.AddOptionsWithValidateOnStart<PluginConfig>()
			.BindConfiguration(ConfigSection);

		var provider = services.BuildServiceProvider();
		Config = provider.GetRequiredService<IOptionsMonitor<PluginConfig>>();

		if (Config.CurrentValue.MissionAmountNormal > Config.CurrentValue.MissionAmountVip)
		{
			Core.Logger.LogWarning("Normal mission amount ({Normal}) is higher than VIP amount ({VIP}). This may cause issues.",
				Config.CurrentValue.MissionAmountNormal, Config.CurrentValue.MissionAmountVip);
		}
	}

	private void InitializeServices()
	{
		_database = new DatabaseService(Config.CurrentValue.DatabaseConnection);
		_missionLoader = new MissionLoader();
		_missionLoader.LoadFromFile(Core.PluginPath);

		_playerManager = new PlayerManager(
			_database,
			_missionLoader,
			() => _resetService?.CalculateExpirationDate(),
			CheckVipStatus);

		if (!string.IsNullOrEmpty(Config.CurrentValue.WebhookUrl))
		{
			_webhookService = new WebhookService(Core.PluginPath);
		}

		_resetService = new ResetService(_database, _playerManager, _webhookService);

		_playerManager.OnMissionCompleted += HandleMissionCompleted;

		Task.Run(async () =>
		{
			try
			{
				await _database.InitializeAsync();
				await _database.BackfillMissingRewardCommandsAsync(_missionLoader);
				await _database.BackfillMissingEventPropertiesAsync(_missionLoader);
				await _resetService.CheckForExpiredMissionsAsync();
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to initialize database or check expired missions");
			}
		});

		_resetService.StartExpirationTimer();

		// playtime missions tick every minute
		_playtimeTimerCts = Core.Scheduler.RepeatBySeconds(60f, () =>
		{
			_playerManager.ProcessPlayTime();
		});
	}

	// Dynamically registers event handlers based on missions.json
	private void RegisterMissionEvents()
	{
		var missions = _missionLoader.GetAllMissions();

		foreach (var mission in missions)
		{
			if (mission.Event == "PlayTime")
				continue;

			if (_registeredEvents.TryGetValue(mission.Event, out var targets))
			{
				targets.Add(mission.Target);
				continue;
			}

			if (RegisterEventForMission(mission))
			{
				_registeredEvents[mission.Event] = [mission.Target];
			}
		}

		Core.Logger.LogInformation("Dynamically registered {Count} mission event types.", _registeredEvents.Count);
	}

	private bool RegisterEventForMission(MissionDefinition mission)
	{
		// Find event type from loaded assemblies
		var eventType = AppDomain.CurrentDomain.GetAssemblies()
			.Select(a => a.GetType($"SwiftlyS2.Shared.GameEventDefinitions.{mission.Event}"))
			.FirstOrDefault(t => t != null);

		if (eventType == null)
		{
			Core.Logger.LogWarning("Event type {Event} not found.", mission.Event);
			return false;
		}

		var gameEventInterface = eventType.GetInterfaces()
			.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IGameEvent<>));

		if (gameEventInterface == null)
		{
			Core.Logger.LogWarning("Event type {Event} does not implement IGameEvent<T>.", mission.Event);
			return false;
		}

		try
		{
			var hookPostMethod = typeof(IGameEventService).GetMethod("HookPost");
			if (hookPostMethod == null)
				return false;

			var genericHookPost = hookPostMethod.MakeGenericMethod(eventType);
			var handlerMethod = GetType().GetMethod(nameof(OnGenericEvent), BindingFlags.NonPublic | BindingFlags.Instance);
			if (handlerMethod == null)
				return false;

			var genericHandler = handlerMethod.MakeGenericMethod(eventType);
			var delegateType = typeof(IGameEventService.GameEventHandler<>).MakeGenericType(eventType);
			var handlerDelegate = Delegate.CreateDelegate(delegateType, this, genericHandler);

			genericHookPost.Invoke(Core.GameEvent, [handlerDelegate]);
			return true;
		}
		catch (Exception ex)
		{
			Core.Logger.LogError(ex, "Failed to register event handler for {Event}.", mission.Event);
			return false;
		}
	}

	private HookResult OnGenericEvent<T>(T @event) where T : IGameEvent<T>
	{
		var eventType = typeof(T).Name;

		// Check warmup restriction
		if (!Config.CurrentValue.AllowProgressDuringWarmup)
		{
			var gameRules = Core.EntitySystem.GetGameRules();
			if (gameRules?.WarmupPeriod == true)
				return HookResult.Continue;
		}

		// Round end is special - has winner/loser logic
		if (eventType == "EventRoundEnd")
		{
			HandleRoundEndEvent(@event);
			return HookResult.Continue;
		}

		if (!_registeredEvents.TryGetValue(eventType, out var targets))
			return HookResult.Continue;

		var properties = ExtractEventProperties(@event);

		// Cache accessor property lookup for this event type
		var accessorProp = _accessorPropertyCache.GetOrAdd(typeof(T), static type =>
			type.GetProperty("Accessor")
				?? type.GetInterfaces()
					.SelectMany(i => new[] { i }.Concat(i.GetInterfaces()))
					.Select(i => i.GetProperty("Accessor"))
					.FirstOrDefault(p => p != null));

		foreach (var target in targets)
		{
			IPlayer? player = null;

			// First try Accessor.GetPlayer (most reliable method)
			if (accessorProp?.GetValue(@event) is IGameEventAccessor accessor)
			{
				player = accessor.GetPlayer(target.ToLower());
			}

			// Fallback: Try {Target}Player property (e.g., UserIdPlayer) - cached
			if (player == null)
			{
				var playerProp = _playerPropertyCache.GetOrAdd((typeof(T), target), static key =>
					key.Item1.GetProperty($"{key.Item2}Player"));
				player = playerProp?.GetValue(@event) as IPlayer;
			}

			if (player?.IsValid != true || player.IsFakeClient)
				continue;

			if (Config.CurrentValue.EventDebugLogs)
			{
				foreach (var (key, propValue) in properties)
				{
					Core.Logger.LogInformation("[{Event}] {Property}: {Value}", eventType, key, propValue);
				}
			}

			_playerManager.ProcessEvent(eventType, target, player, properties);
		}

		return HookResult.Continue;
	}

	private static Dictionary<string, object?> ExtractEventProperties<T>(T @event)
	{
		var cachedProps = _eventPropertiesCache.GetOrAdd(typeof(T), static type =>
			type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p.CanRead)
				.ToArray());

		var properties = new Dictionary<string, object?>(cachedProps.Length);

		foreach (var prop in cachedProps)
		{
			properties[prop.Name] = prop.GetValue(@event);
		}

		return properties;
	}

	private void HandleRoundEndEvent<T>(T @event)
	{
		var winnerProp = typeof(T).GetProperty("Winner");
		if (winnerProp == null)
			return;

		var winnerValue = winnerProp.GetValue(@event);
		var winner = Convert.ToInt32(winnerValue ?? 0);
		if (winner <= (int)Team.Spectator)
			return;

		foreach (var missionPlayer in _playerManager.AllPlayers.Where(p => p.IsValid))
		{
			var playerTeam = (int)(missionPlayer.Player.Controller?.Team ?? Team.None);
			if (playerTeam <= (int)Team.Spectator)
				continue;

			var target = playerTeam == winner ? "winner" : "loser";
			_playerManager.ProcessEvent("EventRoundEnd", target, missionPlayer.Player, null);
		}
	}

	private void RegisterCommands()
	{
		if (Config.CurrentValue.MissionCommands.Count > 0)
		{
			var primary = Config.CurrentValue.MissionCommands[0];
			Core.Command.RegisterCommand(primary, OnMissionCommand);

			foreach (var alias in Config.CurrentValue.MissionCommands.Skip(1))
			{
				Core.Command.RegisterCommandAlias(primary, alias);
			}
		}

		Core.Command.RegisterCommand("mission_reload", OnMissionReloadCommand, permission: "k4.missions.admin");
		Core.Command.RegisterCommandAlias("mission_reload", "missions_reload");
	}

	private void OnMissionReloadCommand(ICommandContext ctx)
	{
		var count = ReloadMissions();

		if (ctx.IsSentByPlayer && ctx.Sender is { IsValid: true } sender)
		{
			var localizer = Core.Translation.GetPlayerLocalizer(sender);
			sender.SendChat($"{localizer["k4.general.prefix"]} Reloaded {count} mission definitions.");
		}
		else
		{
			ctx.Reply($"Reloaded {count} mission definitions.");
		}
	}

	/// <summary>
	/// Re-reads missions.json, registers any newly-introduced event hooks,
	/// and tops up online players' mission counts.
	/// </summary>
	private int ReloadMissions()
	{
		try
		{
			_missionLoader.LoadFromFile(Core.PluginPath);

			// Register hooks for any newly-introduced event types (idempotent for existing ones)
			RegisterMissionEvents();

			// Top up online players to match (possibly changed) definitions / config amounts
			Core.Scheduler.NextWorldUpdate(() =>
			{
				foreach (var player in _playerManager.AllPlayers)
				{
					if (player.IsValid && player.IsLoaded)
						_playerManager.EnsureCorrectMissionCount(player);
				}
			});

			return _missionLoader.GetAllMissions().Count;
		}
		catch (Exception ex)
		{
			Core.Logger.LogError(ex, "Failed to reload missions.");
			return 0;
		}
	}

	private void RegisterEventHandlers()
	{
		// Player lifecycle
		Core.GameEvent.HookPost<EventPlayerActivate>(OnPlayerActivate);
		Core.GameEvent.HookPost<EventPlayerDisconnect>(OnPlayerDisconnect);

		// Round events for saving
		Core.GameEvent.HookPost<EventRoundEnd>(OnRoundEndSave);

		// Map events
		Core.Event.OnMapLoad += OnMapLoad;
	}

	private HookResult OnPlayerActivate(EventPlayerActivate @event)
	{
		var player = Core.PlayerManager.GetPlayer(@event.UserId);

		if (player?.IsValid != true || player.IsFakeClient)
			return HookResult.Continue;

		_playerManager.GetOrCreatePlayer(player);
		return HookResult.Continue;
	}

	private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
	{
		var player = Core.PlayerManager.GetPlayer(@event.UserId);

		if (player != null)
		{
			_playerManager.RemovePlayer(player.SteamID);
		}

		return HookResult.Continue;
	}

	private HookResult OnRoundEndSave(EventRoundEnd @event)
	{
		// Save all player missions at round end
		_ = Task.Run(async () =>
		{
			try { await _playerManager.SaveAllPlayersAsync(); }
			catch (Exception ex) { Core.Logger.LogError(ex, "Failed to save player missions at round end"); }
		});
		return HookResult.Continue;
	}

	private void OnMapLoad(IOnMapLoadEvent @event)
	{
		// Restart expiration timer on map change
		_resetService.StopExpirationTimer();
		_resetService.StartExpirationTimer();

		// Reload mission definitions from disk so file edits take effect on map change
		ReloadMissions();

		Core.Scheduler.DelayBySeconds(0.1f, () =>
		{
			_playerManager.OnMapChange(@event.MapName);
		});
	}

	private void OnMissionCommand(ICommandContext ctx)
	{
		var player = ctx.Sender;
		if (player == null || !player.IsValid)
			return;

		var missionPlayer = _playerManager.GetPlayer(player);
		if (missionPlayer == null || !missionPlayer.IsLoaded)
		{
			var localizer = Core.Translation.GetPlayerLocalizer(player);
			player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.loading"]}");
			return;
		}

		PrintMissionsToPlayer(missionPlayer);
	}

	private void PrintMissionsToPlayer(MissionPlayer player)
	{
		var localizer = Core.Translation.GetPlayerLocalizer(player.Player);

		List<PlayerMission> missions;
		lock (player.MissionLock)
		{
			missions = [.. player.Missions];
		}

		if (missions.Count == 0)
		{
			ShowMissionsMenu(player, localizer, []);
			return;
		}

		ShowMissionsMenu(player, localizer, missions);
	}

	private void ShowMissionsMenu(MissionPlayer player, ILocalizer localizer, List<PlayerMission> missions)
	{
		var resetInfo = GetResetTimeInfo(player, localizer);

		var menuBuilder = Core.MenusAPI
			.CreateBuilder()
			.Design.SetMenuTitle(localizer["k4.missions.menu.title"])
			.Design.SetMenuTitleVisible(true)
			.Design.SetMenuFooterVisible(true)
			.Design.SetGlobalScrollStyle(MenuOptionScrollStyle.LinearScroll)
			.SetPlayerFrozen(false);

		// player limit warning
		if (_playerManager.ActivePlayerCount < Config.CurrentValue.MinimumPlayers)
		{
			menuBuilder.AddOption(new TextMenuOption($"<font color='#FF6B6B'>⚠ {localizer["k4.missions.menu.playerlimit", Config.CurrentValue.MinimumPlayers]}</font>"));
		}

		// reset info at top
		if (!string.IsNullOrEmpty(resetInfo))
		{
			menuBuilder.AddOption(new TextMenuOption($"<font color='#4A90D9'>⏱ {resetInfo}</font>"));
		}

		if (missions.Count == 0)
		{
			menuBuilder.AddOption(new TextMenuOption($"<font color='#888888'>{localizer["k4.missions.no_missions"]}</font>"));
		}
		else
		{
			var counter = 1;
			foreach (var mission in missions)
			{
				var idx = counter;
				var m = mission;

				string statusText;
				string textColor;
				if (mission.IsCompleted)
				{
					statusText = "✓";
					textColor = "#4CAF50";
				}
				else
				{
					var percent = (int)((float)mission.Progress / mission.Amount * 100);
					statusText = $"{percent}%";
					textColor = "#FFFFFF";
				}

				var submenuOption = new SubmenuMenuOption(
					$"<font color='{textColor}'>{mission.Phrase} | {statusText}</font>",
					() =>
					{
						var detailBuilder = Core.MenusAPI
							.CreateBuilder()
							.Design.SetMenuTitle(m.Phrase)
							.Design.SetMenuTitleVisible(true)
							.Design.SetMenuFooterVisible(true)
							.SetPlayerFrozen(false);

						detailBuilder.AddOption(new TextMenuOption($"<font color='#AAAAAA'>{localizer["k4.missions.detail.task", m.Phrase]}</font>"));

						if (m.IsCompleted)
						{
							detailBuilder.AddOption(new TextMenuOption($"<font color='#4CAF50'>{localizer["k4.missions.detail.completed"]}</font>"));
						}
						else
						{
							var pct = (int)((float)m.Progress / m.Amount * 100);
							detailBuilder.AddOption(new TextMenuOption($"<font color='#FFD700'>{localizer["k4.missions.detail.progress", m.Progress, m.Amount]} ({pct}%)</font>"));
						}

						detailBuilder.AddOption(new TextMenuOption($"<font color='#4A90D9'>{localizer["k4.missions.detail.reward", m.RewardPhrase]}</font>"));

						return detailBuilder.Build();
					});
				menuBuilder.AddOption(submenuOption);

				counter++;
			}

			// locked VIP slots
			if (!player.IsVip && Config.CurrentValue.MissionAmountVip > Config.CurrentValue.MissionAmountNormal)
			{
				for (var i = counter; i <= Config.CurrentValue.MissionAmountVip; i++)
				{
					menuBuilder.AddOption(new TextMenuOption($"<font color='#666666'>🔒 {localizer["k4.missions.menu.mission", i]} - {localizer["k4.missions.menu.vip_locked"]}</font>"));
				}
			}
		}

		var menu = menuBuilder.Build();
		Core.MenusAPI.OpenMenuForPlayer(player.Player, menu);
	}

	private static void PrintMissionDetails(MissionPlayer player, ILocalizer localizer, PlayerMission mission, int missionNumber)
	{
		var prefix = localizer["k4.general.prefix"];

		// mission header
		player.Player.SendChat($"{prefix} {localizer["k4.missions.detail.header", missionNumber]}");

		// mission task
		player.Player.SendChat($"{prefix} {localizer["k4.missions.detail.task", mission.Phrase]}");

		// progress
		if (mission.IsCompleted)
		{
			player.Player.SendChat($"{prefix} {localizer["k4.missions.detail.completed"]}");
		}
		else
		{
			player.Player.SendChat($"{prefix} {localizer["k4.missions.detail.progress", mission.Progress, mission.Amount]}");
		}

		// reward
		player.Player.SendChat($"{prefix} {localizer["k4.missions.detail.reward", mission.RewardPhrase]}");
	}

	private string GetResetTimeInfo(MissionPlayer player, ILocalizer localizer)
	{
		if (Config.CurrentValue.ResetMode == ResetMode.PerMap)
		{
			return localizer["k4.missions.menu.reset.map"].ToString();
		}

		if (Config.CurrentValue.ResetMode is ResetMode.Instant)
		{
			return localizer["k4.missions.menu.reset.instant"].ToString();
		}

		DateTime? expiresAt;
		lock (player.MissionLock)
		{
			expiresAt = player.Missions.FirstOrDefault()?.ExpiresAt;
		}
		if (!expiresAt.HasValue)
			return string.Empty;

		var (days, hours, minutes) = ResetService.GetTimeUntilExpiration(expiresAt.Value);

		return Config.CurrentValue.ResetMode switch
		{
			ResetMode.Weekly or ResetMode.Monthly =>
				localizer["k4.missions.menu.reset.days", days, hours, minutes].ToString(),
			_ => localizer["k4.missions.menu.reset.time", hours, minutes].ToString()
		};
	}

	private void HandleMissionCompleted(MissionPlayer player, PlayerMission mission)
	{
		// Handle webhook
		if (_webhookService != null && !string.IsNullOrEmpty(Config.CurrentValue.WebhookUrl))
		{
			var localizer = Core.Translation.GetPlayerLocalizer(player.Player);

			_ = Task.Run(async () =>
			{
				try
				{
					await _webhookService.SendMissionCompleteAsync(
						Config.CurrentValue.WebhookUrl,
						player,
						mission,
						key => localizer[key]);

					if (player.AllMissionsCompleted)
					{
						int totalMissions;
						lock (player.MissionLock)
						{
							totalMissions = player.Missions.Count;
						}

						await _webhookService.SendAllMissionsCompleteAsync(Config.CurrentValue.WebhookUrl, player, totalMissions);
					}
				}
				catch (Exception ex)
				{
					Core.Logger.LogError(ex, "Failed to send mission completion webhook");
				}
			});
		}

		// Handle instant reset mode
		_resetService.OnMissionCompleted(player, mission);
	}

	private bool CheckVipStatus(MissionPlayer player)
	{
		// Check permission flags
		if (Config.CurrentValue.VipFlags.Any(flag => Core.Permission.PlayerHasPermission(player.SteamId, flag)))
			return true;

		// Check domain in name
		var playerName = player.Player.Controller?.PlayerName ?? string.Empty;
		if (!string.IsNullOrEmpty(Config.CurrentValue.VipNameDomain) &&
			playerName.Contains(Config.CurrentValue.VipNameDomain, StringComparison.OrdinalIgnoreCase))
			return true;

		return false;
	}

	private void HandleHotReload()
	{
		Core.Scheduler.NextWorldUpdate(() =>
		{
			// Register existing players
			foreach (var player in Core.PlayerManager.GetAllPlayers())
			{
				if (player.IsValid && !player.IsFakeClient)
				{
					_playerManager.GetOrCreatePlayer(player);
				}
			}
		});
	}
}
