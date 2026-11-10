using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClearPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeDsSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThreeDsSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    CardLast4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Eci = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    AuthenticationValue = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcsNonce = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedByPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreeDsSession", x => x.Id);
                    table.CheckConstraint("CK_ThreeDsSession_Amount_Positive", "[Amount] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThreeDsSession_Status_ExpiresAt",
                table: "ThreeDsSession",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ThreeDsSession_UserId",
                table: "ThreeDsSession",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThreeDsSession");
        }
    }
}
