using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmergencyDispatch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadEventsAndSnapshotBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "AllocationVersions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SnapshotVersion",
                table: "AllocationVersions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RoadEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    RoadCode = table.Column<string>(type: "text", nullable: false),
                    Closed = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoadEvents_EventId",
                table: "RoadEvents",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoadEvents");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "AllocationVersions");

            migrationBuilder.DropColumn(
                name: "SnapshotVersion",
                table: "AllocationVersions");
        }
    }
}
