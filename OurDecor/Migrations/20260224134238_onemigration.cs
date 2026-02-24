using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OurDecor.Migrations
{
    /// <inheritdoc />
    public partial class onemigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaterialTypeImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TypeMaterial = table.Column<string>(type: "text", nullable: false),
                    Mariage = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialTypeImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductTypeImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TypeProduct = table.Column<string>(type: "text", nullable: false),
                    CoefficentProduct = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductTypeImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaterialImport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NameMaterial = table.Column<string>(type: "text", nullable: false),
                    TypeMaterial = table.Column<string>(type: "text", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    QuantityStock = table.Column<decimal>(type: "numeric", nullable: false),
                    MinQuantity = table.Column<decimal>(type: "numeric", nullable: false),
                    QuantityPackage = table.Column<int>(type: "integer", nullable: false),
                    Metering = table.Column<string>(type: "text", nullable: false),
                    MaterialTypeId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialImport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialImport_MaterialTypeImports_MaterialTypeId",
                        column: x => x.MaterialTypeId,
                        principalTable: "MaterialTypeImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductsImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TypeProduct = table.Column<string>(type: "text", nullable: false),
                    NameProduct = table.Column<string>(type: "text", nullable: false),
                    Article = table.Column<int>(type: "integer", nullable: false),
                    MinPricePartner = table.Column<decimal>(type: "numeric", nullable: false),
                    WidthRoll = table.Column<decimal>(type: "numeric", nullable: false),
                    ProductTypeId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductsImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductsImports_ProductTypeImports_ProductTypeId",
                        column: x => x.ProductTypeId,
                        principalTable: "ProductTypeImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductMaterialsImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Products = table.Column<string>(type: "text", nullable: false),
                    NameMaterial = table.Column<string>(type: "text", nullable: false),
                    QuantityMaterial = table.Column<decimal>(type: "numeric", nullable: false),
                    MateriaId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductMaterialsImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductMaterialsImports_MaterialImport_MateriaId",
                        column: x => x.MateriaId,
                        principalTable: "MaterialImport",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialImport_MaterialTypeId",
                table: "MaterialImport",
                column: "MaterialTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductMaterialsImports_MateriaId",
                table: "ProductMaterialsImports",
                column: "MateriaId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductsImports_ProductTypeId",
                table: "ProductsImports",
                column: "ProductTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductMaterialsImports");

            migrationBuilder.DropTable(
                name: "ProductsImports");

            migrationBuilder.DropTable(
                name: "MaterialImport");

            migrationBuilder.DropTable(
                name: "ProductTypeImports");

            migrationBuilder.DropTable(
                name: "MaterialTypeImports");
        }
    }
}
