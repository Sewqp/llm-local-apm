using Microsoft.AspNetCore.Mvc;

namespace TargetServer.Controllers;

/// <summary>
/// 데모용 엔드포인트. 실제 프로젝트에서는 "기존 보유 C# 서버"의 컨트롤러를 그대로 사용하고,
/// 계측 미들웨어만 추가하면 된다. 여기서는 파이프라인 시연을 위해 임의의 지연/오류를 섞는다.
/// </summary>
[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private static readonly Random Rng = new();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        await Task.Delay(Rng.Next(20, 60));
        return Ok(new[]
        {
            new { id = 1, name = "Mechanical Keyboard", price = 89000 },
            new { id = 2, name = "Wireless Mouse", price = 35000 },
            new { id = 3, name = "27-inch Monitor", price = 320000 }
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        // 5% 확률로 DB 락 경합 등을 흉내낸 응답 지연 스파이크 발생 (이상탐지 데모용)
        var isSlowSpike = Rng.NextDouble() < 0.05;
        var delayMs = isSlowSpike ? Rng.Next(800, 1600) : Rng.Next(15, 50);
        await Task.Delay(delayMs);

        return Ok(new { id, name = $"Product {id}", price = 10000 * id });
    }
}
