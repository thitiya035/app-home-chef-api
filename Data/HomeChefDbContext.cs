
using HomeChef.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace HomeChef.Api.Data;

public sealed class HomeChefDbContext(
    DbContextOptions<HomeChefDbContext> options
) : DbContext(options)
{
    public DbSet<Recipe> Recipes => Set<Recipe>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recipe>(entity =>
        {
            entity.ToTable("recipes", "public");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            entity.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Description)
                .HasColumnName("description")
                .HasColumnType("text");

            entity.Property(x => x.Servings)
                .HasColumnName("servings")
                .IsRequired();

            entity.Property(x => x.ImageUrl)
                .HasColumnName("image_url");

            entity.Property(x => x.Category)
                .HasColumnName("category")
                .HasMaxLength(100);

            entity.Property(x => x.Difficulty)
                .HasColumnName("difficulty")
                .HasMaxLength(30);

            entity.HasMany(x => x.Ingredients)
                .WithOne()
                .HasForeignKey(x => x.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(x => x.Instructions)
                .WithOne()
                .HasForeignKey(x => x.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeIngredient>(entity =>
        {
            entity.ToTable("recipe_ingredients", "public");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(x => x.RecipeId)
                .HasColumnName("recipe_id");

            entity.Property(x => x.Position)
                .HasColumnName("position");

            entity.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Quantity)
                .HasColumnName("quantity")
                .HasPrecision(6, 2);

            entity.Property(x => x.Unit)
                .HasColumnName("unit")
                .HasMaxLength(50);

            entity.Property(x => x.Note)
                .HasColumnName("note");

            entity.HasIndex(x => new { x.RecipeId, x.Position })
                .IsUnique();
        });

        modelBuilder.Entity<RecipeInstruction>(entity =>
        {
            entity.ToTable("recipe_instructions", "public");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(x => x.RecipeId)
                .HasColumnName("recipe_id");

            entity.Property(x => x.StepNumber)
                .HasColumnName("step_number");

            entity.Property(x => x.Description)
                .HasColumnName("description")
                .IsRequired();

            entity.HasIndex(x => new { x.RecipeId, x.StepNumber })
                .IsUnique();
        });
    }
}
