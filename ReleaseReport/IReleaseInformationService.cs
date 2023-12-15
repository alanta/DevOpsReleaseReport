using System.Threading;
using System.Threading.Tasks;
using ReleaseReport.Shared;

namespace ReleaseReport;

public interface IReleaseInformationService
{
    Task<Release[]> GetPendingReleases(string environment, CancellationToken cancellationToken);
    Task<Release?> GetPendingRelease(int id, CancellationToken token);
}