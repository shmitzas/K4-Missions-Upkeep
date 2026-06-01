using FluentMigrator;

namespace K4Missions.Database.Migrations;

/// <summary>
/// Initial migration for K4 Missions
/// Creates k4_missions table
/// </summary>
[Migration(202512010249)]
public class M001_MissionsTable : Migration
{
	public override void Up()
	{
		if (Schema.Table("k4_missions").Exists())
			return;

		Create.Table("k4_missions")
			.WithColumn("id").AsInt32().NotNullable().PrimaryKey().Identity()
			.WithColumn("steamid64").AsInt64().NotNullable()
			.WithColumn("event").AsString(64).NotNullable()
			.WithColumn("target").AsString(64).NotNullable()
			.WithColumn("amount").AsInt32().NotNullable()
			.WithColumn("phrase").AsString(255).NotNullable()
			.WithColumn("reward_phrase").AsString(255).NotNullable()
			.WithColumn("reward_commands").AsString(int.MaxValue).NotNullable()
			.WithColumn("progress").AsInt32().NotNullable().WithDefaultValue(0)
			.WithColumn("completed").AsBoolean().NotNullable().WithDefaultValue(false)
			.WithColumn("expires_at").AsDateTime().Nullable();

		Create.Index("idx_steamid").OnTable("k4_missions").OnColumn("steamid64");
		Create.Index("idx_expires_at").OnTable("k4_missions").OnColumn("expires_at");
	}

	public override void Down()
	{
		if (!Schema.Table("k4_missions").Exists())
			return;

		Delete.Table("k4_missions");
	}
}
