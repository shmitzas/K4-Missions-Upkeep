# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v1.0.6] - 2026-02-11

### Fixed

- **CRITICAL**: Fixed configuration binding bug in `LoadConfiguration()` method
  - Changed `.BindConfiguration(ConfigFileName)` to `.BindConfiguration(ConfigSection)`
  - This bug caused all config values to use hardcoded defaults instead of reading from `config.json`
  - **Impact**: Plugin configuration was completely non-functional - mission amounts, VIP settings, webhook URLs, reset modes, and all command settings were ignored

## [v1.0.5]

### Changed

- **Config hot-reload support**: Refactored configuration handling to use `IOptionsMonitor<PluginConfig>` instead of `IOptions<PluginConfig>`, enabling runtime configuration changes without server restart
- **Standardized config access**: All configuration access now uses `Config.CurrentValue` pattern for consistency with K4-WeaponPurchase plugin architecture
- **Simplified service constructors**: Removed config injection from `PlayerManager` and `ResetService` constructors, now using static `Config.CurrentValue` access
- **GameRules access pattern**: Verified and maintained consistent `Core.EntitySystem.GetGameRules()` pattern for warmup period detection, matching K4-WeaponPurchase architecture

### Technical Details

- Plugin now uses hot-reloadable configuration pattern matching K4-WeaponPurchase plugin
- Configuration changes can be detected and applied at runtime
- Improved code maintainability with centralized config access
- GameRules access uses standardized `Core.EntitySystem.GetGameRules()?.WarmupPeriod` pattern

## [v1.0.4]

### Added

- **EventProperties persistence**: EventProperties and MapName are now properly saved to and loaded from database
- **Extended primitive support**: EventProperties now supports all primitive types (bool, string, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, decimal)
- **New database columns**: Added `event_properties` and `map_name` columns via M002 migration

### Fixed

- Fixed EventProperties not being persisted to database (missions with filters like weapon/headshot now work correctly after server restart)
- Fixed MapName restriction not being loaded from database

## [v1.0.3]

### Fixed

- Fixed migration version ID conflict with FluentMigrator

## [v1.0.2]

### Added

- **Multi-database support**: Now supports MySQL/MariaDB, PostgreSQL, and SQLite
- **Database migrations**: Automatic schema management with FluentMigrator
- **ORM integration**: Dapper + Dommel for type-safe database operations

### Changed

- Refactored database layer to use Dommel ORM instead of raw SQL queries
- Improved database compatibility across different database engines
- Optimized publish output by excluding unused language resources and database providers

### Fixed

- Fixed SQL syntax compatibility issues with different database engines
