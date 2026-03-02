namespace OurDecor.Models
{
    public class MaterialImport
    {
        public int Id { get; set; }
        public string? NameMaterial { get; set; }
        public string? TypeMaterial { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal QuantityStock {  get; set; }
        public decimal MinQuantity {  get; set; }
        public int QuantityPackage { get; set; }
        public string? Metering {  get; set; }

        public int? MaterialTypeId { get; set; }
        public MaterialTypeImport? MaterialType { get; set; }

        public List<ProductMaterialsImport>? ProductMaterials { get; set; }
    }
}
