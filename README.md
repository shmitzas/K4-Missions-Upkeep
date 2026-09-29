<div align="center">
  <h1 align="center">KitsuneLab©</h1>
  <h3 align="center">K4-Missions</h3>
  <a align="center">A dynamic mission system for Counter-Strike 2 using SwiftlyS2 framework. Create custom missions with configurable events, rewards, and reset modes.</a>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/Shmitzas/K4-Missions-Upkeep/total?style=flat&logo=github&cacheSeconds=3600" alt="Downloads">
  <img src="https://img.shields.io/github/stars/Shmitzas/K4-Missions-Upkeep?style=flat&logo=github&cacheSeconds=3600" alt="Stars">
  <img src="https://img.shields.io/github/license/Shmitzas/K4-Missions-Upkeep" alt="License">
</p>

# Important notice!
> [!IMPORTANT]  
> [K4ryuu](https://github.com/K4ryuu) is the creator of this plugin.<br>
> Since he is no longer maintaining his CS2 plugins, I forked some of them and maintain them ONLY FOR BUG FIXES!

---

### Dependencies

- [**SwiftlyS2**](https://github.com/swiftly-solution/swiftlys2): Server plugin framework for Counter-Strike 2
- **Database**: One of the following supported databases:
  - **MySQL / MariaDB** - Recommended for production
  - **PostgreSQL** - Full support
  - **SQLite** - Great for single-server setups

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- INSTALLATION -->

## Installation

1. Install [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2) on your server
2. Configure your database connection in SwiftlyS2's `database.jsonc` (MySQL, PostgreSQL, or SQLite)
3. [Download the latest release](https://github.com/shmitzas/K4-Missions-Upkeep/releases/latest)
4. Extract to your server's `swiftlys2/plugins/` directory
5. Configure `config.json` and `missions.json` in the plugin folder
6. Restart your server - database tables will be created automatically

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- FEATURES -->

## Features

- **Dynamic Mission System**: Create unlimited custom missions via `missions.json`
- **Event-Based Progress**: Track kills, assists, MVP awards, bomb plants/defuses, hostage rescues, round wins, and playtime
- **Event Property Filters**: Filter missions by weapon, headshot, map, and more
- **VIP Support**: Configure different mission counts for VIP and regular players
- **Multiple Reset Modes**: Daily, Weekly, Monthly, Per-Map, or Instant mission resets
- **Reward Commands**: Execute any server command as mission reward
- **Discord Webhooks**: Send notifications on mission completions
- **Minimum Player Requirement**: Prevent mission farming on empty servers
- **Warmup Protection**: Optionally disable progress during warmup

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- CONFIGURATION -->

## Configuration

### config.json

```json
{
  "K4Missions": {
    "DatabaseConnection": "host",
    "MissionCommands": ["mission", "missions"],
    "VipNameDomain": null,
    "MinimumPlayers": 4,
    "MissionAmountNormal": 1,
    "MissionAmountVip": 3,
    "VipFlags": ["any.vip.flag"],
    "EventDebugLogs": false,
    "AllowProgressDuringWarmup": false,
    "ResetMode": "Daily",
    "WebhookUrl": ""
  }
}
```

### missions.json Example

```json
[
  {
    "Event": "EventPlayerDeath",
    "Target": "Attacker",
    "Amount": 10,
    "RewardCommands": ["sw_givecredits {steamid64} 30"],
    "RewardPhrase": "30 Credits",
    "Phrase": "Kill 10 players"
  },
  {
    "Event": "EventPlayerDeath",
    "EventProperties": {
      "Weapon": "ak47",
      "Headshot": true
    },
    "Target": "Attacker",
    "Amount": 5,
    "RewardCommands": ["sw_givecredits {steamid64} 50"],
    "RewardPhrase": "50 Credits",
    "Phrase": "Get 5 AK47 headshot kills"
  },
  {
    "Event": "EventRoundMvp",
    "Target": "Userid",
    "Amount": 3,
    "RewardCommands": ["sw_givecredits {steamid64} 50"],
    "RewardPhrase": "50 Credits",
    "Phrase": "Get 3 MVP awards"
  },
  {
    "Event": "EventRoundEnd",
    "Target": "winner",
    "Amount": 5,
    "RewardCommands": ["sw_givecredits {steamid64} 25"],
    "RewardPhrase": "25 Credits",
    "Phrase": "Win 5 rounds"
  },
  {
    "Event": "PlayTime",
    "Target": "Userid",
    "Amount": 30,
    "RewardCommands": ["sw_givecredits {steamid64} 60"],
    "RewardPhrase": "60 Credits",
    "Phrase": "Play 30 minutes on the server"
  }
]
```

### Available Events

The plugin supports **all CS2 game events** dynamically. You can use any event from the [CS2 Game Events List](https://cs2.poggu.me/dumped-data/game-events/).

**Common events for missions:**

| Event                 | Target                 | Description                                 |
| --------------------- | ---------------------- | ------------------------------------------- |
| `EventPlayerDeath`    | `Attacker`, `Assister` | Player kills/assists                        |
| `EventRoundMvp`       | `Userid`               | MVP awards                                  |
| `EventBombPlanted`    | `Userid`               | Bomb plants                                 |
| `EventBombDefused`    | `Userid`               | Bomb defuses                                |
| `EventHostageRescued` | `Userid`               | Hostage rescues                             |
| `EventGrenadeThrown`  | `Userid`               | Grenade throws                              |
| `EventRoundEnd`       | `winner`, `loser`      | Round wins/losses                           |
| `PlayTime`            | `Userid`               | Minutes played (internal, not a game event) |

> **Note:** Event names must use PascalCase with `Event` prefix (e.g., `player_death` → `EventPlayerDeath`). The `Target` field should match a player-related property from the event (check the event documentation for available fields).

### Event Properties (EventProperties)

Event properties allow filtering missions to specific conditions. The plugin dynamically reads all properties from game events.

> **💡 Tip:** Enable `EventDebugLogs: true` in config.json to see all available properties and their values in the server console when events fire. This makes it easy to discover which properties you can filter on.

#### Common EventPlayerDeath Properties

| Property        | Type    | Description                              | Example Value                |
| --------------- | ------- | ---------------------------------------- | ---------------------------- |
| `Weapon`        | string  | Weapon name used for the kill            | `"ak47"`, `"awp"`, `"knife"` |
| `Headshot`      | boolean | Whether it was a headshot                | `true`, `false`              |
| `Penetrated`    | number  | Number of surfaces penetrated (wallbang) | `0`, `1`, `2`                |
| `Noscope`       | boolean | Whether it was a noscope kill            | `true`, `false`              |
| `Thrusmoke`     | boolean | Whether killed through smoke             | `true`, `false`              |
| `Attackerblind` | boolean | Whether attacker was flashed             | `true`, `false`              |
| `Distance`      | number  | Distance between attacker and victim     | `500.0`                      |

#### Property Matching Logic

- **String properties**: Uses case-insensitive contains matching (e.g., `"ak"` matches `"ak47"`)
- **Boolean properties**: Must match exactly (`true` or `false`)
- **Number properties**: Event value must be >= mission value (useful for minimum distance, penetration count)

#### EventProperties Examples

```json
{
  "Event": "EventPlayerDeath",
  "EventProperties": {
    "Weapon": "ak47",
    "Headshot": true
  },
  "Target": "Attacker",
  "Amount": 10,
  "Phrase": "Get 10 AK-47 headshot kills"
}
```

```json
{
  "Event": "EventPlayerDeath",
  "EventProperties": {
    "Weapon": "awp",
    "Noscope": true
  },
  "Target": "Attacker",
  "Amount": 5,
  "Phrase": "Get 5 AWP noscope kills"
}
```

```json
{
  "Event": "EventPlayerDeath",
  "EventProperties": {
    "Penetrated": 1
  },
  "Target": "Attacker",
  "Amount": 3,
  "Phrase": "Get 3 wallbang kills"
}
```

```json
{
  "Event": "EventPlayerDeath",
  "EventProperties": {
    "Thrusmoke": true
  },
  "Target": "Attacker",
  "Amount": 5,
  "Phrase": "Kill 5 enemies through smoke"
}
```

### Mission Definition Fields

| Field             | Type     | Required | Description                                      |
| ----------------- | -------- | -------- | ------------------------------------------------ |
| `Event`           | string   | Yes      | Game event type to track                         |
| `Target`          | string   | Yes      | Event field identifying the player               |
| `Amount`          | number   | Yes      | Required count to complete mission               |
| `Phrase`          | string   | Yes      | Mission description shown to players             |
| `RewardCommands`  | string[] | Yes      | Commands executed on completion                  |
| `RewardPhrase`    | string   | Yes      | Reward description shown to players              |
| `EventProperties` | object   | No       | Filter conditions for the event                  |
| `MapName`         | string   | No       | Restrict mission to specific map                 |
| `Flag`            | string   | No       | Permission flag required to receive this mission |

### Reset Modes

| Mode      | Description                                 |
| --------- | ------------------------------------------- |
| `Daily`   | Missions reset at midnight                  |
| `Weekly`  | Missions reset every Sunday                 |
| `Monthly` | Missions reset at end of month              |
| `PerMap`  | Missions reset on map change                |
| `Instant` | Completed missions are immediately replaced |

### Reward Placeholders

| Placeholder   | Description          |
| ------------- | -------------------- |
| `{steamid64}` | Player's Steam ID 64 |
| `{steamid}`   | Same as steamid64    |
| `{name}`      | Player's name        |
| `{userid}`    | Player's user ID     |
| `{slot}`      | Player's slot        |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Database

The plugin uses automatic schema management with FluentMigrator. Tables are created automatically on first run.

### Supported Databases

| Database        | Status  | Notes                                      |
| --------------- | ------- | ------------------------------------------ |
| MySQL / MariaDB | ✅ Full | Recommended for multi-server setups        |
| PostgreSQL      | ✅ Full | Alternative for existing Postgres setups   |
| SQLite          | ✅ Full | Perfect for single-server, no setup needed |

### Database Tables

- `k4_missions` - Player mission assignments and progress

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- LICENSE -->

## License

Distributed under the GPL-3.0 License. See [`LICENSE.md`](LICENSE.md) for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>
