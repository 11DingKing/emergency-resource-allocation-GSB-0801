using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmergencyAllocation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllocationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InputVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DangerLevelRaised = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotTakenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SolverVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalCost = table.Column<long>(type: "bigint", nullable: false),
                    IsFeasible = table.Column<bool>(type: "boolean", nullable: false),
                    NoFeasibleReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PreviousVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoadSegments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FromNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeightLimitMeters = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false),
                    TravelTimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HeightMeters = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditExplanations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RelatedTaskId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RelatedTeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditExplanations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditExplanations_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VehicleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RouteNodes = table.Column<string>(type: "text", nullable: false),
                    EstimatedArrivalMinutes = table.Column<int>(type: "integer", nullable: false),
                    EstimatedCompletionMinutes = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreemptionReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAssignments_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VehicleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HomeNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrentNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    Capabilities = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocationNode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequiredArrivalMinutes = table.Column<int>(type: "integer", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    DangerLevel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AssignedTeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequiredCapabilities = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tasks_Teams_AssignedTeamId",
                        column: x => x.AssignedTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationVersions_InputVersion",
                table: "AllocationVersions",
                column: "InputVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditExplanations_AllocationVersionId",
                table: "AuditExplanations",
                column: "AllocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoadSegments_FromNode_ToNode",
                table: "RoadSegments",
                columns: new[] { "FromNode", "ToNode" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_AllocationVersionId",
                table: "TaskAssignments",
                column: "AllocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_AssignedTeamId",
                table: "tasks",
                column: "AssignedTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_VehicleId",
                table: "Teams",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditExplanations");

            migrationBuilder.DropTable(
                name: "RoadSegments");

            migrationBuilder.DropTable(
                name: "TaskAssignments");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "AllocationVersions");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "Vehicles");
        }
    }
}
