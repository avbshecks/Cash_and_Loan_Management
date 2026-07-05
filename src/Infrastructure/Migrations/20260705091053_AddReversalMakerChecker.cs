using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashLoanManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReversalMakerChecker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReversalApprovedAt",
                table: "CashTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalApprovedByUserId",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                table: "CashTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalRejectionReason",
                table: "CashTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversalRequestedAt",
                table: "CashTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalRequestedByUserId",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalStatus",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "AccountantTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversalApprovedAt",
                table: "AccountantTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalApprovedByUserId",
                table: "AccountantTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalOfTransactionId",
                table: "AccountantTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                table: "AccountantTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalRejectionReason",
                table: "AccountantTransactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversalRequestedAt",
                table: "AccountantTransactions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalRequestedByUserId",
                table: "AccountantTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversalStatus",
                table: "AccountantTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4258));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4266));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4268));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4270));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4273));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 5, 9, 10, 52, 759, DateTimeKind.Utc).AddTicks(4276));

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_ReversalApprovedByUserId",
                table: "CashTransactions",
                column: "ReversalApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_ReversalRequestedByUserId",
                table: "CashTransactions",
                column: "ReversalRequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountantTransactions_ReversalApprovedByUserId",
                table: "AccountantTransactions",
                column: "ReversalApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountantTransactions_ReversalRequestedByUserId",
                table: "AccountantTransactions",
                column: "ReversalRequestedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountantTransactions_Users_ReversalApprovedByUserId",
                table: "AccountantTransactions",
                column: "ReversalApprovedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountantTransactions_Users_ReversalRequestedByUserId",
                table: "AccountantTransactions",
                column: "ReversalRequestedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Users_ReversalApprovedByUserId",
                table: "CashTransactions",
                column: "ReversalApprovedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Users_ReversalRequestedByUserId",
                table: "CashTransactions",
                column: "ReversalRequestedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountantTransactions_Users_ReversalApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountantTransactions_Users_ReversalRequestedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Users_ReversalApprovedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Users_ReversalRequestedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_ReversalApprovedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_ReversalRequestedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountantTransactions_ReversalApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountantTransactions_ReversalRequestedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalApprovedAt",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalApprovedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRejectionReason",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRequestedAt",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRequestedByUserId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalStatus",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "IsReversed",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalApprovedAt",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalApprovedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalOfTransactionId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRejectionReason",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRequestedAt",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalRequestedByUserId",
                table: "AccountantTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalStatus",
                table: "AccountantTransactions");

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9428));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9434));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9435));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9436));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9437));

            migrationBuilder.UpdateData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 3, 7, 29, 27, 561, DateTimeKind.Utc).AddTicks(9438));
        }
    }
}
