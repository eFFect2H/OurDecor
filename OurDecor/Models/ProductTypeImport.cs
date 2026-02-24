namespace OurDecor.Models
{
    public class ProductTypeImport
    {
        public int Id { get; set; }
        public string TypeProduct { get; set; }
        public decimal CoefficentProduct { get; set; }

        public List<ProductsImport> ProductsImports { get; set; }
    }
}
