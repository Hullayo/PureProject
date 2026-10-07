using System.Security.Cryptography;
using System.Text;
using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed record ManualSyncResult(List<Project> Projects, AppSettings Settings,
    int Uploaded, int Downloaded, List<SyncConflict> Conflicts);

/// <summary>Conservative three-way reconciliation. Unknown baselines and concurrent edits produce conflicts.</summary>
public sealed class ManualSyncService(RestSyncClient client)
{
    public async Task<ManualSyncResult> SyncAsync(IEnumerable<Project> projects, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var working = projects.Select(PmSerializer.Clone).ToList();
        var server = RestSyncClient.ValidateServerUri(settings.SyncServerUrl).AbsoluteUri;
        var sameServer = string.Equals(server, settings.SyncStateServerUrl, StringComparison.Ordinal);
        var revisions = sameServer ? new Dictionary<string, long>(settings.SyncRevisions, StringComparer.Ordinal) : [];
        var hashes = sameServer ? new Dictionary<string, string>(settings.SyncedProjectHashes, StringComparer.Ordinal) : [];
        var nextSettings = settings with { SyncStateServerUrl = server, SyncRevisions = revisions, SyncedProjectHashes = hashes };
        var conflicts = new List<SyncConflict>();
        var uploaded = 0;
        var downloaded = 0;
        var index = await client.ListAsync(settings, cancellationToken).ConfigureAwait(false);
        var remote = index.Projects.Where(item => !item.Deleted).ToDictionary(item => item.Id, StringComparer.Ordinal);

        for (var position = 0; position < working.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = working[position];
            var localHash = Hash(local);
            var baseline = revisions.GetValueOrDefault(local.Id);
            var knownHash = hashes.GetValueOrDefault(local.Id);
            if (!remote.TryGetValue(local.Id, out var remoteMeta))
            {
                if (baseline > 0)
                {
                    conflicts.Add(new(local.Id, "server_project_missing", baseline, Deleted: true));
                    continue;
                }
                var push = await client.PushAsync(settings, local, 0, cancellationToken).ConfigureAwait(false);
                if (push.Conflict is not null) { conflicts.Add(push.Conflict); continue; }
                revisions[local.Id] = push.Rev!.Value;
                hashes[local.Id] = localHash;
                uploaded++;
                continue;
            }

            if (baseline > 0 && remoteMeta.Rev == baseline)
            {
                if (knownHash == localHash) continue;
                var push = await client.PushAsync(settings, local, baseline, cancellationToken).ConfigureAwait(false);
                if (push.Conflict is not null) { conflicts.Add(push.Conflict); continue; }
                revisions[local.Id] = push.Rev!.Value;
                hashes[local.Id] = localHash;
                uploaded++;
                continue;
            }

            if (baseline > 0 && remoteMeta.Rev < baseline)
            {
                conflicts.Add(new(local.Id, "server_revision_rewound", remoteMeta.Rev));
                continue;
            }

            var pull = await client.PullAsync(settings, local.Id, cancellationToken).ConfigureAwait(false);
            var remoteHash = Hash(pull.Project);
            if (remoteHash == localHash)
            {
                revisions[local.Id] = pull.Rev;
                hashes[local.Id] = localHash;
            }
            else if (baseline > 0 && knownHash == localHash)
            {
                working[position] = pull.Project;
                revisions[local.Id] = pull.Rev;
                hashes[local.Id] = remoteHash;
                downloaded++;
            }
            else
            {
                conflicts.Add(new(local.Id, baseline == 0 ? "unknown_baseline" : "both_modified", pull.Rev,
                    PmSerializer.ExportPm(pull.Project)));
            }
        }

        var localIds = working.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in remote.Values.Where(item => !localIds.Contains(item.Id)))
        {
            // A project removed locally after a prior sync must not silently reappear.
            if (revisions.ContainsKey(item.Id))
            {
                conflicts.Add(new(item.Id, "local_project_missing", item.Rev));
                continue;
            }
            var pull = await client.PullAsync(settings, item.Id, cancellationToken).ConfigureAwait(false);
            working.Add(pull.Project);
            revisions[item.Id] = pull.Rev;
            hashes[item.Id] = Hash(pull.Project);
            downloaded++;
        }

        return new(working, nextSettings, uploaded, downloaded, conflicts);
    }

    public static string Hash(Project project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PmSerializer.ExportPm(project))));
}
