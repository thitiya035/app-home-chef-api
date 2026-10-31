# HomeChef API

ASP.NET Core (.NET 10), EF Core and PostgreSQL.

## Run locally

Set `ConnectionStrings:HomeChefDb` with .NET User Secrets or the
`ConnectionStrings__HomeChefDb` environment variable. Keep passwords out of
tracked configuration files. `UserSecretsId` in the project is an identifier,
not a credential.

The database must already contain `public.recipes`, `public.recipe_ingredients`
and `public.recipe_instructions` matching `HomeChefDbContext`.

**Before running/deploying this version against an existing database**, run
`Data/Scripts/001_add_recipe_deleted_at.sql` as the table owner (for example,
`postgres`), not the application login. For example, with PostgreSQL's
`psql` (enter the owner password when prompted):

```powershell
psql --host=localhost --username=postgres --dbname=homechef_db --password --set=ON_ERROR_STOP=1 --file=Data/Scripts/001_add_recipe_deleted_at.sql
dotnet run --launch-profile http
```

The script adds nullable `deleted_at timestamptz`; existing recipes stay active.
It can be run again without changing existing values. This project does not
apply schema changes automatically at startup and does not use EF migrations.

If the runtime role `homechef_app` does not yet have table/sequence access, run
`Data/Scripts/002_grant_recipe_app_permissions.sql` as the owner in the same
database. It grants only recipe table operations and sequence usage, without
making the runtime role an owner or superuser. Adjust the role name if your
connection uses a different login. In pgAdmin, select `homechef_db`, open Query
Tool as `postgres`, and run both scripts there. `SELECT current_database();`
confirms the selected database. DB health only runs `SELECT 1`, so a healthy
connection alone does not prove that recipe tables or their permissions are ready.

Swagger UI: `http://localhost:5072/swagger` (Development only).

## Recipe endpoints

| Method | Route | Success | Missing data |
|---|---|---|---|
| POST | `/api/recipes` | `201` + RecipeResponse | — |
| GET | `/api/recipes?page=1&itempage=10` | `200` + RecipeListResponse | `200` with empty `record` |
| GET | `/api/recipes/{id}` | `200` + RecipeResponse | `404` |
| PUT | `/api/recipes/{id}` | `200` + updated RecipeResponse | `404` |
| DELETE | `/api/recipes/{id}` | `204`, no body | `404`, including repeated deletion |

IDs are GUIDs. GET lists active recipes ordered by ID, with ingredients and
instructions ordered by their positions. Use query parameters `page` (1-based,
default 1) and `itempage` (default 10, allowed 1–100). Invalid values return `400`
Validation Problem Details. Search is not included.

The list response is an object rather than the previous array:

```json
{
  "totalRecord": 0,
  "record": []
}
```

`totalRecord` counts all active recipes before pagination. `record` contains
the requested page of complete RecipeResponse objects. No active recipes or
a page past the end returns `200` with an empty array, preserving the total.
Existing clients must read `response.record` instead of treating the response
as an array. Single-recipe responses are unchanged. Pagination needs no schema
changes beyond the existing soft-delete setup.

PUT is a full replacement using the POST request fields. Required fields must
be present; nullable optional fields clear existing values. Ingredients and
instructions replace the complete old lists, with positions/step numbers
assigned starting at 1 in array order. POST and PUT share validation (`400` for
invalid requests), trimming and quantity rounding. Replacement uses a single
transaction, so a failed write preserves the original recipe and its children.

DELETE sets the UTC deletion timestamp without deleting the recipe or its
children. Deleted recipes are hidden from both GET endpoints and cannot be
updated or deleted again. `deletedAt` is internal and is not in RecipeResponse.
There is no restore endpoint. Unexpected server errors return `500` Problem
Details without database exception details.

See `HomeChef.Api.http` for sample requests.

## Integration tests

Tests use xUnit, the ASP.NET Core test host and **real PostgreSQL**. Point
`HOMECHEF_TEST_CONNECTION_STRING` at a dedicated test server with a role that
can create databases. Each test creates a unique `homechef_test_<guid>` database
and drops only that database afterwards. Tests never use the app connection
string. Missing test configuration fails explicitly instead of silently skipping.

Example for a separately provisioned local test server (replace the placeholder
password if the server requires authentication):

```powershell
$env:HOMECHEF_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55432;Database=postgres;Username=homechef_test;Password=YOUR_TEST_PASSWORD'
dotnet build --artifacts-path artifacts/build
dotnet test tests/HomeChef.Api.Tests/HomeChef.Api.Tests.csproj --artifacts-path artifacts/test-build
```

Separate artifact paths allow verification while a development app holds the
normal build output open. Tests cover list/read behavior, full updates and
validation, soft-delete retention, rollback on database rejection, safe `500`
responses, OpenAPI metadata and repeated execution of the schema upgrade script.
Pagination tests cover defaults, page boundaries, totals excluding soft-deleted
recipes, ordered children, invalid query values and overflow-safe empty pages.
