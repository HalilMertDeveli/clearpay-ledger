using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClearPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerProceduresAndCoveringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_WalletId_CreatedAt",
                table: "LedgerEntry");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOperation_CreatedAt_Type",
                table: "PaymentOperation",
                columns: new[] { "CreatedAt", "Type" })
                .Annotation("SqlServer:Include", new[] { "Status", "Provider", "ProviderReference", "CorrelationId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_WalletId_CreatedAt",
                table: "LedgerEntry",
                columns: new[] { "WalletId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "Amount", "Kind", "CorrelationId" });

            // dbo.usp_LedgerWalletBalance, dbo.usp_LedgerWalletSummary, dbo.usp_PaymentSettlementCandidates
            foreach (var batch in Sql.SqlScripts.Batches("LedgerProcedures.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_PaymentSettlementCandidates;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_LedgerWalletSummary;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_LedgerWalletBalance;");

            migrationBuilder.DropIndex(
                name: "IX_PaymentOperation_CreatedAt_Type",
                table: "PaymentOperation");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_WalletId_CreatedAt",
                table: "LedgerEntry");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_WalletId_CreatedAt",
                table: "LedgerEntry",
                columns: new[] { "WalletId", "CreatedAt" });
        }
    }
}
