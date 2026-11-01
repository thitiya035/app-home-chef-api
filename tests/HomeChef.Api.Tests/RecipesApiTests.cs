using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeChef.Api.Contracts.Recipes;
using HomeChef.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace HomeChef.Api.Tests;

public sealed class RecipesApiTests : IAsyncLifetime
{
    private readonly string databaseName = $"homechef_test_{Guid.NewGuid():N}";
    private string adminConnectionString = null!;
    private string testConnectionString = null!;
    private RecipeApiFactory factory = null!;
    private HttpClient client = null!;
    private bool databaseCreated;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<RecipesApiTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        adminConnectionString = configuration["HOMECHEF_TEST_CONNECTION_STRING"]!;
        if (string.IsNullOrWhiteSpace(adminConnectionString))
            throw new InvalidOperationException(
                "Set HOMECHEF_TEST_CONNECTION_STRING as an environment variable or a User Secret " +
                "on the test project to a dedicated PostgreSQL test server (CREATEDB required). " +
                "See README.md for setup instructions.");

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
        await command.ExecuteNonQueryAsync();
        databaseCreated = true;

        testConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;
        await using var db = OpenDb();
        await db.Database.EnsureCreatedAsync();

        factory = new RecipeApiFactory(testConnectionString);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        if (!databaseCreated)
            return;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }

    private HomeChefDbContext OpenDb() => new(
        new DbContextOptionsBuilder<HomeChefDbContext>().UseNpgsql(testConnectionString).Options);

    private static CreateRecipeRequest ValidRequest() => new()
    {
        Name = " Thai omelette ",
        Description = " Original description ",
        Servings = 2,
        ImageUrl = "https://example.com/recipe.png",
        Category = " Thai ",
        Difficulty = " Easy ",
        Ingredients = [new() { Name = " Egg ", Quantity = 2, Unit = " egg ", Note = " Fresh " }],
        Instructions = [new() { Description = " Beat eggs " }]
    };

    private async Task<RecipeResponse> CreateRecipe(CreateRecipeRequest? request = null)
    {
        using var response = await client.PostAsJsonAsync("/api/recipes", request ?? ValidRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeResponse>();
        Assert.NotNull(recipe);
        return recipe;
    }

    [Fact]
    public async Task List_returns_total_and_records_with_ordered_children()
    {
        var request = ValidRequest();
        request.Ingredients!.Add(new() { Name = "Pork", Quantity = 100, Unit = "g" });
        request.Instructions!.Add(new() { Description = "Fry" });
        var first = await CreateRecipe(request);
        var second = await CreateRecipe();

        using var response = await client.GetAsync("/api/recipes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RecipeListResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result.TotalRecord);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), result.Record.Select(r => r.Id));
        var retrieved = Assert.Single(result.Record, r => r.Id == first.Id);
        Assert.Equal(new[] { "Egg", "Pork" }, retrieved.Ingredients.Select(i => i.Name));
        Assert.Equal(new[] { 1, 2 }, retrieved.Instructions.Select(i => i.StepNumber));
        Assert.Equal(new[] { "Beat eggs", "Fry" }, retrieved.Instructions.Select(i => i.Description));
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("page=1.5", "page")]
    [InlineData("page=2147483648", "page")]
    [InlineData("page=", "page")]
    [InlineData("itempage=0", "itempage")]
    [InlineData("itempage=-1", "itempage")]
    [InlineData("itempage=101", "itempage")]
    [InlineData("itempage=2147483647", "itempage")]
    [InlineData("itempage=abc", "itempage")]
    [InlineData("itempage=1.5", "itempage")]
    [InlineData("itempage=2147483648", "itempage")]
    [InlineData("itempage=", "itempage")]
    public async Task List_rejects_invalid_pagination(string query, string field)
    {
        using var response = await client.GetAsync($"/api/recipes?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey(field));
    }

    [Fact]
    public async Task Pagination_defaults_and_pages_return_all_active_recipes_once_without_overflow()
    {
        var created = new List<RecipeResponse>();
        for (var i = 0; i < 13; i++)
            created.Add(await CreateRecipe());
        var deletedId = created[6].Id;
        using var deletedResponse = await client.DeleteAsync($"/api/recipes/{deletedId}");
        Assert.Equal(HttpStatusCode.NoContent, deletedResponse.StatusCode);
        var expectedIds = created.Where(r => r.Id != deletedId).Select(r => r.Id).Order().ToArray();

        var defaultPage = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(defaultPage);
        Assert.Equal(12, defaultPage.TotalRecord);
        Assert.Equal(10, defaultPage.Record.Count);
        Assert.Equal(expectedIds.Take(10), defaultPage.Record.Select(r => r.Id));

        var secondDefaultPage = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes?page=2");
        Assert.NotNull(secondDefaultPage);
        Assert.Equal(12, secondDefaultPage.TotalRecord);
        Assert.Equal(2, secondDefaultPage.Record.Count);
        Assert.Equal(expectedIds.Skip(10), secondDefaultPage.Record.Select(r => r.Id));

        var firstSmallPage = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes?itempage=5");
        Assert.NotNull(firstSmallPage);
        Assert.Equal(12, firstSmallPage.TotalRecord);
        Assert.Equal(5, firstSmallPage.Record.Count);
        Assert.Equal(expectedIds.Take(5), firstSmallPage.Record.Select(r => r.Id));

        var singleItemPage = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes?page=2&itempage=1");
        Assert.NotNull(singleItemPage);
        Assert.Equal(12, singleItemPage.TotalRecord);
        Assert.Equal(expectedIds[1], Assert.Single(singleItemPage.Record).Id);

        var allPagedIds = new List<Guid>();
        foreach (var (pageNumber, expectedCount) in new[] { (1, 5), (2, 5), (3, 2) })
        {
            var result = await client.GetFromJsonAsync<RecipeListResponse>(
                $"/api/recipes?page={pageNumber}&itempage=5");
            Assert.NotNull(result);
            Assert.Equal(12, result.TotalRecord);
            Assert.Equal(expectedCount, result.Record.Count);
            foreach (var recipe in result.Record)
            {
                Assert.Equal(new IngredientResponse("Egg", 2, "egg", "Fresh"), Assert.Single(recipe.Ingredients));
                Assert.Equal(new InstructionResponse(1, "Beat eggs"), Assert.Single(recipe.Instructions));
                allPagedIds.Add(recipe.Id);
            }
        }
        Assert.Equal(expectedIds, allPagedIds);

        var largestPageSize = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes?page=1&itempage=100");
        Assert.NotNull(largestPageSize);
        Assert.Equal(12, largestPageSize.TotalRecord);
        Assert.Equal(expectedIds, largestPageSize.Record.Select(r => r.Id));

        foreach (var query in new[] { "page=4&itempage=5", "page=2147483647&itempage=100" })
        {
            var beyondLastPage = await client.GetFromJsonAsync<RecipeListResponse>($"/api/recipes?{query}");
            Assert.NotNull(beyondLastPage);
            Assert.Equal(12, beyondLastPage.TotalRecord);
            Assert.Empty(beyondLastPage.Record);
        }
    }

    [Fact]
    public async Task Update_replaces_children_clears_optional_fields_and_can_be_repeated()
    {
        var original = ValidRequest();
        original.Ingredients!.Add(new() { Name = "Pork", Quantity = 100, Unit = "g" });
        original.Instructions!.Add(new() { Description = "Fry" });
        var created = await CreateRecipe(original);

        var replacement = new CreateRecipeRequest
        {
            Name = " New recipe ",
            Servings = 4,
            Ingredients = [new() { Name = " Oil ", Quantity = 0.001m, Unit = " tsp " }],
            Instructions = [new() { Description = " Heat pan " }, new() { Description = " Cook " },
                new() { Description = " Serve " }]
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PutAsJsonAsync($"/api/recipes/{created.Id}", replacement);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var updated = await response.Content.ReadFromJsonAsync<RecipeResponse>();
            Assert.NotNull(updated);
            Assert.Equal(created.Id, updated.Id);
            Assert.Equal("New recipe", updated.Name);
            Assert.Equal(4, updated.Servings);
            Assert.Null(updated.Description);
            Assert.Null(updated.ImageUrl);
            Assert.Null(updated.Category);
            Assert.Null(updated.Difficulty);
            Assert.Equal(new IngredientResponse("Oil", 0.01m, "tsp", null), Assert.Single(updated.Ingredients));
            Assert.Equal(new[] { 1, 2, 3 }, updated.Instructions.Select(i => i.StepNumber));
            Assert.Equal(new[] { "Heat pan", "Cook", "Serve" }, updated.Instructions.Select(i => i.Description));

            var persisted = await client.GetStringAsync($"/api/recipes/{created.Id}");
            Assert.Equal(await response.Content.ReadAsStringAsync(), persisted);
        }

        // Replace again with reordered, expanded ingredients and fewer steps.
        replacement.Description = " New description ";
        replacement.ImageUrl = " https://example.com/new.png ";
        replacement.Category = " Updated ";
        replacement.Difficulty = " Medium ";
        replacement.Ingredients = [new() { Name = "Pork", Quantity = 1.235m, Unit = "g", Note = " Chopped " },
            new() { Name = "Egg", Quantity = 3, Unit = "egg" }];
        replacement.Instructions = [new() { Description = "Serve immediately" }];
        using var finalResponse = await client.PutAsJsonAsync($"/api/recipes/{created.Id}", replacement);
        Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
        var final = await client.GetFromJsonAsync<RecipeResponse>($"/api/recipes/{created.Id}");
        Assert.NotNull(final);
        Assert.Equal("New description", final.Description);
        Assert.Equal("https://example.com/new.png", final.ImageUrl);
        Assert.Equal("Updated", final.Category);
        Assert.Equal("Medium", final.Difficulty);
        Assert.Equal(new[] { "Pork", "Egg" }, final.Ingredients.Select(i => i.Name));
        Assert.Equal(new IngredientResponse("Pork", 1.24m, "g", "Chopped"), final.Ingredients[0]);
        Assert.Equal(new InstructionResponse(1, "Serve immediately"), Assert.Single(final.Instructions));
    }

    [Fact]
    public async Task Delete_retains_data_but_hides_recipe_from_read_update_and_repeated_delete()
    {
        var deleted = await CreateRecipe();
        var survivor = await CreateRecipe();
        var before = DateTimeOffset.UtcNow;

        using var response = await client.DeleteAsync($"/api/recipes/{deleted.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/recipes/{deleted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/recipes/{deleted.Id}", ValidRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recipes/{deleted.Id}")).StatusCode);
        var list = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(list);
        Assert.Equal(1, list.TotalRecord);
        Assert.Equal(survivor.Id, Assert.Single(list.Record).Id);

        await using var connection = new NpgsqlConnection(testConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT deleted_at,
                   (SELECT count(*) FROM public.recipe_ingredients WHERE recipe_id = @id),
                   (SELECT count(*) FROM public.recipe_instructions WHERE recipe_id = @id)
            FROM public.recipes WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", deleted.Id);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            var deletedAt = reader.GetFieldValue<DateTimeOffset>(0);
            Assert.Equal(TimeSpan.Zero, deletedAt.Offset);
            Assert.InRange(deletedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recipes/{survivor.Id}")).StatusCode);
        var emptyList = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(emptyList);
        Assert.Equal(0, emptyList.TotalRecord);
        Assert.Empty(emptyList.Record);
    }

    [Fact]
    public async Task Empty_list_returns_empty_page_but_missing_recipe_still_returns_not_found()
    {
        var id = Guid.NewGuid();
        var emptyList = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(emptyList);
        Assert.Equal(0, emptyList.TotalRecord);
        Assert.Empty(emptyList.Record);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/recipes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/recipes/{id}", ValidRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/recipes/{id}")).StatusCode);
    }

    [Fact]
    public async Task Failed_update_rolls_back_scalars_and_children_and_returns_safe_problem_details()
    {
        var created = await CreateRecipe();
        var before = await client.GetStringAsync($"/api/recipes/{created.Id}");
        await using (var connection = new NpgsqlConnection(testConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                CREATE FUNCTION public.reject_test_instruction() RETURNS trigger AS $$
                BEGIN
                    IF NEW.description = 'FAIL_TEST_INSERT' THEN
                        RAISE EXCEPTION 'sensitive database failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_test_instruction BEFORE INSERT ON public.recipe_instructions
                    FOR EACH ROW EXECUTE FUNCTION public.reject_test_instruction();
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        var replacement = ValidRequest();
        replacement.Name = "This must roll back";
        replacement.Ingredients = [new() { Name = "Changed ingredient" }];
        replacement.Instructions = [new() { Description = "FAIL_TEST_INSERT" }];
        using var response = await client.PutAsJsonAsync($"/api/recipes/{created.Id}", replacement);

        await AssertSafeServerError(response);
        Assert.Equal(before, await client.GetStringAsync($"/api/recipes/{created.Id}"));
        var list = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(list);
        Assert.Equal(1, list.TotalRecord);
        Assert.Equal("Thai omelette", Assert.Single(list.Record).Name);
    }

    [Theory]
    [InlineData("name_missing", "name")]
    [InlineData("name_blank", "name")]
    [InlineData("name_long", "name")]
    [InlineData("servings_missing", "servings")]
    [InlineData("servings_zero", "servings")]
    [InlineData("servings_negative", "servings")]
    [InlineData("ingredients_missing", "ingredients")]
    [InlineData("ingredients_empty", "ingredients")]
    [InlineData("ingredient_null", "ingredients[0]")]
    [InlineData("ingredient_name_missing", "ingredients[0].name")]
    [InlineData("ingredient_name_blank", "ingredients[0].name")]
    [InlineData("ingredient_name_long", "ingredients[0].name")]
    [InlineData("quantity_zero", "ingredients[0].quantity")]
    [InlineData("quantity_negative", "ingredients[0].quantity")]
    [InlineData("quantity_large", "ingredients[0].quantity")]
    [InlineData("unit_missing", "ingredients[0].unit")]
    [InlineData("unit_blank", "ingredients[0].unit")]
    [InlineData("unit_long", "ingredients[0].unit")]
    [InlineData("instructions_missing", "instructions")]
    [InlineData("instructions_empty", "instructions")]
    [InlineData("instruction_null", "instructions[0].description")]
    [InlineData("instruction_description_missing", "instructions[0].description")]
    [InlineData("instruction_description_blank", "instructions[0].description")]
    [InlineData("category_long", "category")]
    [InlineData("difficulty_long", "difficulty")]
    [InlineData("image_relative", "imageUrl")]
    [InlineData("image_ftp", "imageUrl")]
    public async Task Post_and_put_apply_same_validation_without_changing_data(string scenario, string field)
    {
        var created = await CreateRecipe();
        var before = await client.GetStringAsync($"/api/recipes/{created.Id}");
        var request = ValidRequest();
        switch (scenario)
        {
            case "name_missing": request.Name = null; break;
            case "name_blank": request.Name = "  "; break;
            case "name_long": request.Name = new string('n', 201); break;
            case "servings_missing": request.Servings = null; break;
            case "servings_zero": request.Servings = 0; break;
            case "servings_negative": request.Servings = -1; break;
            case "ingredients_missing": request.Ingredients = null; break;
            case "ingredients_empty": request.Ingredients = []; break;
            case "ingredient_null": request.Ingredients = [null]; break;
            case "ingredient_name_missing": request.Ingredients![0]!.Name = null; break;
            case "ingredient_name_blank": request.Ingredients![0]!.Name = "  "; break;
            case "ingredient_name_long": request.Ingredients![0]!.Name = new string('i', 201); break;
            case "quantity_zero": request.Ingredients![0]!.Quantity = 0; break;
            case "quantity_negative": request.Ingredients![0]!.Quantity = -1; break;
            case "quantity_large": request.Ingredients![0]!.Quantity = 10000; break;
            case "unit_missing": request.Ingredients![0]!.Unit = null; break;
            case "unit_blank": request.Ingredients![0]!.Unit = "  "; break;
            case "unit_long": request.Ingredients![0]!.Unit = new string('u', 51); break;
            case "instructions_missing": request.Instructions = null; break;
            case "instructions_empty": request.Instructions = []; break;
            case "instruction_null": request.Instructions = [null]; break;
            case "instruction_description_missing": request.Instructions![0]!.Description = null; break;
            case "instruction_description_blank": request.Instructions![0]!.Description = "  "; break;
            case "category_long": request.Category = new string('c', 101); break;
            case "difficulty_long": request.Difficulty = new string('d', 31); break;
            case "image_relative": request.ImageUrl = "/image.png"; break;
            case "image_ftp": request.ImageUrl = "ftp://example.com/image.png"; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        using var post = await client.PostAsJsonAsync("/api/recipes", request);
        using var put = await client.PutAsJsonAsync($"/api/recipes/{created.Id}", request);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var postProblem = await post.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        var putProblem = await put.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(postProblem);
        Assert.NotNull(putProblem);
        Assert.True(postProblem.Errors.ContainsKey(field), await post.Content.ReadAsStringAsync());
        Assert.Equal(postProblem.Errors[field], putProblem.Errors[field]);
        Assert.Equal(before, await client.GetStringAsync($"/api/recipes/{created.Id}"));
        var list = await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes");
        Assert.NotNull(list);
        Assert.Equal(1, list.TotalRecord);
        Assert.Equal(created.Id, Assert.Single(list.Record).Id);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{bad json")]
    public async Task Invalid_body_returns_bad_request_without_changing_data(string payload)
    {
        var created = await CreateRecipe();
        var before = await client.GetStringAsync($"/api/recipes/{created.Id}");
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            var url = method == HttpMethod.Post ? "/api/recipes" : $"/api/recipes/{created.Id}";
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(before, await client.GetStringAsync($"/api/recipes/{created.Id}"));
    }

    [Theory]
    [InlineData("GET", "Production")]
    [InlineData("POST", "Production")]
    [InlineData("PUT", "Production")]
    [InlineData("DELETE", "Production")]
    [InlineData("GET", "Development")]
    [InlineData("POST", "Development")]
    [InlineData("PUT", "Development")]
    [InlineData("DELETE", "Development")]
    public async Task Database_errors_return_safe_problem_details(string method, string environment)
    {
        var created = await CreateRecipe();
        await using (var connection = new NpgsqlConnection(testConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "ALTER TABLE public.recipes RENAME TO unavailable_recipes", connection);
            await command.ExecuteNonQueryAsync();
        }

        await using var errorFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var errorClient = errorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var url = method is "GET" or "POST" ? "/api/recipes" : $"/api/recipes/{created.Id}";
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(ValidRequest());
        using var response = await errorClient.SendAsync(request);
        await AssertSafeServerError(response);
    }

    [Fact]
    public async Task Schema_script_upgrades_existing_rows_and_is_repeatable()
    {
        var created = await CreateRecipe();
        var before = await client.GetStringAsync($"/api/recipes/{created.Id}");
        await using var connection = new NpgsqlConnection(testConnectionString);
        await connection.OpenAsync();
        await using (var legacy = new NpgsqlCommand("ALTER TABLE public.recipes DROP COLUMN deleted_at", connection))
            await legacy.ExecuteNonQueryAsync();

        var script = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Sql", "001_add_recipe_deleted_at.sql"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var upgrade = new NpgsqlCommand(script, connection);
            await upgrade.ExecuteNonQueryAsync();
            Assert.Equal(before, await client.GetStringAsync($"/api/recipes/{created.Id}"));
        }

        await using var check = new NpgsqlCommand("SELECT deleted_at FROM public.recipes WHERE id = @id", connection);
        check.Parameters.AddWithValue("id", created.Id);
        Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/recipes/{created.Id}")).StatusCode);
        var deletedAt = await check.ExecuteScalarAsync();
        Assert.NotEqual(DBNull.Value, deletedAt);
        await using var repeatAfterDelete = new NpgsqlCommand(script, connection);
        await repeatAfterDelete.ExecuteNonQueryAsync();
        Assert.Equal(deletedAt, await check.ExecuteScalarAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/recipes/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Openapi_describes_list_update_delete_and_does_not_expose_deleted_at()
    {
        await using var devFactory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var devClient = devFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var document = await devClient.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
        Assert.NotNull(document);
        var paths = document.RootElement.GetProperty("paths");
        var list = paths.GetProperty("/api/recipes").GetProperty("get");
        Assert.EndsWith("/RecipeListResponse", list.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("RecipeListResponse").GetProperty("properties");
        var totalRecordType = properties.GetProperty("totalRecord").GetProperty("type");
        Assert.Contains("integer", totalRecordType.ValueKind == JsonValueKind.Array
            ? totalRecordType.EnumerateArray().Select(type => type.GetString())
            : new[] { totalRecordType.GetString() });
        var recordType = properties.GetProperty("record").GetProperty("type");
        Assert.Contains("array", recordType.ValueKind == JsonValueKind.Array
            ? recordType.EnumerateArray().Select(type => type.GetString())
            : new[] { recordType.GetString() });
        Assert.EndsWith("/RecipeResponse", properties.GetProperty("record")
            .GetProperty("items").GetProperty("$ref").GetString());
        Assert.True(list.GetProperty("responses").TryGetProperty("400", out _));
        Assert.False(list.GetProperty("responses").TryGetProperty("404", out _));
        var parameters = list.GetProperty("parameters").EnumerateArray().ToArray();
        var pageParameter = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "page");
        var itempageParameter = Assert.Single(parameters, p => p.GetProperty("name").GetString() == "itempage");
        Assert.Equal("query", pageParameter.GetProperty("in").GetString());
        Assert.Equal("query", itempageParameter.GetProperty("in").GetString());
        Assert.Equal(1, pageParameter.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(10, itempageParameter.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(1, pageParameter.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(1, itempageParameter.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, itempageParameter.GetProperty("schema").GetProperty("maximum").GetInt32());
        var put = paths.GetProperty("/api/recipes/{id}").GetProperty("put");
        Assert.True(put.TryGetProperty("requestBody", out _));
        Assert.True(put.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(put.GetProperty("responses").TryGetProperty("404", out _));
        var delete = paths.GetProperty("/api/recipes/{id}").GetProperty("delete");
        Assert.False(delete.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        Assert.True(delete.GetProperty("responses").TryGetProperty("500", out _));
        Assert.DoesNotContain("deletedAt", document.RootElement.GetProperty("components").GetProperty("schemas").ToString());
    }

    [Fact]
    public async Task Reported_Thai_recipe_can_be_created_after_legacy_schema_upgrade()
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "create-kaprao.json"));
        await using var connection = new NpgsqlConnection(testConnectionString);
        await connection.OpenAsync();
        await using (var legacy = new NpgsqlCommand("ALTER TABLE public.recipes DROP COLUMN deleted_at", connection))
            await legacy.ExecuteNonQueryAsync();

        // Reproduce the deployment failure with the exact request, then apply the shipped fix.
        using (var legacyResponse = await client.PostAsync("/api/recipes",
            new StringContent(payload, Encoding.UTF8, "application/json")))
        {
            await AssertSafeServerError(legacyResponse);
        }

        var script = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Sql", "001_add_recipe_deleted_at.sql"));
        await using (var upgrade = new NpgsqlCommand(script, connection))
            await upgrade.ExecuteNonQueryAsync();

        using var response = await client.PostAsync("/api/recipes",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeResponse>();
        Assert.NotNull(recipe);
        Assert.Equal("ข้าวกะเพราหมูสับ", recipe.Name);
        Assert.Equal("ข้าวกะเพราหมูสับรสจัดจ้าน หอมใบกะเพรา ทำง่าย", recipe.Description);
        Assert.Equal(2, recipe.Servings);
        Assert.Null(recipe.ImageUrl);
        Assert.Equal("อาหารจานเดียว", recipe.Category);
        Assert.Equal("Easy", recipe.Difficulty);
        Assert.Equal(6, recipe.Ingredients.Count);
        Assert.Equal(new IngredientResponse("หมูสับ", 200, "กรัม", "เลือกหมูสับติดมันเล็กน้อย"), recipe.Ingredients[0]);
        Assert.Null(recipe.Ingredients[1].Note);
        Assert.Null(recipe.Ingredients[4].Note);
        Assert.Null(recipe.Ingredients[5].Note);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, recipe.Instructions.Select(i => i.StepNumber));
        Assert.Equal("ตักเสิร์ฟพร้อมข้าวสวยร้อน ๆ", recipe.Instructions[5].Description);
        Assert.NotNull(response.Headers.Location);
        var persisted = await client.GetFromJsonAsync<RecipeResponse>(response.Headers.Location);
        Assert.NotNull(persisted);
        Assert.Equal(recipe.Id, persisted.Id);
        Assert.Equal(recipe.Name, persisted.Name);
        Assert.Equal(recipe.Description, persisted.Description);
        Assert.Equal(recipe.Servings, persisted.Servings);
        Assert.Equal(recipe.ImageUrl, persisted.ImageUrl);
        Assert.Equal(recipe.Category, persisted.Category);
        Assert.Equal(recipe.Difficulty, persisted.Difficulty);
        Assert.Equal<IngredientResponse>(recipe.Ingredients, persisted.Ingredients);
        Assert.Equal<InstructionResponse>(recipe.Instructions, persisted.Instructions);
        Assert.Equal(recipe.Id, Assert.Single(
            (await client.GetFromJsonAsync<RecipeListResponse>("/api/recipes"))!.Record).Id);
    }

    private static async Task AssertSafeServerError(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(text);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        Assert.DoesNotContain("sensitive database failure", text);
        Assert.DoesNotContain("Npgsql", text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FAIL_TEST_INSERT", text);
    }

    private sealed class RecipeApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:HomeChefDb", connectionString);
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
