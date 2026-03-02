using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OurDecor.Migrations
{
    /// <inheritdoc />
    public partial class NewMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaterialImport_MaterialTypeImports_MaterialTypeId",
                table: "MaterialImport");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductMaterialsImports_MaterialImport_MateriaId",
                table: "ProductMaterialsImports");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductsImports_ProductTypeImports_ProductTypeId",
                table: "ProductsImports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductTypeImports",
                table: "ProductTypeImports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductsImports",
                table: "ProductsImports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductMaterialsImports",
                table: "ProductMaterialsImports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MaterialTypeImports",
                table: "MaterialTypeImports");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MaterialImport",
                table: "MaterialImport");

            migrationBuilder.RenameTable(
                name: "ProductTypeImports",
                newName: "ProductType");

            migrationBuilder.RenameTable(
                name: "ProductsImports",
                newName: "Products");

            migrationBuilder.RenameTable(
                name: "ProductMaterialsImports",
                newName: "ProductMaterials");

            migrationBuilder.RenameTable(
                name: "MaterialTypeImports",
                newName: "MaterialType");

            migrationBuilder.RenameTable(
                name: "MaterialImport",
                newName: "Material");

            migrationBuilder.RenameIndex(
                name: "IX_ProductsImports_ProductTypeId",
                table: "Products",
                newName: "IX_Products_ProductTypeId");

            migrationBuilder.RenameIndex(
                name: "IX_ProductMaterialsImports_MateriaId",
                table: "ProductMaterials",
                newName: "IX_ProductMaterials_MateriaId");

            migrationBuilder.RenameIndex(
                name: "IX_MaterialImport_MaterialTypeId",
                table: "Material",
                newName: "IX_Material_MaterialTypeId");

            migrationBuilder.AlterColumn<string>(
                name: "TypeProduct",
                table: "Products",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "ProductsImportId",
                table: "ProductMaterials",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "TypeMaterial",
                table: "Material",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "NameMaterial",
                table: "Material",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Metering",
                table: "Material",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<int>(
                name: "MaterialTypeId",
                table: "Material",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductType",
                table: "ProductType",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Products",
                table: "Products",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductMaterials",
                table: "ProductMaterials",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MaterialType",
                table: "MaterialType",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Material",
                table: "Material",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NameRole = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Login = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductMaterials_ProductsImportId",
                table: "ProductMaterials",
                column: "ProductsImportId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Material_MaterialType_MaterialTypeId",
                table: "Material",
                column: "MaterialTypeId",
                principalTable: "MaterialType",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductMaterials_Material_MateriaId",
                table: "ProductMaterials",
                column: "MateriaId",
                principalTable: "Material",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductMaterials_Products_ProductsImportId",
                table: "ProductMaterials",
                column: "ProductsImportId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductType_ProductTypeId",
                table: "Products",
                column: "ProductTypeId",
                principalTable: "ProductType",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Material_MaterialType_MaterialTypeId",
                table: "Material");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductMaterials_Material_MateriaId",
                table: "ProductMaterials");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductMaterials_Products_ProductsImportId",
                table: "ProductMaterials");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductType_ProductTypeId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductType",
                table: "ProductType");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Products",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductMaterials",
                table: "ProductMaterials");

            migrationBuilder.DropIndex(
                name: "IX_ProductMaterials_ProductsImportId",
                table: "ProductMaterials");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MaterialType",
                table: "MaterialType");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Material",
                table: "Material");

            migrationBuilder.DropColumn(
                name: "ProductsImportId",
                table: "ProductMaterials");

            migrationBuilder.RenameTable(
                name: "ProductType",
                newName: "ProductTypeImports");

            migrationBuilder.RenameTable(
                name: "Products",
                newName: "ProductsImports");

            migrationBuilder.RenameTable(
                name: "ProductMaterials",
                newName: "ProductMaterialsImports");

            migrationBuilder.RenameTable(
                name: "MaterialType",
                newName: "MaterialTypeImports");

            migrationBuilder.RenameTable(
                name: "Material",
                newName: "MaterialImport");

            migrationBuilder.RenameIndex(
                name: "IX_Products_ProductTypeId",
                table: "ProductsImports",
                newName: "IX_ProductsImports_ProductTypeId");

            migrationBuilder.RenameIndex(
                name: "IX_ProductMaterials_MateriaId",
                table: "ProductMaterialsImports",
                newName: "IX_ProductMaterialsImports_MateriaId");

            migrationBuilder.RenameIndex(
                name: "IX_Material_MaterialTypeId",
                table: "MaterialImport",
                newName: "IX_MaterialImport_MaterialTypeId");

            migrationBuilder.AlterColumn<string>(
                name: "TypeProduct",
                table: "ProductsImports",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TypeMaterial",
                table: "MaterialImport",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NameMaterial",
                table: "MaterialImport",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Metering",
                table: "MaterialImport",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MaterialTypeId",
                table: "MaterialImport",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductTypeImports",
                table: "ProductTypeImports",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductsImports",
                table: "ProductsImports",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductMaterialsImports",
                table: "ProductMaterialsImports",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MaterialTypeImports",
                table: "MaterialTypeImports",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MaterialImport",
                table: "MaterialImport",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialImport_MaterialTypeImports_MaterialTypeId",
                table: "MaterialImport",
                column: "MaterialTypeId",
                principalTable: "MaterialTypeImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductMaterialsImports_MaterialImport_MateriaId",
                table: "ProductMaterialsImports",
                column: "MateriaId",
                principalTable: "MaterialImport",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductsImports_ProductTypeImports_ProductTypeId",
                table: "ProductsImports",
                column: "ProductTypeId",
                principalTable: "ProductTypeImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
