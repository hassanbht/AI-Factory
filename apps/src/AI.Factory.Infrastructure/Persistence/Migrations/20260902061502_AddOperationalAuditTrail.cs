using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AI.Factory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorId",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorType",
                table: "audit_log_entries",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "System");

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                table: "audit_log_entries",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentOperationId",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "audit_log_entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_entries_CorrelationId",
                table: "audit_log_entries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_entries_RunId",
                table: "audit_log_entries",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_entries_Severity",
                table: "audit_log_entries",
                column: "Severity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_audit_log_entries_CorrelationId",
                table: "audit_log_entries");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_entries_RunId",
                table: "audit_log_entries");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_entries_Severity",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "ActorType",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "ParentOperationId",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "audit_log_entries");
        }
    }
}
