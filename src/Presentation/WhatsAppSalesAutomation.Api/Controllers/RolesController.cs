using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/roles")]
[Authorize(Roles = AppRoles.SuperAdmin + "," + AppRoles.Admin)]
public class RolesController : ControllerBase
{
    private readonly IUserService _userService;

    public RolesController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<string>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _userService.GetAllRolesAsync(cancellationToken));
}
