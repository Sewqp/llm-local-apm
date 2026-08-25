using CollectionServer.Dtos;
using CollectionServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace CollectionServer.Controllers;

[ApiController]
[Route("api/servers")]
public class ServersController(IManagedServerService service) : ControllerBase
{
    // 등록된 대상 서버 목록 + 실행 상태
    [HttpGet]
    public IActionResult GetAll() => Ok(service.GetAll());

    // 새 서버 등록 (재시작 후에도 유지됨)
    [HttpPost]
    public IActionResult Add([FromBody] CreateManagedServerRequest request)
    {
        var (success, error) = service.AddServer(request);
        return success ? Ok() : BadRequest(new { error });
    }

    [HttpDelete("{name}")]
    public IActionResult Remove(string name)
    {
        var (success, error) = service.RemoveServer(name);
        return success ? Ok() : BadRequest(new { error });
    }

    [HttpPost("{name}/start")]
    public async Task<IActionResult> Start(string name)
    {
        var started = await service.StartAsync(name);
        return started
            ? Ok()
            : NotFound(new { error = $"'{name}' 서버를 찾을 수 없거나 시작하지 못했습니다." });
    }

    [HttpPost("{name}/stop")]
    public IActionResult Stop(string name)
    {
        var stopped = service.Stop(name);
        return stopped
            ? Ok()
            : NotFound(new { error = $"'{name}' 서버가 실행 중이 아닙니다." });
    }
}
