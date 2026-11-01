
using System.ComponentModel.DataAnnotations;
using HomeChef.Api.Contracts.Recipes;
using HomeChef.Api.Data;
using HomeChef.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeChef.Api.Controllers;

[ApiController]
[Route("api/recipes")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
public sealed class RecipesController(
    HomeChefDbContext db
) : ControllerBase
{
    // GET /api/recipes
    [HttpGet]
    [ProducesResponseType<RecipeListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecipeListResponse>> GetAll(
        CancellationToken cancellationToken,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int itempage = 10)
    {
        var query = db.Recipes.AsNoTracking();
        var totalRecord = await query.CountAsync(cancellationToken);
        var offset = ((long)page - 1) * itempage;

        if (offset >= totalRecord)
            return Ok(new RecipeListResponse(totalRecord, []));

        var recipes = await query
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .AsSplitQuery()
            .OrderBy(r => r.Id)
            .Skip((int)offset)
            .Take(itempage)
            .ToListAsync(cancellationToken);

        return Ok(new RecipeListResponse(totalRecord, recipes.Select(ToResponse).ToArray()));
    }

    // POST /api/recipes
    [HttpPost]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecipeResponse>> Create(
        [FromBody] CreateRecipeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecipe(request);

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var recipe = new Recipe();
        ApplyRecipe(request, recipe);

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetById),
            new { id = recipe.Id },
            ToResponse(recipe)
        );
    }

    // GET /api/recipes/{id}
    [HttpGet("{id:guid}")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
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

    // PUT /api/recipes/{id}
    [HttpPut("{id:guid}")]
    [ProducesResponseType<RecipeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeResponse>> Update(
        Guid id,
        [FromBody] CreateRecipeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecipe(request);
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var recipe = await db.Recipes
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
            return NotFound();

        db.RemoveRange(recipe.Ingredients);
        db.RemoveRange(recipe.Instructions);
        recipe.Ingredients.Clear();
        recipe.Instructions.Clear();

        // Free the unique (recipe_id, position/step_number) keys before replacement.
        // Both saves are in one transaction so a failure restores the original recipe.
        await db.SaveChangesAsync(cancellationToken);
        ApplyRecipe(request, recipe);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(ToResponse(recipe));
    }

    // DELETE /api/recipes/{id}
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deletedAt = DateTimeOffset.UtcNow;
        var affected = await db.Recipes
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.DeletedAt, deletedAt),
                cancellationToken);

        if (affected == 0)
            return NotFound();

        return NoContent();
    }

    // Business Validation
    private void ValidateRecipe(CreateRecipeRequest request)
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

    private static void ApplyRecipe(CreateRecipeRequest request, Recipe recipe)
    {
        recipe.Name = request.Name!.Trim();
        recipe.Description = CleanOptional(request.Description);
        recipe.Servings = request.Servings!.Value;
        recipe.ImageUrl = CleanOptional(request.ImageUrl);
        recipe.Category = CleanOptional(request.Category);
        recipe.Difficulty = CleanOptional(request.Difficulty);

        for (int i = 0; i < request.Ingredients!.Count; i++)
        {
            var input = request.Ingredients[i]!;
            decimal? quantity = null;
            if (input.Quantity is decimal raw)
            {
                decimal rounded = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
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

        for (int i = 0; i < request.Instructions!.Count; i++)
        {
            recipe.Instructions.Add(new RecipeInstruction
            {
                StepNumber = i + 1,
                Description = request.Instructions[i]!.Description!.Trim()
            });
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
