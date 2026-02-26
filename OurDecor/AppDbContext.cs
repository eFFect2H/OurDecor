using Microsoft.EntityFrameworkCore;
using OurDecor.Models;

namespace OurDecor
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<MaterialImport> MaterialImport { get; set; }
        public DbSet<MaterialTypeImport> MaterialTypeImports { get; set; }
        public DbSet<ProductMaterialsImport> ProductMaterialsImports { get; set; }
        public DbSet<ProductsImport> ProductsImports { get; set; }  
        public DbSet<ProductTypeImport> ProductTypeImports { get; set; }    
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MaterialImport>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.MaterialType).WithMany(e => e.MaterialImports).HasForeignKey(e => e.MaterialTypeId);

                entity.HasMany(e => e.ProductMaterials).WithOne(e => e.Material).HasForeignKey(e => e.MateriaId);
            });

            modelBuilder.Entity<ProductTypeImport>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasMany(e => e.ProductsImports).WithOne(e => e.ProductType).HasForeignKey(e => e.ProductTypeId);
            });

            modelBuilder.Entity<ProductsImport>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasMany(e => e.ProductMaterialsImports).WithOne(e => e.Product).HasForeignKey(e => e.ProductsImportId);
            });

            modelBuilder.Entity<Role>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasMany(e => e.Users).WithOne(r => r.Role).HasForeignKey(r => r.RoleId);
            });
        }
    }
}
