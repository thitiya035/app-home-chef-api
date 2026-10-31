
using HomeChef.Api.Contracts.Recipes;
using HomeChef.Api.Data;
using HomeChef.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeChef.Api.Controllers;

[ApiController]
[Route("api/recipes")]
public sealed class RecipesController(
    HomeChefDbContext db
) : ControllerBase
{
    // POST /api/recipes
    [HttpPost]
    public async Task<ActionResult<RecipeResponse>> Create(
        [FromBody] CreateRecipeRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Validate
        ValidateCreate(request);

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        // 2. Create Recipe
        var recipe = new Recipe
        {
            Name = request.Name!.Trim(),
            Description = CleanOptional(request.Description),
            Servings = request.Servings!.Value,
            ImageUrl = CleanOptional(request.ImageUrl),
            Category = CleanOptional(request.Category),
            Difficulty = CleanOptional(request.Difficulty)
        };

        // 3. Map Ingredients
        for (int i = 0; i < request.Ingredients!.Count; i++)
        {
            var input = request.Ingredients[i]!;
            decimal? quantity = null;

            if (input.Quantity is decimal raw)
            {
                decimal rounded = Math.Round(
                    raw, 2, MidpointRounding.AwayFromZero);

                quantity = rounded == 0m ? 0.01m : rounded;
            }

            recipe.Ingredients.Add(new RecipeIngredient
            {
                Position = i + 1,
                Name = input.Name!.Trim(),
                Quantity = quantity,
                Unit = CleanOptional(input.Unit),
                Note = CleanOptional(input.Note)
            });
        }

        // 4. Map Instructions
        for (int i = 0; i < request.Instructions!.Count; i++)
        {
            var input = request.Instructions[i]!;

            recipe.Instructions.Add(new RecipeInstruction
            {
                StepNumber = i + 1,
                Description = input.Description!.Trim()
            });
        }

        // 5. Insert into PostgreSQL
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(cancellationToken);

        // 6. Return 201 Created
        return CreatedAtAction(
            nameof(GetById),
            new { id = recipe.Id },
            ToResponse(recipe)
        );
    }

    // GET /api/recipes/{id}
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RecipeResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var recipe = await db.Recipes.AsNoTracking()
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(
                r => r.Id == id, cancellationToken);

        if (recipe is null)
            return NotFound();

        return Ok(ToResponse(recipe));
    }

    // Business Validation
    private void ValidateCreate(CreateRecipeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            ModelState.AddModelError(
                "name", "Name is required.");
        else if (request.Name.Trim().Length > 200)
            ModelState.AddModelError(
                "name", "Name must be 200 characters or fewer.");

        if (request.Servings is null or <= 0)
            ModelState.AddModelError(
                "servings", "Servings must be greater than 0.");

        CheckMaxLength(request.Category, 100, "category");
        CheckMaxLength(request.Difficulty, 30, "difficulty");

        if (CleanOptional(request.ImageUrl) is { } url &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             (parsed.Scheme != Uri.UriSchemeHttp &&
              parsed.Scheme != Uri.UriSchemeHttps)))
        {
            ModelState.AddModelError(
                "imageUrl", "ImageUrl must be an HTTP or HTTPS URL.");
        }

        if (request.Ingredients is not { Count: > 0 })
        {
            ModelState.AddModelError(
                "ingredients", "At least one ingredient is required.");
        }
        else
        {
            for (int i = 0; i < request.Ingredients.Count; i++)
            {
                var item = request.Ingredients[i];

                if (item is null)
                {
                    ModelState.AddModelError(
                        $"ingredients[{i}]", "Ingredient cannot be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name))
                    ModelState.AddModelError(
                        $"ingredients[{i}].name", "Name is required.");
                else if (item.Name.Trim().Length > 200)
                    ModelState.AddModelError(
                        $"ingredients[{i}].name",
                        "Name must be 200 characters or fewer.");

                if (item.Quantity is decimal raw &&
                    (raw <= 0m || raw > 9999.99m))
                {
                    ModelState.AddModelError(
                        $"ingredients[{i}].quantity",
                        "Quantity must be greater than 0 and at most 9999.99.");
                }

                if (item.Quantity.HasValue &&
                    string.IsNullOrWhiteSpace(item.Unit))
                {
                    ModelState.AddModelError(
                        $"ingredients[{i}].unit",
                        "Unit is required when quantity is specified.");
                }

                CheckMaxLength(
                    item.Unit, 50, $"ingredients[{i}].unit");
            }
        }

        if (request.Instructions is not { Count: > 0 })
        {
            ModelState.AddModelError(
                "instructions", "At least one instruction is required.");
        }
        else
        {
            for (int i = 0; i < request.Instructions.Count; i++)
            {
                var item = request.Instructions[i];

                if (item is null ||
                    string.IsNullOrWhiteSpace(item.Description))
                {
                    ModelState.AddModelError(
                        $"instructions[{i}].description",
                        "Description is required.");
                }
            }
        }
    }

    private void CheckMaxLength(
        string? value, int max, string field)
    {
        if (CleanOptional(value) is { } cleaned &&
            cleaned.Length > max)
        {
            ModelState.AddModelError(
                field, $"{field} must be {max} characters or fewer.");
        }
    }

    private static string? CleanOptional(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static RecipeResponse ToResponse(Recipe recipe) => new(
        recipe.Id,
        recipe.Name,
        recipe.Description,
        recipe.Ingredients.OrderBy(x => x.Position)
            .Select(x => new IngredientResponse(
                x.Name, x.Quantity, x.Unit, x.Note))
            .ToList(),
        recipe.Instructions.OrderBy(x => x.StepNumber)
            .Select(x => new InstructionResponse(
                x.StepNumber, x.Description))
            .ToList(),
        recipe.Servings,
        recipe.ImageUrl,
        recipe.Category,
        recipe.Difficulty
    );
}
