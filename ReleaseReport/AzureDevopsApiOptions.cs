namespace ReleaseReport;

public class AzureDevopsApiOptions
{
    public const string ConfigurationSection = "AzureDevOps";
    public string OrganizationUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
}