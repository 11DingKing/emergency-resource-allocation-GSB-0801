using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                name: "allocation_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    road_snapshot_version = table.Column<long>(type: "bigint", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    total_cost_minutes = table.Column<int>(type: "integer", nullable: false),
                    assigned_count = table.Column<int>(type: "integer", nullable: false),
                    unassigned_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocation_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "road_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    travel_time_minutes = table.Column<int>(type: "integer", nullable: false),
                    height_limit_meters = table.Column<double>(type: "double precision", nullable: true),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    snapshot_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    interruption_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_road_segments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    base_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_available = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "allocation_audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_order = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    task_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    team_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    vehicle_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    road_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocation_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_allocation_audit_entries_allocation_versions_allocation_ver~",
                        column: x => x.allocation_version_id,
                        principalTable: "allocation_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_capabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_capabilities", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_capabilities_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    height_meters = table.Column<double>(type: "double precision", nullable: false),
                    avg_speed_m_per_min = table.Column<double>(type: "double precision", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicles", x => x.id);
                    table.ForeignKey(
                        name: "FK_vehicles_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "emergency_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    location_node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    deadline_minutes = table.Column<int>(type: "integer", nullable: true),
                    severity_version = table.Column<int>(type: "integer", nullable: false),
                    assigned_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emergency_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_emergency_tasks_teams_assigned_team_id",
                        column: x => x.assigned_team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_emergency_tasks_vehicles_assigned_vehicle_id",
                        column: x => x.assigned_vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision = table.Column<int>(type: "integer", nullable: false),
                    estimated_travel_minutes = table.Column<int>(type: "integer", nullable: false),
                    estimated_total_minutes = table.Column<int>(type: "integer", nullable: false),
                    meets_deadline = table.Column<bool>(type: "boolean", nullable: false),
                    route_node_ids = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    preempted = table.Column<bool>(type: "boolean", nullable: false),
                    previous_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_vehicle_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_assignments_allocation_versions_allocation_version_id",
                        column: x => x.allocation_version_id,
                        principalTable: "allocation_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assignments_emergency_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "emergency_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assignments_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_assignments_vehicles_vehicle_id",
                        column: x => x.vehicle_id,
                        principalTable: "vehicles",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "task_capability_requirements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_capability_requirements", x => x.id);
                    table.ForeignKey(
                        name: "FK_task_capability_requirements_emergency_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "emergency_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_allocation_audit_entries_allocation_version_id",
                table: "allocation_audit_entries",
                column: "allocation_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_allocation_versions_operation_idempotency_key",
                table: "allocation_versions",
                columns: new[] { "operation", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_allocation_versions_sequence",
                table: "allocation_versions",
                column: "sequence");

            migrationBuilder.CreateIndex(
                name: "IX_allocation_versions_status",
                table: "allocation_versions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_allocation_version_id",
                table: "assignments",
                column: "allocation_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_task_id",
                table: "assignments",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_team_id",
                table: "assignments",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_vehicle_id",
                table: "assignments",
                column: "vehicle_id");

            migrationBuilder.CreateIndex(
                name: "IX_emergency_tasks_assigned_team_id",
                table: "emergency_tasks",
                column: "assigned_team_id");

            migrationBuilder.CreateIndex(
                name: "IX_emergency_tasks_assigned_vehicle_id",
                table: "emergency_tasks",
                column: "assigned_vehicle_id");

            migrationBuilder.CreateIndex(
                name: "IX_emergency_tasks_code",
                table: "emergency_tasks",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_road_segments_code",
                table: "road_segments",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_road_segments_snapshot_version",
                table: "road_segments",
                column: "snapshot_version");

            migrationBuilder.CreateIndex(
                name: "IX_task_capability_requirements_task_id_capability",
                table: "task_capability_requirements",
                columns: new[] { "task_id", "capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_capabilities_team_id_capability",
                table: "team_capabilities",
                columns: new[] { "team_id", "capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teams_code",
                table: "teams",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_code",
                table: "vehicles",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_team_id",
                table: "vehicles",
                column: "team_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocation_audit_entries");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "road_segments");

            migrationBuilder.DropTable(
                name: "task_capability_requirements");

            migrationBuilder.DropTable(
                name: "team_capabilities");

            migrationBuilder.DropTable(
                name: "allocation_versions");

            migrationBuilder.DropTable(
                name: "emergency_tasks");

            migrationBuilder.DropTable(
                name: "vehicles");

            migrationBuilder.DropTable(
                name: "teams");
        }
    }
}
