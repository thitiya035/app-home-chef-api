
namespace HomeChef.Api.Contracts.Recipes;

public sealed class CreateRecipeRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<CreateIngredientRequest?>? Ingredients { get; set; }
    public List<CreateInstructionRequest?>? Instructions { get; set; }
    public int? Servings { get; set; }
    public string? ImageUrl { get; set; }
    public string? Category { get; set; }
    public string? Difficulty { get; set; }
}

public sealed class CreateIngredientRequest
{
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? Note { get; set; }
}

public sealed class CreateInstructionRequest
{
    public string? Description { get; set; }
}
