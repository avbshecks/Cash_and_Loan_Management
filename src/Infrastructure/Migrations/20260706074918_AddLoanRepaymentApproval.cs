using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashLoanManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanRepaymentApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing repayments as Approved (2) so historical remaining-balance
            // and loan-status calculations are unaffected; only new repayments start Pending.
            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "LoanRepayments",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "LoanRepayments",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedByUserId",
                table: "LoanRepayments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "LoanRepayments",
                type: "text",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_LoanRepayments_ApprovedByUserId",
                table: "LoanRepayments",
                column: "ApprovedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_LoanRepayments_Users_ApprovedByUserId",
                table: "LoanRepayments",
                column: "ApprovedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LoanRepayments_Users_ApprovedByUserId",
                table: "LoanRepayments");

            migrationBuilder.DropIndex(
                name: "IX_LoanRepayments_ApprovedByUserId",
                table: "LoanRepayments");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "LoanRepayments");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "LoanRepayments");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "LoanRepayments");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "LoanRepayments");

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
        }
    }
}
