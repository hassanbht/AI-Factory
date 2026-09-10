using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy AgentRun -> HermesExecution: a pure rename (table + index),
            // never a drop/recreate, so existing Hermes execution history
            // survives the migration untouched. Hand-edited: EF's own diff
            // engine matched the old "AgentRun" CLR type name against the new
            // canonical AgentRun type of the same name and produced a bogus
            // column-rename plan (Success -> Version, Response -> ProjectId,
            // etc.) that would have silently corrupted this table's data.
            migrationBuilder.RenameTable(
                name: "agent_runs",
                newName: "hermes_executions");

            migrationBuilder.RenameIndex(
                name: "IX_agent_runs_WorkItemId_AttemptNumber",
                table: "hermes_executions",
                newName: "IX_hermes_executions_WorkItemId_AttemptNumber");

            migrationBuilder.CreateTable(
                name: "agent_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    AgentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SystemPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_definitions", x => x.Id);
                });

            // Canonical AgentRun: newly created, distinct table from the
            // renamed hermes_executions above. Deliberately no FK to
            // WorkItem/Project (see FactoryDbContext) so an AgentRun's own
            // history never vanishes because of an unrelated cascade delete.
            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Iteration = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_runs_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_Enabled",
                table: "agent_definitions",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_AgentDefinitionId",
                table: "agent_runs",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_CorrelationId",
                table: "agent_runs",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_CreatedAtUtc",
                table: "agent_runs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ProjectId",
                table: "agent_runs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_Status",
                table: "agent_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_WorkItemId",
                table: "agent_runs",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropTable(
                name: "agent_definitions");

            migrationBuilder.RenameIndex(
                name: "IX_hermes_executions_WorkItemId_AttemptNumber",
                table: "hermes_executions",
                newName: "IX_agent_runs_WorkItemId_AttemptNumber");

            migrationBuilder.RenameTable(
                name: "hermes_executions",
                newName: "agent_runs");
        }
    }
}
