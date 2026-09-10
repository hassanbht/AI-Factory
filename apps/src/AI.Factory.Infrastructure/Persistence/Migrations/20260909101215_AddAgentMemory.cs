using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_memory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_memory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_AgentDefinitionId",
                table: "agent_memory",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_AgentRunId",
                table: "agent_memory",
                column: "AgentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_CreatedAtUtc",
                table: "agent_memory",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_ProjectId",
                table: "agent_memory",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_Type",
                table: "agent_memory",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_memory");
        }
    }
}
