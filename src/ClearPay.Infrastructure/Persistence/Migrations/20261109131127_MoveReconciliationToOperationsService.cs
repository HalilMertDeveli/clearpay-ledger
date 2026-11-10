using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClearPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveReconciliationToOperationsService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reconciliation now lives in the Operations service with its own database and projection;
            // the ledger keeps no reconciliation tables, index or procedure.
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.usp_PaymentSettlementCandidates;");

            migrationBuilder.DropTable(
                name: "ReconciliationItem");

            migrationBuilder.DropTable(
                name: "ReconciliationRun");

            migrationBuilder.DropIndex(
                name: "IX_PaymentOperation_CreatedAt_Type",
                table: "PaymentOperation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in Sql.SqlScripts.Batches("LedgerProcedures.sql"))
                migrationBuilder.Sql(batch);

            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LedgerTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MatchedCount = table.Column<int>(type: "int", nullable: false),
                    MismatchCount = table.Column<int>(type: "int", nullable: false),
                    SettlementTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LedgerAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationItem_ReconciliationRun_RunId",
                        column: x => x.RunId,
                        principalTable: "ReconciliationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOperation_CreatedAt_Type",
                table: "PaymentOperation",
                columns: new[] { "CreatedAt", "Type" })
                .Annotation("SqlServer:Include", new[] { "Status", "Provider", "ProviderReference", "CorrelationId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationItem_RunId_Kind",
                table: "ReconciliationItem",
                columns: new[] { "RunId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_BusinessDate_Status",
                table: "ReconciliationRun",
                columns: new[] { "BusinessDate", "Status" });
        }
    }
}
