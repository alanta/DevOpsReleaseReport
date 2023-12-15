using FakeItEasy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ReleaseReport.Tests;

public class When_loading_releases
{
    private readonly ITestOutputHelper _output;

    public When_loading_releases(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Run()
    {
        // Arrange
        var options = new AzureDevopsApiOptions
        {
            OrganizationUrl = "https://dev.azure.com/yourorg/",
            ProjectName = "YourProjectName",
            AccessToken = "Use your own PAT"
        };

        var service = new YamlPipelineReleaseInformationService(Options.Create(options), A.Fake<IMemoryCache>(), new NullLogger<YamlPipelineReleaseInformationService>());

        var result = await service.GetPendingReleases("", CancellationToken.None);

        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    }
    
}