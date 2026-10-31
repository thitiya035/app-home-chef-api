
namespace HomeChef.Api.Models;

public sealed class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Servings { get; set; }
    public string? ImageUrl { get; set; }
    public string? Category { get; set; }
    public string? Difficulty { get; set; }

    public List<RecipeIngredient> Ingredients { get; set; } = new();
    public List<RecipeInstruction> Instructions { get; set; } = new();
}

public sealed class RecipeIngredient
{
    public long Id { get; set; }
    public Guid RecipeId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? Note { get; set; }
}

public sealed class RecipeInstruction
{
    public long Id { get; set; }
    public Guid RecipeId { get; set; }
    public int StepNumber { get; set; }
    public string Description { get; set; } = string.Empty;
}
