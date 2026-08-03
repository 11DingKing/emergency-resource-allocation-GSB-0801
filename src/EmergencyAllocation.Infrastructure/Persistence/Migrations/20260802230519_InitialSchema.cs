using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmergencyAllocation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_payload_digest",
                table: "allocation_versions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "road_digest",
                table: "allocation_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "task_digest",
                table: "allocation_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "team_digest",
                table: "allocation_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "triggering_road_event_id",
                table: "allocation_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vehicle_digest",
                table: "allocation_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "road_event_id",
                table: "allocation_audit_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "road_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    road_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    snapshot_version_before = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_version_after = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_road_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_road_events_event_id",
                table: "road_events",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "road_events");

            migrationBuilder.DropColumn(
                name: "request_payload_digest",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "road_digest",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "task_digest",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "team_digest",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "triggering_road_event_id",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "vehicle_digest",
                table: "allocation_versions");

            migrationBuilder.DropColumn(
                name: "road_event_id",
                table: "allocation_audit_entries");
        }
    }
}
