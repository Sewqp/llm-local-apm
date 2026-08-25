using Microsoft.AspNetCore.Mvc;

namespace TargetServer.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private static readonly Random Rng = new();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        await Task.Delay(Rng.Next(30, 80));
        return Ok(new[] { new { id = 101, status = "Shipped" }, new { id = 102, status = "Processing" } });
    }

    [HttpPost]
    public async Task<IActionResult> Create()
    {
        await Task.Delay(Rng.Next(40, 100));

        // 8% 확률로 결제 모듈 장애 등을 흉내낸 500 오류 발생 (이상탐지 데모용)
        if (Rng.NextDouble() < 0.08)
        {
            return StatusCode(500, new { error = "Payment gateway timeout" });
        }

        return Created(string.Empty, new { id = Rng.Next(1000, 9999), status = "Created" });
    }
}
