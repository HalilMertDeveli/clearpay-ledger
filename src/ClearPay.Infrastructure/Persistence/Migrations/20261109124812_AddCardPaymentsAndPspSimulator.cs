using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClearPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardPaymentsAndPspSimulator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sim");

            migrationBuilder.CreateTable(
                name: "CardPayment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ThreeDsSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CapturedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CardLast4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DeclineCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardPayment", x => x.Id);
                    table.CheckConstraint("CK_CardPayment_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_CardPayment_Captured_LE_Amount", "[CapturedAmount] * 1.0 >= 0 AND [CapturedAmount] * 1.0 <= [Amount] * 1.0");
                    table.CheckConstraint("CK_CardPayment_Refund_LE_Captured", "[RefundedAmount] * 1.0 >= 0 AND [RefundedAmount] * 1.0 <= [CapturedAmount] * 1.0");
                });

            migrationBuilder.CreateTable(
                name: "PspTransaction",
                schema: "sim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    AuthorizationReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: false),
                    DeclineCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ResponseLost = table.Column<bool>(type: "bit", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PspTransaction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentOperation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProviderIdempotencyKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOperation", x => x.Id);
                    table.CheckConstraint("CK_PaymentOperation_Amount_Positive", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PaymentOperation_CardPayment_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "CardPayment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardPayment_UserId",
                table: "CardPayment",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_CardPayment_ThreeDsSessionId",
                table: "CardPayment",
                column: "ThreeDsSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOperation_PaymentId",
                table: "PaymentOperation",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "UX_PaymentOperation_Provider_Key",
                table: "PaymentOperation",
                columns: new[] { "Provider", "ProviderIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SimPspTransaction_Provider_Reference",
                schema: "sim",
                table: "PspTransaction",
                columns: new[] { "Provider", "Reference" });

            migrationBuilder.CreateIndex(
                name: "UX_SimPspTransaction_Provider_Key",
                schema: "sim",
                table: "PspTransaction",
                columns: new[] { "Provider", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentOperation");

            migrationBuilder.DropTable(
                name: "PspTransaction",
                schema: "sim");

            migrationBuilder.DropTable(
                name: "CardPayment");
        }
    }
}
