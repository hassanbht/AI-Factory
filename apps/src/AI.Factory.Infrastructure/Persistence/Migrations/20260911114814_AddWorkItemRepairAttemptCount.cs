using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemRepairAttemptCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RepairAttemptCount",
                table: "work_items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepairAttemptCount",
                table: "work_items");
        }
    }
}
