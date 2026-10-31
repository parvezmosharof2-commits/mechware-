using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopVerse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdersAndSubOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_idempotency_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal = table.Column<long>(type: "bigint", nullable: false),
                    discount_total = table.Column<long>(type: "bigint", nullable: false),
                    tax_total = table.Column<long>(type: "bigint", nullable: false),
                    shipping_total = table.Column<long>(type: "bigint", nullable: false),
                    grand_total = table.Column<long>(type: "bigint", nullable: false),
                    ship_to = table.Column<string>(type: "jsonb", nullable: false),
                    event_seq = table.Column<int>(type: "integer", nullable: false),
                    placed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_discount", "discount_total <= subtotal");
                    table.CheckConstraint("ck_orders_money", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND shipping_total >= 0 AND grand_total >= 0");
                    table.ForeignKey(
                        name: "FK_orders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sub_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_order_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    goods_net = table.Column<long>(type: "bigint", nullable: false),
                    discount_total = table.Column<long>(type: "bigint", nullable: false),
                    tax_total = table.Column<long>(type: "bigint", nullable: false),
                    shipping_total = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    commission_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    commission = table.Column<long>(type: "bigint", nullable: false),
                    seller_earning = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sub_orders", x => x.id);
                    table.CheckConstraint("ck_sub_orders_commission", "commission_rate BETWEEN 0 AND 1 AND commission >= 0 AND seller_earning >= 0 AND commission + seller_earning = goods_net");
                    table.CheckConstraint("ck_sub_orders_money", "goods_net >= 0 AND discount_total >= 0 AND tax_total >= 0 AND shipping_total >= 0 AND total >= 0");
                    table.ForeignKey(
                        name: "FK_sub_orders_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<long>(type: "bigint", nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    gross = table.Column<long>(type: "bigint", nullable: false),
                    discount = table.Column<long>(type: "bigint", nullable: false),
                    net = table.Column<long>(type: "bigint", nullable: false),
                    tax = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_money", "unit_price >= 0 AND gross >= 0 AND discount >= 0 AND discount <= gross AND net >= 0 AND tax >= 0 AND total = net + tax");
                    table.CheckConstraint("ck_order_items_qty", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_order_items_sub_orders_sub_order_id",
                        column: x => x.sub_order_id,
                        principalTable: "sub_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_user_key",
                table: "idempotency_keys",
                columns: new[] { "user_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_order_events_order_seq",
                table: "order_events",
                columns: new[] { "order_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_items_sub_order_id",
                table: "order_items",
                column: "sub_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_items_variant",
                table: "order_items",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status",
                table: "orders",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_placed",
                table: "orders",
                columns: new[] { "user_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_orders_number",
                table: "orders",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sub_orders_order_id",
                table: "sub_orders",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_sub_orders_seller_status",
                table: "sub_orders",
                columns: new[] { "seller_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_sub_orders_number",
                table: "sub_orders",
                column: "sub_order_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "sub_orders");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
