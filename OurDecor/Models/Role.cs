namespace OurDecor.Models
{
    public class Role
    {
        public int Id { get; set; }
        public string NameRole { get; set; } = null!;

        public List<User> Users { get; set; }
    }
}
