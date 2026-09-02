using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestrationRunSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orchestration_run_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrchestrationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureType = table.Column<string>(type: "TEXT", nullable: true),
                    FailureMessage = table.Column<string>(type: "TEXT", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orchestration_run_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_orchestration_run_steps_orchestration_runs_OrchestrationRunId",
                        column: x => x.OrchestrationRunId,
                        principalTable: "orchestration_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_run_steps_OrchestrationRunId",
                table: "orchestration_run_steps",
                column: "OrchestrationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_run_steps_Stage",
                table: "orchestration_run_steps",
                column: "Stage");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_run_steps_StartedAtUtc",
                table: "orchestration_run_steps",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_run_steps_Status",
                table: "orchestration_run_steps",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orchestration_run_steps");
        }
    }
}
