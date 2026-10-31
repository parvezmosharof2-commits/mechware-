using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Promotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    exclusive = table.Column<bool>(type: "boolean", nullable: false),
                    percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    fixed_amount = table.Column<long>(type: "bigint", nullable: false),
                    max_discount = table.Column<long>(type: "bigint", nullable: true),
                    min_subtotal = table.Column<long>(type: "bigint", nullable: false),
                    volume_breaks = table.Column<string>(type: "jsonb", nullable: false),
                    spend_breaks = table.Column<string>(type: "jsonb", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    global_limit = table.Column<int>(type: "integer", nullable: true),
                    per_user_limit = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    redeemed_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotions", x => x.id);
                    table.CheckConstraint("ck_promotions_limits", "(global_limit IS NULL OR global_limit >= 1) AND (per_user_limit IS NULL OR per_user_limit >= 1)");
                    table.CheckConstraint("ck_promotions_percent", "percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_promotions_redeemed", "redeemed_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "promotion_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_redemptions", x => x.id);
                    table.CheckConstraint("ck_promotion_redemptions_discount", "discount > 0");
                    table.ForeignKey(
                        name: "FK_promotion_redemptions_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_promotion_redemptions_promotions_promotion_id",
                        column: x => x.promotion_id,
                        principalTable: "promotions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "promotion_user_usage",
                columns: table => new
                {
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uses = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_user_usage", x => new { x.promotion_id, x.user_id });
                    table.CheckConstraint("ck_promotion_user_usage_uses", "uses >= 0");
                    table.ForeignKey(
                        name: "FK_promotion_user_usage_promotions_promotion_id",
                        column: x => x.promotion_id,
                        principalTable: "promotions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_promotion_user_usage_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_promotion_id",
                table: "promotion_redemptions",
                column: "promotion_id");

            migrationBuilder.CreateIndex(
                name: "ux_promotion_redemptions_order_promo",
                table: "promotion_redemptions",
                columns: new[] { "order_id", "promotion_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_user_usage_user_id",
                table: "promotion_user_usage",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_active",
                table: "promotions",
                columns: new[] { "is_active", "code" });

            migrationBuilder.CreateIndex(
                name: "ux_promotions_code",
                table: "promotions",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_redemptions");

            migrationBuilder.DropTable(
                name: "promotion_user_usage");

            migrationBuilder.DropTable(
                name: "promotions");
        }
    }
}
