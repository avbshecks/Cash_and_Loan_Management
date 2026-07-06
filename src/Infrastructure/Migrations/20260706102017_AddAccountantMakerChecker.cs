using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashLoanManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountantMakerChecker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "AccountantTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "AccountantTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedByUserId",
                table: "AccountantTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "AccountantTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8026));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8031));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8031));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8032));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8033));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 10, 20, 16, 688, DateTimeKind.Utc).AddTicks(8034));

            migrationBuilder.CreateIndex(
                name: "IX_AccountantTransactions_ApprovedByUserId",
                table: "AccountantTransactions",
                column: "ApprovedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountantTransactions_Users_ApprovedByUserId",
                table: "AccountantTransactions",
                column: "ApprovedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountantTransactions_Users_ApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountantTransactions_ApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "AccountantTransactions");

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7257));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7263));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7265));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7267));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7268));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 6, 7, 49, 18, 297, DateTimeKind.Utc).AddTicks(7270));
        }
    }
}
