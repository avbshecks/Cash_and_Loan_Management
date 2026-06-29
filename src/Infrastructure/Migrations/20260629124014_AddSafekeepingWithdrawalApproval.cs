using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashLoanManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSafekeepingWithdrawalApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "SafekeepingTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "SafekeepingTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedByUserId",
                table: "SafekeepingTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "SafekeepingTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 40, 13, 841, DateTimeKind.Utc).AddTicks(824));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 40, 13, 841, DateTimeKind.Utc).AddTicks(829));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 40, 13, 841, DateTimeKind.Utc).AddTicks(830));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 40, 13, 841, DateTimeKind.Utc).AddTicks(831));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 40, 13, 841, DateTimeKind.Utc).AddTicks(832));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "SafekeepingTransactions");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "SafekeepingTransactions");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "SafekeepingTransactions");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "SafekeepingTransactions");

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 16, 16, 971, DateTimeKind.Utc).AddTicks(1545));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 16, 16, 971, DateTimeKind.Utc).AddTicks(1553));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 16, 16, 971, DateTimeKind.Utc).AddTicks(1555));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 16, 16, 971, DateTimeKind.Utc).AddTicks(1557));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 29, 12, 16, 16, 971, DateTimeKind.Utc).AddTicks(1559));
        }
    }
}
