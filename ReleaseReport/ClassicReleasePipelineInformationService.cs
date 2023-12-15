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
    /// <summary>
    /// Gets release information based on classic release pipelines
    /// </summary>
    public class ClassicReleasePipelineInformationService : IDisposable, IReleaseInformationService
    {
        private readonly AzureDevopsApiOptions _options;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ClassicReleasePipelineInformationService> _logger;
        
        private readonly Lazy<VssConnection> _connection;

        public ClassicReleasePipelineInformationService(IOptions<AzureDevopsApiOptions> options, IMemoryCache cache, ILogger<ClassicReleasePipelineInformationService> logger)
        {
            _options = options.Value;
            _cache = cache;
            _logger = logger;
            _connection = new Lazy<VssConnection>(() => new VssConnection(new Uri(_options.OrganizationUrl),
                new VssBasicCredential(string.Empty, _options.AccessToken)));
        }

        public async Task<Release[]> GetPendingReleases(string environment, CancellationToken cancellationToken)
        {
            var releaseClient = await _connection.Value.GetClientAsync<ReleaseHttpClient2>();
            var wiClient = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>();

            var pendingReleases = (await ListAllPendingApprovals(releaseClient))
                // only releases to target environment
                .Where( r => r.ReleaseEnvironmentReference.Name.Contains(environment, StringComparison.OrdinalIgnoreCase))
                .Distinct(new ReleaseApprovalComparer())
                .ToArray();

            var data = new List<Release>();
            
            foreach (var release in pendingReleases)
            {
                var releaseData = await ProcessRelease( releaseClient, wiClient, release );

                if (releaseData != null)
                {
                    data.Add(releaseData);
                }
            }

            return data.ToArray();
        }

        public async Task<Release?> GetPendingRelease(int id, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        private class ReleaseApprovalComparer : IEqualityComparer<ReleaseApproval>
        {
            public bool Equals(ReleaseApproval? x, ReleaseApproval? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.GetType() != y.GetType()) return false;
                return Equals(x.ReleaseReference?.Id, y.ReleaseReference?.Id);
            }

            public int GetHashCode(ReleaseApproval obj)
            {
                return obj?.ReleaseReference?.Id ?? 0;
            }
        }
        
        private async Task<IEnumerable<ReleaseApproval>> ListAllPendingApprovals(ReleaseHttpClient2 releaseClient)
        {
            
            List<ReleaseApproval> releaseApprovals = new List<ReleaseApproval>();

            int continuationToken = 0;
            bool parseResult;
            do
            {
                IPagedCollection<ReleaseApproval> releaseApprovalsPage = await releaseClient.GetApprovalsAsync2(project: _options.ProjectName, continuationToken: continuationToken);

                releaseApprovals.AddRange(releaseApprovalsPage);

                parseResult = int.TryParse(releaseApprovalsPage.ContinuationToken, out var parsedContinuationToken);
                if (parseResult)
                {
                    continuationToken = parsedContinuationToken;
                }
            } while ((continuationToken != 0) && parseResult);

            return releaseApprovals;
        }

        private async Task<Release?> ProcessRelease(ReleaseHttpClient2 releaseClient, WorkItemTrackingHttpClient wiClient, ReleaseApproval releaseItem)
        {
            // current release
            var release = await releaseClient.GetReleaseAsync(project: _options.ProjectName, releaseId: releaseItem.ReleaseReference.Id);
            var previousRelease = await FindPreviousRelease(releaseClient, releaseItem, release);

            var data = new Release
            {
                Id = release.Id,
                Name = releaseItem.ReleaseDefinitionReference.Name,
                Version = release.Name,
                Url = (release.Links.Links["web"] as ReferenceLink)?.Href,
            };

            var buildClient = await _connection.Value.GetClientAsync<BuildHttpClient>();

            foreach (var deploymentArtifact in release.Artifacts)
            {
                try
                {
                    var buildRunId = Convert.ToInt32(deploymentArtifact.DefinitionReference["version"].Id);
                    var previousBuildId = Convert.ToInt32((
                            previousRelease!.Artifacts.FirstOrDefault(a => a.Alias.Equals(deploymentArtifact.Alias, StringComparison.OrdinalIgnoreCase)) ?? previousRelease!.Artifacts[0]
                        ).DefinitionReference["version"].Id);
                    
                    // https://docs.microsoft.com/en-us/rest/api/azure/devops/build/builds/get%20work%20items%20between%20builds?view=azure-devops-rest-6.0
                    var workItemList = await buildClient
                        .GetWorkItemsBetweenBuildsAsync(project: _options.ProjectName, previousBuildId, buildRunId);

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
                    _logger.LogWarning(ex, "Failed to load work items for {artifact}", deploymentArtifact.Alias);
                }
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
