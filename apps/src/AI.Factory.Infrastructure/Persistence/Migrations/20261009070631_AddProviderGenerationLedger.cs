using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderGenerationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_capture_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CertificationEpoch = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BenchmarkIdentity = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    GatewayKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ListenPrefix = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OverrideBaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CapturedGenerationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_capture_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_generations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CertificationEpoch = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BenchmarkIdentity = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GenerationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthoritativeCostUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    AccountingStatus = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    AccountingNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_generations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_capture_sessions_AgentRunId",
                table: "provider_capture_sessions",
                column: "AgentRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_generations_AccountingStatus",
                table: "provider_generations",
                column: "AccountingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_provider_generations_AgentRunId",
                table: "provider_generations",
                column: "AgentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_generations_CertificationEpoch",
                table: "provider_generations",
                column: "CertificationEpoch");

            migrationBuilder.CreateIndex(
                name: "IX_provider_generations_CertificationEpoch_BenchmarkIdentity",
                table: "provider_generations",
                columns: new[] { "CertificationEpoch", "BenchmarkIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_generations_GenerationId",
                table: "provider_generations",
                column: "GenerationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_capture_sessions");

            migrationBuilder.DropTable(
                name: "provider_generations");
        }
    }
}
