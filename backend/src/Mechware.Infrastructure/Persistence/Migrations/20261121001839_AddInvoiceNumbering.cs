using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mechware.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_number_counters",
                columns: table => new
                {
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    next_serial = table.Column<long>(type: "bigint", nullable: false),
                    last_issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_number_counters", x => new { x.seller_id, x.series });
                    table.CheckConstraint("ck_invoice_number_counters_series", "series IN ('TaxInvoice', 'CreditNote', 'SpecimenTaxInvoice', 'SpecimenCreditNote')");
                    table.CheckConstraint("ck_invoice_number_counters_state", "(fiscal_year = 0 AND next_serial = 1 AND last_issued_at IS NULL) OR (fiscal_year BETWEEN 2000 AND 9998 AND next_serial >= 2 AND last_issued_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_invoice_number_counters_sellers_seller_id",
                        column: x => x.seller_id,
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // E6.2 step guard: +1 or a rollover only, no DELETE / TRUNCATE (InvoiceNumberingSql).
            migrationBuilder.Sql(InvoiceNumberingSql.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InvoiceNumberingSql.Uninstall);
            migrationBuilder.DropTable(
                name: "invoice_number_counters");
        }
    }
}
