using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using ReleaseReport;

[assembly: FunctionsStartup(typeof(BlazorApp.Api.Startup))]

namespace BlazorApp.Api
{
    public partial class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddReleaseServices(builder.GetContext().Configuration);
        }
    }
}
