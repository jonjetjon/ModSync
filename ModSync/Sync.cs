using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModSync.Utility;

namespace ModSync;

using SyncPathFileList = Dictionary<string, List<string>>;
using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

public static class Sync
{
    //takes in localmodfiles and remotemodfiles and finds files that exist only in remotemodfiles
    public static SyncPathFileList GetAddedFiles(List<SyncPath> syncPaths, SyncPathModFiles localModFiles, SyncPathModFiles remoteModFiles)
    {
        return syncPaths
            .Select(syncPath => new KeyValuePair<string, List<string>>(
                syncPath.path,
                remoteModFiles[syncPath.path]
                    .Where((kvp) => !kvp.Value.directory) //ignore directories
                    .Select((kvp) => kvp.Key)
                    .Except(localModFiles.TryGetValue(syncPath.path, out var modFiles) ? modFiles.Keys : new List<string>(), StringComparer.OrdinalIgnoreCase)
                    .ToList()
            ))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
    
    public static SyncPathFileList GetUpdatedFiles(
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousRemoteModFiles
    )
    {
        return syncPaths
            //iterate through the syncpaths
            .Select(syncPath =>
            {
                //if there are no local files in this syncpath return an empty list
                if (!localModFiles.TryGetValue(syncPath.path, out var localPathFiles))
                    return new KeyValuePair<string, List<string>>(syncPath.path, []);
                //query for files that exist in both the remote and local files for this syncpath
                var query = remoteModFiles[syncPath.path].Keys.Intersect(localPathFiles.Keys, StringComparer.OrdinalIgnoreCase);
                //enforced sync ignores this next check
                if (!syncPath.enforced)
                    //iterate through the list of files that are on both the remote and local files list
                    query = query.Where(file =>
                        //try to get previousremotemodfiles for the current sync path and store it to previouspathfiles
                        //if there is no previousremotemodfiles for the currentsync path then the current syncpath is included in the GetUpdatedFiles
                        !previousRemoteModFiles.TryGetValue(syncPath.path, out var previousPathFiles)
                        //if the previousremotemodfiles did contain the current sync path then we need to check if the current file was in previousremotemodfiles
                        //if it was in previousremotemodfiles store the previous version to modFile
                        //if it wasn't then it's a new file and the current files is included in GetUpdatedFiles
                        || !previousPathFiles.TryGetValue(file, out var modFile)
                        //if the previousremotemodfiles contained the current file then compare the previous hash to the current hash of the REMOTE mod file ONLY
                        //if those hashes don't match then the current files is included in GetUpdatedFiles
                        || remoteModFiles[syncPath.path][file].hash != modFile.hash
                    );
                //iterate through those results and find only files where the local file hash and the remote file hash don't match
                query = query.Where(file => remoteModFiles[syncPath.path][file].hash != localPathFiles[file].hash);
                //return the results as a list of KVPs
                return new KeyValuePair<string, List<string>>(syncPath.path, query.ToList());
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    
    //get a list of files that have been removed between previousremotemodfiles and the current state of remotemodfiles
    public static SyncPathFileList GetRemovedFiles(
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousRemoteModFiles
    )
    {
        return syncPaths
            //iterate through the syncpaths
            .Select(syncPath =>
            {
                //check if the syncpath exists locally, if it does store it to localPathFiles
                if (!localModFiles.TryGetValue(syncPath.path, out var localPathFiles))
                    //if it doesn't exist return an empty list since the file has already been deleted on the local side
                    return new KeyValuePair<string, List<string>>(syncPath.path, []);

                IEnumerable<string> query;
                if (syncPath.enforced)
                    //if the path is enforced then find files that exist in the local path that don't exist in in the remotepath
                    query = localPathFiles.Keys.Except(remoteModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase);
                else
                    //if the path is not enforced then we grab the previous remote mod files for the path and store them to previousPathFiles
                    query = !previousRemoteModFiles.TryGetValue(syncPath.path, out var previousPathFiles)
                    //if there is nothing in the previous path return an empty list since we can't have deleted anything if there was nothing there
                        ? []
                        //if there were files in the previous remote mod files for that path that were in previous remote files that still exist in local mod files
                        : previousPathFiles
                            .Keys.Intersect(localPathFiles.Keys, StringComparer.OrdinalIgnoreCase)
                            //filter out files that still exist on the remote(therefore finding the files that were removed on remote between previous and current that exist on local)
                            .Except(remoteModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase);
                //return a KVP with the files that should be removed
                return new KeyValuePair<string, List<string>>(syncPath.path, query.ToList());
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public static SyncPathFileList GetCreatedDirectories(
        string basePath,
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles
    )
    {
        return syncPaths
            .Select(syncPath =>
            {
                return new KeyValuePair<string, List<string>>(
                    syncPath.path,
                    remoteModFiles[syncPath.path]
                        .Where((kvp) => kvp.Value.directory)
                        .Select((kvp) => kvp.Key)
                        .Except(localModFiles[syncPath.path].Keys, StringComparer.OrdinalIgnoreCase)
                        .Where((dir) => !Directory.Exists(Path.Combine(basePath, dir)))
                        .ToList()
                );
            })
            .ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value);
    }

    private static List<string> GetFilesInDirectory(string basePath, string directory, List<Regex> exclusions)
    {
        if (File.Exists(directory))
            return [directory];

        if (!Directory.Exists(directory))
            return [];

        return Directory
            .GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where((file) => !IsExcluded(exclusions, file.Replace($"{basePath}\\", "")))
            .Concat(
                Directory
                    .GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where((subDir) => !IsExcluded(exclusions, subDir.Replace($"{basePath}\\", "")))
                    .SelectMany((subDir) => Directory.GetFileSystemEntries(subDir).Length == 0 ? [subDir] : GetFilesInDirectory(basePath, subDir, exclusions))
            )
            .ToList();
    }

    public static async Task<SyncPathModFiles> HashLocalFiles(
        string basePath,
        List<SyncPath> syncPaths,
        List<Regex> remoteExclusions,
        List<Regex> localExclusions
    )
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var processedFiles = new HashSet<string>();
        var limitOpenFiles = new SemaphoreSlim(1024);

        var results = new SyncPathModFiles();

        foreach (var syncPath in syncPaths)
        {
            var path = Path.Combine(basePath, syncPath.path);

            results[syncPath.path] = (
                await Task.WhenAll(
                    GetFilesInDirectory(basePath, path, [.. remoteExclusions, .. syncPath.enforced ? [] : localExclusions])
                        .Where((file) => !processedFiles.Contains(file))
                        .AsParallel()
                        .Select(
                            async (file) =>
                            {
                                await limitOpenFiles.WaitAsync();
                                var modFile = await CreateModFile(file);
                                limitOpenFiles.Release();

                                processedFiles.Add(file);
                                return new KeyValuePair<string, ModFile>(file.Replace($"{basePath}\\", ""), modFile);
                            }
                        )
                )
            ).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        watch.Stop();
        Plugin.Logger.LogInfo($"Corter-ModSync: Hashed {processedFiles.Count} files in {watch.Elapsed.TotalMilliseconds}ms");

        return results;
    }

    public static async Task<ModFile> CreateModFile(string file)
    {
        var hash = "";

        if (Directory.Exists(file))
            return new ModFile(hash, true);

        try
        {
            hash = await ImoHash.HashFile(file);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Corter-ModSync: Error hashing '{file}': {e.Message}");
            hash = "";
        }

        return new ModFile(hash);
    }

    //takes in the list of syncpaths, the localmodfiles, the remotemodfiles, the previoussync, and the basepath
    //searches for and outputs added files, updated files, removedfiles, and created directories
    //sort of a "do all the things" function
    public static void CompareModFiles(
        string basePath, 
        List<SyncPath> syncPaths,
        SyncPathModFiles localModFiles,
        SyncPathModFiles remoteModFiles,
        SyncPathModFiles previousSync,
        out SyncPathFileList addedFiles,
        out SyncPathFileList updatedFiles,
        out SyncPathFileList removedFiles,
        out SyncPathFileList createdDirectories
    )
    {
        addedFiles = GetAddedFiles(syncPaths, localModFiles, remoteModFiles);
        updatedFiles = GetUpdatedFiles(syncPaths, localModFiles, remoteModFiles, previousSync);
        removedFiles = GetRemovedFiles(syncPaths, localModFiles, remoteModFiles, previousSync);
        createdDirectories = GetCreatedDirectories(basePath, syncPaths, localModFiles, remoteModFiles);
    }

    public static bool IsExcluded(List<Regex> exclusions, string path)
    {
        return exclusions.Any(regex => regex.IsMatch(path.Replace(@"\", "/")));
    }
}
