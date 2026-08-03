using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanVersion = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    InputVersion = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    TotalCostMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersedesPlanId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoadSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MaxVehicleHeightMeters = table.Column<decimal>(type: "numeric(4,1)", nullable: false),
                    TravelMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Capabilities = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequiredCapabilities = table.Column<List<string>>(type: "text[]", nullable: false),
                    DeadlineMinutes = table.Column<int>(type: "integer", nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Danger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Teams_CurrentTeamId",
                        column: x => x.CurrentTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    HeightMeters = table.Column<decimal>(type: "numeric(4,1)", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vehicles_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoadId = table.Column<Guid>(type: "uuid", nullable: true),
                    EtaMinutes = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<int>(type: "integer", nullable: false),
                    IsPreemption = table.Column<bool>(type: "boolean", nullable: false),
                    Reasons = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Assignments_RoadSegments_RoadId",
                        column: x => x.RoadId,
                        principalTable: "RoadSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UnassignedTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reasons = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnassignedTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnassignedTasks_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UnassignedTasks_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_PlanId",
                table: "Assignments",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_RoadId",
                table: "Assignments",
                column: "RoadId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_TaskId",
                table: "Assignments",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_TeamId",
                table: "Assignments",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_InputVersion",
                table: "Plans",
                column: "InputVersion",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoadSegments_Code",
                table: "RoadSegments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Code",
                table: "Tasks",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CurrentTeamId",
                table: "Tasks",
                column: "CurrentTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Code",
                table: "Teams",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnassignedTasks_PlanId",
                table: "UnassignedTasks",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_UnassignedTasks_TaskId",
                table: "UnassignedTasks",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_TeamId",
                table: "Vehicles",
                column: "TeamId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "UnassignedTasks");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "RoadSegments");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
