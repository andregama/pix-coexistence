using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConvivenciaPix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpiPendingSystemBMsg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "UNIQUEIDENTIFIER", nullable: false),
                    IdSystemB = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    MessageId = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "DATETIMEOFFSET", nullable: false),
                    Amount = table.Column<decimal>(type: "DECIMAL(18,2)", nullable: false),
                    PayerId = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    PayeeId = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    RawXmlBase64 = table.Column<string>(type: "NVARCHAR(MAX)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "DATETIME2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpiPendingSystemBMsg", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpiSentMsg",
                columns: table => new
                {
                    IdSystemA = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    IdSystemB = table.Column<string>(type: "VARCHAR(255)", nullable: false),
                    CorrelationSource = table.Column<string>(type: "VARCHAR(20)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "DATETIME2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpiSentMsg", x => new { x.IdSystemA, x.IdSystemB });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpiPendingSystemBMsg_IdSystemB",
                table: "SpiPendingSystemBMsg",
                column: "IdSystemB",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpiPendingSystemBMsg_Timestamp_Amount",
                table: "SpiPendingSystemBMsg",
                columns: new[] { "Timestamp", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_SpiSentMsg_IdSystemA",
                table: "SpiSentMsg",
                column: "IdSystemA",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpiSentMsg_IdSystemB",
                table: "SpiSentMsg",
                column: "IdSystemB",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpiPendingSystemBMsg");

            migrationBuilder.DropTable(
                name: "SpiSentMsg");
        }
    }
}
