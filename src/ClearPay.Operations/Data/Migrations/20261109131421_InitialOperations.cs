using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClearPay.Operations.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.CreateTable(
                name: "InboxMessage",
                schema: "ops",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Template = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentOperation",
                schema: "ops",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MerchantReference = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOperation", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MatchedCount = table.Column<int>(type: "int", nullable: false),
                    MismatchCount = table.Column<int>(type: "int", nullable: false),
                    LedgerTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SettlementTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationItem",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LedgerAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    SettlementAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationItem_ReconciliationRun_RunId",
                        column: x => x.RunId,
                        principalSchema: "ops",
                        principalTable: "ReconciliationRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_Recipient_CreatedAt",
                schema: "ops",
                table: "Notification",
                columns: new[] { "Recipient", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Notification_SourceMessageId",
                schema: "ops",
                table: "Notification",
                column: "SourceMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOperation_CreatedAt_Type",
                schema: "ops",
                table: "PaymentOperation",
                columns: new[] { "CreatedAt", "Type" })
                .Annotation("SqlServer:Include", new[] { "Status", "Provider", "ProviderReference", "MerchantReference", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationItem_RunId_Kind",
                schema: "ops",
                table: "ReconciliationItem",
                columns: new[] { "RunId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_BusinessDate_Status",
                schema: "ops",
                table: "ReconciliationRun",
                columns: new[] { "BusinessDate", "Status" });

            // ops.usp_SettlementCandidates (covering seek for the nightly reconciliation)
            foreach (var batch in Sql.OpsSql.Batches("OperationsProcedures.sql"))
                migrationBuilder.Sql(batch);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS ops.usp_SettlementCandidates;");
            migrationBuilder.DropTable(
                name: "InboxMessage",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "Notification",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "PaymentOperation",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "ReconciliationItem",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "ReconciliationRun",
                schema: "ops");
        }
    }
}
