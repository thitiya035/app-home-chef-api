namespace HomeChef.Api.Contracts.Recipes;

public sealed record RecipeListResponse(
    int TotalRecord,
    IReadOnlyList<RecipeResponse> Record
);
