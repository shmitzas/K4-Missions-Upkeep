using FluentMigrator;

namespace K4Missions.Database.Migrations;

/// <summary>
/// Migration to add event_properties, map_name, and flag columns
/// These fields allow for filtered missions and proper persistence
/// </summary>
[Migration(202512021907)]
public class M002_AddEventProperties : Migration
{
	public override void Up()
	{
		if (!Schema.Table("k4_missions").Exists())
			return;

		// Add event_properties column (JSON string for filtering conditions)
		if (!Schema.Table("k4_missions").Column("event_properties").Exists())
		{
			Alter.Table("k4_missions")
				.AddColumn("event_properties").AsString(int.MaxValue).Nullable();
		}

		// Add map_name column (map restriction)
		if (!Schema.Table("k4_missions").Column("map_name").Exists())
		{
			Alter.Table("k4_missions")
				.AddColumn("map_name").AsString(64).Nullable();
		}
	}

	public override void Down()
	{
		if (!Schema.Table("k4_missions").Exists())
			return;

		if (Schema.Table("k4_missions").Column("event_properties").Exists())
			Delete.Column("event_properties").FromTable("k4_missions");

		if (Schema.Table("k4_missions").Column("map_name").Exists())
			Delete.Column("map_name").FromTable("k4_missions");
	}
}
