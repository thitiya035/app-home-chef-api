
namespace HomeChef.Api.Contracts.Recipes;

public sealed record RecipeResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<IngredientResponse> Ingredients,
    IReadOnlyList<InstructionResponse> Instructions,
    int Servings,
    string? ImageUrl,
    string? Category,
    string? Difficulty
);

public sealed record IngredientResponse(
    string Name,
    decimal? Quantity,
    string? Unit,
    string? Note
);

public sealed record InstructionResponse(
    int StepNumber,
    string Description
);
