using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmergencyDispatch.Infrastructure.Migrations
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
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    InputVersion = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TotalCostMinutes = table.Column<int>(type: "integer", nullable: false),
                    HasUnassignedTasks = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoadSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    HeightLimitMeters = table.Column<decimal>(type: "numeric(4,2)", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoadSegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RequiredCapabilities = table.Column<string>(type: "text", nullable: false),
                    DeadlineMinutes = table.Column<int>(type: "integer", nullable: false),
                    ServiceMinutes = table.Column<int>(type: "integer", nullable: false),
                    DangerLevel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ExecutingTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutingVehicleId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Capabilities = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    HeightMeters = table.Column<decimal>(type: "numeric(4,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllocationBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DangerLevel = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationBaselines_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoadSegmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrivalMinutes = table.Column<int>(type: "integer", nullable: false),
                    TaskCode = table.Column<string>(type: "text", nullable: false),
                    TeamCode = table.Column<string>(type: "text", nullable: false),
                    VehicleCode = table.Column<string>(type: "text", nullable: false),
                    RoadSegmentCode = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskCode = table.Column<string>(type: "text", nullable: false),
                    RuleCode = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEntries_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UnassignedReasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskCode = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnassignedReasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnassignedReasons_AllocationVersions_AllocationVersionId",
                        column: x => x.AllocationVersionId,
                        principalTable: "AllocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationBaselines_AllocationVersionId_TaskId",
                table: "AllocationBaselines",
                columns: new[] { "AllocationVersionId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AllocationVersions_InputVersion",
                table: "AllocationVersions",
                column: "InputVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AllocationVersions_VersionNumber",
                table: "AllocationVersions",
                column: "VersionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_AllocationVersionId",
                table: "Assignments",
                column: "AllocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_AllocationVersionId",
                table: "AuditEntries",
                column: "AllocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_RoadSegments_Code",
                table: "RoadSegments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_TaskId_RoadSegmentId",
                table: "Routes",
                columns: new[] { "TaskId", "RoadSegmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Code",
                table: "Tasks",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Code",
                table: "Teams",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnassignedReasons_AllocationVersionId",
                table: "UnassignedReasons",
                column: "AllocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Code",
                table: "Vehicles",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllocationBaselines");

            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "RoadSegments");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "UnassignedReasons");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "AllocationVersions");
        }
    }
}
