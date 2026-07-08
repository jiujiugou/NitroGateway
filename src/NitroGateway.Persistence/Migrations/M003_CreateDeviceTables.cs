using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

[Migration(3)]
public sealed class M003_CreateDeviceTables : Migration
{
    public override void Up()
    {
        if (Schema.Table("devices").Exists()) return;
        Create.Table("devices")
            .WithColumn("Id").AsString().PrimaryKey()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("ProtocolName").AsString(100).NotNullable()
            .WithColumn("ProtocolDialect").AsString(100).Nullable()
            .WithColumn("Endpoint").AsString(500).NotNullable()
            .WithColumn("ConnectTimeoutMs").AsInt32().WithDefaultValue(3000)
            .WithColumn("RequestTimeoutMs").AsInt32().WithDefaultValue(5000)
            .WithColumn("RetryCount").AsInt32().WithDefaultValue(3)
            .WithColumn("Status").AsString(50).NotNullable()
            .WithColumn("ConnectionParams").AsString().Nullable();

        Create.Table("points")
            .WithColumn("Id").AsString().PrimaryKey()
            .WithColumn("DeviceId").AsString().NotNullable().ForeignKey("devices", "Id")
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("Address").AsString(200).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("DataType").AsString(50).NotNullable()
            .WithColumn("Access").AsString(50).NotNullable().WithDefaultValue("ReadOnly")
            .WithColumn("Enabled").AsBoolean().WithDefaultValue(true)
            .WithColumn("ScanIntervalMs").AsInt32().WithDefaultValue(0)
            .WithColumn("Deadband").AsDouble().WithDefaultValue(0)
            .WithColumn("ScaleFactor").AsDouble().WithDefaultValue(1.0)
            .WithColumn("ScaleOffset").AsDouble().WithDefaultValue(0);

        Create.Index("IX_points_DeviceId").OnTable("points").OnColumn("DeviceId");
    }

    public override void Down()
    {
        Delete.Table("points");
        Delete.Table("devices");
    }
}
