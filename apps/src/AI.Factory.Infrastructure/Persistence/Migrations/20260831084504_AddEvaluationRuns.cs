using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evaluation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BuildSucceeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    TestsSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    ReviewBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    HumanApproval = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RepairCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Regression = table.Column<string>(type: "TEXT", nullable: false),
                    IsFullyValidated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evaluation_runs_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evaluation_runs_WorkItemId",
                table: "evaluation_runs",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation_runs");
        }
    }
}
