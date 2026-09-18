using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectLifecycleStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LifecycleStartedAtUtc",
                table: "projects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LifecycleStartedAtUtc",
                table: "projects");
        }
    }
}
