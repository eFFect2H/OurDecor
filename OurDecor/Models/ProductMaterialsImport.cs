namespace OurDecor.Models
{
    public class ProductMaterialsImport
    {
        public int Id { get; set; }
        public string Products { get; set; }
        public string NameMaterial { get; set; }
        public decimal QuantityMaterial { get; set; }

        public int MateriaId { get; set; }
        public MaterialImport Material { get; set; }

        public int ProductsImportId { get; set; }
        public ProductsImport Product { get; set; }
    }
}
