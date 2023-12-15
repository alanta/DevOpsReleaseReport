using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Clients;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts;
using Microsoft.VisualStudio.Services.WebApi;
using Release = ReleaseReport.Shared.Release;
using WorkItem = ReleaseReport.Shared.WorkItem;

namespace ReleaseReport
{

    public class PipelineStatus
    {
        public string Name { get; set; }
        public int Id { get; set; }

        public DateTime? LastProductionDeployment { get; set; }
        public int? LastProductionDeploymentBuildId { get; set; }

        public DateTime? PendingDeploymentSince { get; set; }
        public int? PendingDeploymentBuildId { get; set; }
        public bool IsPending => PendingDeploymentBuildId.HasValue;
        public string? Version { get; set; }
        public string? Url { get; set; }
        public DateTime LastChangedDate { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Gets release information based on classic release pipelines
    /// </summary>
    public class YamlPipelineReleaseInformationService : IDisposable, IReleaseInformationService
    {
        private readonly AzureDevopsApiOptions _options;
        private readonly IMemoryCache _cache;
        private readonly ILogger _logger;
        
        private readonly Lazy<VssConnection> _connection;

        public YamlPipelineReleaseInformationService(IOptions<AzureDevopsApiOptions> options, IMemoryCache cache, ILogger<YamlPipelineReleaseInformationService> logger)
        {
            _options = options.Value;
            _cache = cache;
            _logger = logger;
            _connection = new Lazy<VssConnection>(() => new VssConnection(new Uri(_options.OrganizationUrl),
                new VssBasicCredential(string.Empty, _options.AccessToken)));
        }

        public async Task<Release[]> GetPendingReleases(string environment, CancellationToken cancellationToken)
        {
            var pendingReleases = (await GetPipelineStatus()).Where(s => s.IsPending).ToArray();

            var data = new List<Release>();

            var wiClient = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>();
            foreach (var release in pendingReleases)
            {
                var releaseData = await ProcessRelease( wiClient, release );

                if (releaseData != null)
                {
                    data.Add(releaseData);
                }
            }

            return data.OrderBy(a => a.Name).ToArray();
        }

        public async Task<Release?> GetPendingRelease(int id, CancellationToken token)
        {
            var builds = await _connection.Value.GetClientAsync<BuildHttpClient>(token);

            var definition = await builds.GetDefinitionAsync(
                project: _options.ProjectName,
                id, 
                includeLatestBuilds: true, 
                cancellationToken: token);

            if (definition == null)
            {
                return null;

            }

            var status = await GetDefinitionStatus(definition);

            var wiClient = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(token);

            return await ProcessRelease( wiClient, status );
        }

        public async Task<List<PipelineStatus>> GetPipelineStatus()
        {
            
            var builds = await _connection.Value.GetClientAsync<Microsoft.TeamFoundation.Build.WebApi.BuildHttpClient>();

            var pendingDeployments = new List<PipelineStatus>();

            var allDefinitions = await builds.GetDefinitionsAsync2(
            project: _options.ProjectName,
            processType: 2 /* yaml */,
            builtAfter: DateTime.Today.AddMonths(-3) // Only recent runs are interesting
            , includeLatestBuilds: true
            );

            foreach (var definition in allDefinitions)
            {
                var status = await GetDefinitionStatus(definition);

                if (status.PendingDeploymentBuildId != null)
                {
                    pendingDeployments.Add(status);
                }
            }

            return pendingDeployments;
        }

        private static string BuildDefinitionCacheKey(int id) => $"Definition{id}";

        public async Task<PipelineStatus> GetDefinitionStatus(BuildDefinitionReference definition)
        {
            if( _cache.TryGetValue<PipelineStatus>(BuildDefinitionCacheKey(definition.Id), out var cachedItem))
            {
                if (definition.LatestBuild.LastChangedDate <= cachedItem.LastChangedDate)
                {
                    return cachedItem;
                }
            }

            var builds = await _connection.Value.GetClientAsync<Microsoft.TeamFoundation.Build.WebApi.BuildHttpClient>();

            var status = new PipelineStatus
            {
                Id = definition.Id,
                Name = definition.Name,
                LastChangedDate = definition.LatestBuild?.LastChangedDate ?? DateTime.Now
            };

            // Find finished builds
            var finishedBuilds = await builds.GetBuildsAsync2(_options.ProjectName,
            reasonFilter: BuildReason.IndividualCI | BuildReason.Manual,
            statusFilter: BuildStatus.Completed,
            definitions: new []{ definition.Id },
            top: 10,
            queryOrder:BuildQueryOrder.QueueTimeDescending);

            Build? lastProductionDeployment = null;

            // Find the most recent completed run that deployed to production
            foreach (var run in finishedBuilds)
            {
                var timeline = await builds.GetBuildTimelineAsync(_options.ProjectName, run.Id); // TODO : prevent duplicates

                if (timeline == null)
                {
                    // skip
                    continue;
                }

                var check = timeline.Records.FirstOrDefault(r => r.RecordType == "Checkpoint.Approval" && r.State == TimelineRecordState.Completed);

                if (check is null)
                {
                    _logger.LogInformation("Pipeline has no approvals. Skipping it.");
                    return status;
                }

                lastProductionDeployment = run;

                _logger.LogInformation($"{definition.Name} {run.BuildNumber} deployed to prod on {check.FinishTime:f}");

                status.LastProductionDeployment = check.FinishTime;
                status.LastProductionDeploymentBuildId = run.Id;

                break;
            }

            // Find pending builds
            var pendingBuilds = await builds.GetBuildsAsync2(_options.ProjectName,
                reasonFilter: BuildReason.IndividualCI | BuildReason.Manual,
                statusFilter: BuildStatus.InProgress,
                definitions: new []{ definition.Id },
                top: 10,
                queryOrder:BuildQueryOrder.QueueTimeDescending
            );

            _logger.LogInformation("Found {0} pending runs", pendingBuilds.Count);

            // Get the timeline for the run so we can check for pending approvals
            // Query the timeline, look for records of type Checkpoint.Approval with state=inProgress
            // GET https://dev.azure.com/org/proj/_apis/build/builds/<id>/Timeline
            foreach (var run in pendingBuilds)
            {
                var timeline = await builds.GetBuildTimelineAsync(_options.ProjectName, run.Id);

                var check = timeline.Records.FirstOrDefault(r => r.RecordType == "Checkpoint.Approval" && r.State == TimelineRecordState.InProgress);

                if (check is null)
                    continue;

                if (lastProductionDeployment != null && run.StartTime < lastProductionDeployment.StartTime)
                {
                    break; // no pending deployments newer than the last
                }

                _logger.LogInformation($"{definition.Name} {run.BuildNumber} is pending since {check.StartTime:f}");
                status.PendingDeploymentBuildId = run.Id;
                status.PendingDeploymentSince = check.StartTime;
                status.Version = run.BuildNumber;
                status.Url = (run.Links.Links["web"] as ReferenceLink)?.Href;
                break;
            }
            
            _cache.Set(BuildDefinitionCacheKey(definition.Id), status, TimeSpan.FromMinutes(5));
            
            return status;
        }

        private async Task<Release?> ProcessRelease(WorkItemTrackingHttpClient wiClient, PipelineStatus releaseItem)
        {
            // current release
            var data = new Release
            {
                Id = releaseItem.Id,
                Name = releaseItem.Name,
                Version = releaseItem.Version!,
                Url =releaseItem.Url,
            };

            var buildClient = await _connection.Value.GetClientAsync<BuildHttpClient>();
          
            try
            {
                var buildRunId = (int)releaseItem.PendingDeploymentBuildId!;
                List<ResourceRef> workItemList;
                if (releaseItem.LastProductionDeploymentBuildId.HasValue)
                {
                    // Load workitems pushed to main branch since last production release
                    var previousBuildId = (int)releaseItem.LastProductionDeploymentBuildId;

                    // https://docs.microsoft.com/en-us/rest/api/azure/devops/build/builds/get%20work%20items%20between%20builds?view=azure-devops-rest-6.0
                    workItemList = await buildClient
                        .GetWorkItemsBetweenBuildsAsync(project: _options.ProjectName, previousBuildId, buildRunId);
                }
                else
                {
                    // No previous release found, use the work items from the latest build
                    // ⚠ this means we may be missing some work items if there are more commits
                    workItemList = await buildClient
                        .GetBuildWorkItemsRefsAsync(project: _options.ProjectName, buildRunId);
                }

                var workItems = await GetWorkItems( wiClient, workItemList.Select(wi => Convert.ToInt32(wi.Id)).ToArray() );

                var flatList = new List<WorkItem>();
                foreach (var wi in workItems)
                {
                    var dataItem = MapWorkItemToPBI(wi);
                    flatList.Add(dataItem);
                }
                
                // convert to a hierarchical list so the tasks are under the PBI
                
                // First Load missing PBIs
                var missingParentWorkItemIds = flatList
                    .Where(wi => wi.ParentId.HasValue).Select(wi => wi.ParentId.Value).Distinct()
                    .Except( flatList.Select( wi => wi.Id ) )
                    .ToArray();

                if (missingParentWorkItemIds.Any())
                {
                    var missingParentWorkItems = await GetWorkItems(wiClient, missingParentWorkItemIds);

                    flatList.AddRange(missingParentWorkItems.Select(MapWorkItem));
                }

                foreach (var wi in flatList.Where( wi => wi.ParentId.HasValue ).ToArray())
                {
                    var parent = flatList.First(parentWi => parentWi.Id == wi.ParentId);
                    parent.Tasks ??= new List<WorkItem>();
                    parent.Tasks.Add(wi);
                    flatList.Remove(wi);
                }

                data.WorkItems.AddRange(flatList);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load work items");
            }
            

            return data;
        }

        private async Task<Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Release?> FindPreviousRelease(ReleaseHttpClient2 releaseClient, ReleaseApproval releaseItem, Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Release release)
        {
            var targetEnvironmentId = release.Environments
                .FirstOrDefault(env => env.Id == releaseItem.ReleaseEnvironmentReference.Id)?.DefinitionEnvironmentId;
            
            var previousRelease = (await releaseClient.GetReleasesAsync2(_options.ProjectName,
                statusFilter: ReleaseStatus.Active,
                definitionId: releaseItem.ReleaseDefinitionReference.Id,
                definitionEnvironmentId: targetEnvironmentId,
                queryOrder: ReleaseQueryOrder.Descending,
                top: 1,
                environmentStatusFilter: 4, // Succeeded
                expand: ReleaseExpands.Artifacts
            )).FirstOrDefault();
            return previousRelease;
        }

        private WorkItem MapWorkItemToPBI(Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem wi)
        {
            var item = MapWorkItem(wi);

            if (item.Type == "Task")
            {
                var parent = wi.Relations.FirstOrDefault(rel => rel.Rel == "System.LinkTypes.Hierarchy-Reverse");
                if (parent != null)
                {
                    if (TryParseIdFromRelation(parent, out var parentId))
                    {
                        item.ParentId = parentId;
                    }
                }
            }

            return item;
        }

        private static WorkItem MapWorkItem(Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem wi)
        {
            var item = new WorkItem
            {
                Id = (int) wi.Id!,
                Url = (wi.Links.Links["html"] as ReferenceLink)?.Href,
                Type = wi.Fields["System.WorkItemType"]?.ToString() ?? "Unknown",
                Description = wi.Fields["System.Title"].ToString() ?? "-",
                Status = wi.Fields["System.State"].ToString() ?? "Unknown"
            };
            
            if (item.Type == "Product Backlog Item")
            {
                item.Type = "PBI";
            }
            
            return item;
        }

        public static bool TryParseIdFromRelation(WorkItemRelation relation, out int id)
        {
            var lastIndex = relation.Url.LastIndexOf("/", StringComparison.Ordinal);
            return Int32.TryParse(relation.Url.Substring(lastIndex + 1), out id);
        }


        public async Task<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem[]> GetWorkItems(WorkItemTrackingHttpClient wiClient, int[] workItems)
        {
            var (cachedItems, missingItems) = TryLoadItemsFromCache(workItems);
            var result = new List<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>(cachedItems);

            if (missingItems.Length > 0)
            {
                var loadedWorkItems =
                    (await wiClient.GetWorkItemsAsync(_options.ProjectName, missingItems, expand: WorkItemExpand.All))
                    .ToArray();
                foreach (var loadedItem in loadedWorkItems)
                {
                    using (var entry = _cache.CreateEntry(WorkItemCacheKey(loadedItem.Id!.Value)))
                    {
                        entry.Value = loadedItem;
                        entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                    }
                }

                result.AddRange(loadedWorkItems);
            }

            return result.ToArray();
        }

        private static string WorkItemCacheKey(int id) => $"WorkItem{id}";

        private (Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem[] cachedItems, int[] missingItems) TryLoadItemsFromCache(int[] workItems)
        {
            var missingItems = new List<int>();
            var cachedItems = new List<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>();

            foreach (var item in workItems)
            {
                var cachedItem = _cache.Get<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?>(WorkItemCacheKey(item));
                if (cachedItem != null)
                {
                    cachedItems.Add(cachedItem);
                }
                else
                {
                    missingItems.Add(item);
                }
            }

            return (cachedItems.ToArray(), missingItems.ToArray());
        }


        public void Dispose()
        {
            _connection.Value?.Dispose();
        }
    }
}
