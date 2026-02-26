namespace OurDecor.Models
{
    public class ProductsImport
    {
        public int Id { get; set; }
        public string TypeProduct { get; set; }
        public string NameProduct { get; set; }
        public int Article { get; set; }
        public decimal MinPricePartner { get; set; }
        public decimal WidthRoll { get; set; }

        public int ProductTypeId { get; set; }
        public ProductTypeImport ProductType { get; set; }

        public List<ProductMaterialsImport> ProductMaterialsImports { get; set; }
    }
}
