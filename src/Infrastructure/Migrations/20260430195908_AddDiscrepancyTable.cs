using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConvivenciaPix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscrepancyTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpiDiscrepancies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdSystemA = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdSystemB = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CorrelationSource = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Field = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SystemAValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SystemBValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpiDiscrepancies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpiDiscrepancies_IdSystemA_DetectedAt",
                table: "SpiDiscrepancies",
                columns: new[] { "IdSystemA", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpiDiscrepancies");
        }
    }
}
