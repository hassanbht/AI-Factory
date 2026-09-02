using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestrationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orchestration_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CurrentStage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedModel = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorkerHeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orchestration_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "orchestration_progress_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrchestrationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orchestration_progress_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_orchestration_progress_events_orchestration_runs_OrchestrationRunId",
                        column: x => x.OrchestrationRunId,
                        principalTable: "orchestration_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "orchestration_run_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrchestrationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    BuildResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    TestResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    ValidationResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    RepairContextText = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orchestration_run_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_orchestration_run_attempts_orchestration_runs_OrchestrationRunId",
                        column: x => x.OrchestrationRunId,
                        principalTable: "orchestration_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_progress_events_OrchestrationRunId",
                table: "orchestration_progress_events",
                column: "OrchestrationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_run_attempts_OrchestrationRunId",
                table: "orchestration_run_attempts",
                column: "OrchestrationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_runs_ProjectId",
                table: "orchestration_runs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_runs_Status",
                table: "orchestration_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_runs_WorkItemId",
                table: "orchestration_runs",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orchestration_progress_events");

            migrationBuilder.DropTable(
                name: "orchestration_run_attempts");

            migrationBuilder.DropTable(
                name: "orchestration_runs");
        }
    }
}
