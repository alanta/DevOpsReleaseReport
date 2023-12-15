using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using ReleaseReport;
using ReleaseReport.Shared;

namespace BlazorApp.Api
{
    public class LoadReleaseReport
    {
        private readonly IReleaseInformationService _releaseService;

        public LoadReleaseReport(IReleaseInformationService releaseService)
        {
            _releaseService = releaseService;
        }

        [FunctionName("ReleaseReport")]
        public async Task<ActionResult<Release[]>> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)]
            HttpRequest req,
            ClaimsPrincipal principal,
            ILogger log, CancellationToken token)
        {
            if (principal == null || !(principal.Identity?.IsAuthenticated ?? false))
            {
                log.LogWarning("Unauthorized request");
                return new ForbidResult("Request was not authenticated.");
            }
            return await _releaseService.GetPendingReleases("", token);
        }

        [FunctionName("Refresh")]
        public async Task<ActionResult<Release>> Refresh(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "Refresh/{id:int}")] HttpRequest req, [FromRoute]int id,
            ClaimsPrincipal principal, ILogger log, CancellationToken token)
        {
            if (principal == null || !(principal.Identity?.IsAuthenticated ?? false))
            {
                log.LogWarning("Unauthorized request");
                return new ForbidResult("Request was not authenticated.");
            }

            return await _releaseService.GetPendingRelease(id, token);
        }
    }
}
