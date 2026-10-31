using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HomeChef.Api.Controllers;


[ApiController]
[Route("api/db-health")]
public class DatabaseHealthController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public DatabaseHealthController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);

            await using var command =
                new NpgsqlCommand("SELECT 1", connection);

            var result =
                await command.ExecuteScalarAsync(cancellationToken);

            if (result is not int value || value != 1)
            {
                return StatusCode(503, new
                {
                    status = "unhealthy"
                });
            }

            return Ok(new
            {
                status = "connected"
            });
        }
        catch (NpgsqlException)
        {
            return StatusCode(503, new
            {
                status = "unavailable"
            });
        }
    }
}