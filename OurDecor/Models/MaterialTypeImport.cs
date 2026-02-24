namespace OurDecor.Models
{
    public class MaterialTypeImport
    {
        public int Id { get; set; }
        public string TypeMaterial { get; set; }
        public decimal Mariage { get; set; }

        public List<MaterialImport> MaterialImports { get; set; }
    }
}
