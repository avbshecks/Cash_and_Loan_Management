using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashLoanManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCashReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "CashTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReversalOfTransactionId",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 6, 35, 31, 195, DateTimeKind.Utc).AddTicks(9506));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 6, 35, 31, 195, DateTimeKind.Utc).AddTicks(9512));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 6, 35, 31, 195, DateTimeKind.Utc).AddTicks(9513));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 6, 35, 31, 195, DateTimeKind.Utc).AddTicks(9514));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 23, 6, 35, 31, 195, DateTimeKind.Utc).AddTicks(9514));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalOfTransactionId",
                table: "CashTransactions");

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 7, 9, 41, 19, 826, DateTimeKind.Utc).AddTicks(8675));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 7, 9, 41, 19, 826, DateTimeKind.Utc).AddTicks(8679));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 7, 9, 41, 19, 826, DateTimeKind.Utc).AddTicks(8680));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 7, 9, 41, 19, 826, DateTimeKind.Utc).AddTicks(8681));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 6, 7, 9, 41, 19, 826, DateTimeKind.Utc).AddTicks(8681));
        }
    }
}
