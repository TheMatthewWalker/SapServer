using System.Net;
using System.Net.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;
using SapServer.Models;

namespace SapServer.Filters;

/// <summary>
/// Global action filter enforcing DataAnnotations validation ([Required],
/// [StringLength], etc.) on every [FromBody] model. Web API 2, unlike
/// ASP.NET Core's [ApiController], never does this automatically — model
/// binding populates ModelState with any violations, but nothing ever checks
/// it unless an action does so itself. Confirmed live: a real request with
/// ScrapReason="" (violating [StringLength(4, MinimumLength=4)]) reached SAP
/// and got a raw ABAP rejection ("Fill out all required entry fields")
/// instead of a clean 400 (endpoint-test-log-2026-08-27.md, ROUND 2 #9/11) —
/// this was a systemic gap affecting every DataAnnotations-decorated model in
/// the app, not just that one field, so it's fixed here globally rather than
/// with a one-off check in ProductionController.
/// </summary>
public sealed class ValidateModelAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(HttpActionContext actionContext)
    {
        if (actionContext.ModelState.IsValid)
            return;

        var message = string.Join(" ", actionContext.ModelState
            .SelectMany(kvp => kvp.Value.Errors.Select(e =>
                string.IsNullOrEmpty(e.ErrorMessage) ? e.Exception?.Message ?? "Invalid value." : e.ErrorMessage)));

        actionContext.Response = actionContext.Request.CreateResponse(
            HttpStatusCode.BadRequest,
            ApiResponse<object>.Fail("INVALID_DATA", message, null!));
    }
}
