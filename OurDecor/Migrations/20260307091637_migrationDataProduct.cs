using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OurDecor.Migrations
{
    /// <inheritdoc />
    public partial class migrationDataProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Products",
                columns: new[] { "Id", "Article", "MinPricePartner", "NameProduct", "ProductTypeId", "TypeProduct", "WidthRoll" },
                values: new object[,]
                {
                    { 3, 1549922, 16950.00m, "Фотообои флизелиновые Горы 500x270 см", null, "Фотообои", 0.91m },
                    { 4, 2018556, 15850.00m, "Обои из природного материала Традиционный принт светло", null, "Декоративные обои", 0.5m },
                    { 5, 3028272, 11430.00m, "Обои под покраску флизелиновые Рельеф", null, "Обои под покраску", 0.75m },
                    { 6, 4029272, 5630.00m, "Стеклообои Рогожка белые", null, "Стеклообои", 0.3m },
                    { 7, 2118827, 16200.00m, "Обои флизелиновые Эвелин светло-серые", null, "Декоративные обои", 1.06m },
                    { 8, 1758375, 13500.00m, "Обои под покраску флизелиновые цвет белый", null, "Обои под покраску", 0.68m }
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "NameRole" },
                values: new object[,]
                {
                    { 1, "Admin" },
                    { 2, "User" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 2);
        }
    }
}
