using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmergencyAllocation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoadEventsAndSnapshotHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoadSnapshotHash",
                table: "AllocationVersions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TaskSnapshotHash",
                table: "AllocationVersions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TeamSnapshotHash",
                table: "AllocationVersions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TriggeringRoadEventId",
                table: "AllocationVersions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleSnapshotHash",
                table: "AllocationVersions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RoadEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoadSegmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoadEvents_RoadSegments_RoadSegmentId",
                        column: x => x.RoadSegmentId,
                        principalTable: "RoadSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoadEvents_RoadSegmentId_OccurredAt",
                table: "RoadEvents",
                columns: new[] { "RoadSegmentId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoadEvents");

            migrationBuilder.DropColumn(
                name: "RoadSnapshotHash",
                table: "AllocationVersions");

            migrationBuilder.DropColumn(
                name: "TaskSnapshotHash",
                table: "AllocationVersions");

            migrationBuilder.DropColumn(
                name: "TeamSnapshotHash",
                table: "AllocationVersions");

            migrationBuilder.DropColumn(
                name: "TriggeringRoadEventId",
                table: "AllocationVersions");

            migrationBuilder.DropColumn(
                name: "VehicleSnapshotHash",
                table: "AllocationVersions");
        }
    }
}
