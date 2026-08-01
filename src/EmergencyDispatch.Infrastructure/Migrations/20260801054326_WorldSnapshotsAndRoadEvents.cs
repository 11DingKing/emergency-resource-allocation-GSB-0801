using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmergencyDispatch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorldSnapshotsAndRoadEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastEventId",
                table: "RoadSegments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorldSnapshotId",
                table: "Plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RoadEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    RoadId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoadEvents_RoadSegments_RoadId",
                        column: x => x.RoadId,
                        principalTable: "RoadSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorldSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    WorldDigest = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorldSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorldSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Digest = table.Column<string>(type: "text", nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldSnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorldSnapshotEntries_WorldSnapshots_WorldSnapshotId",
                        column: x => x.WorldSnapshotId,
                        principalTable: "WorldSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_WorldSnapshotId",
                table: "Plans",
                column: "WorldSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_RoadEvents_EventId",
                table: "RoadEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoadEvents_RoadId",
                table: "RoadEvents",
                column: "RoadId");

            migrationBuilder.CreateIndex(
                name: "IX_WorldSnapshotEntries_WorldSnapshotId_EntityType_Code",
                table: "WorldSnapshotEntries",
                columns: new[] { "WorldSnapshotId", "EntityType", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorldSnapshots_WorldDigest",
                table: "WorldSnapshots",
                column: "WorldDigest",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Plans_WorldSnapshots_WorldSnapshotId",
                table: "Plans",
                column: "WorldSnapshotId",
                principalTable: "WorldSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Plans_WorldSnapshots_WorldSnapshotId",
                table: "Plans");

            migrationBuilder.DropTable(
                name: "RoadEvents");

            migrationBuilder.DropTable(
                name: "WorldSnapshotEntries");

            migrationBuilder.DropTable(
                name: "WorldSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Plans_WorldSnapshotId",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "LastEventId",
                table: "RoadSegments");

            migrationBuilder.DropColumn(
                name: "WorldSnapshotId",
                table: "Plans");
        }
    }
}
