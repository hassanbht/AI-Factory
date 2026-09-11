using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemPlanKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "work_items",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_items_ProjectId_PlanKey",
                table: "work_items",
                columns: new[] { "ProjectId", "PlanKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_items_ProjectId_PlanKey",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "work_items");
        }
    }
}
