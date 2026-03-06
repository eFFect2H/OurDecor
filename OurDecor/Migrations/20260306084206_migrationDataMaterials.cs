using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OurDecor.Migrations
{
    /// <inheritdoc />
    public partial class migrationDataMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "MaterialType",
                columns: new[] { "Id", "Mariage", "TypeMaterial" },
                values: new object[,]
                {
                    { 1, 0.7m, "Бумага" },
                    { 2, 0.5m, "Краска" },
                    { 3, 0.15m, "Клей" },
                    { 4, 0.2m, "Дисперсия" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "MaterialType",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "MaterialType",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "MaterialType",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "MaterialType",
                keyColumn: "Id",
                keyValue: 4);
        }
    }
}
