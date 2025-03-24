using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModSync.Utility;
using SPT.Common.Http;
using SPT.Common.Utils;

namespace ModSync;

using SyncPathModFiles = Dictionary<string, Dictionary<string, ModFile>>;

public class Server(Version pluginVersion)
{
    //asynchronously gets json data as a string from the path taken in as a string
    private async Task<string> GetJson(string path)
    {
        try
        {
            //create a new httpclient
            using var client = new HttpClient();
            //create a custom header that contains the plugin version
            client.DefaultRequestHeaders.Add("modsync-version", pluginVersion.ToString());
            //set a 5 minute timeout for the request
            client.Timeout = TimeSpan.FromMinutes(5);
            //perform an asynchronous get request to the server using the client instantiated above
            var json = await client.GetStringAsync($"{RequestHandler.Host}{path}");
            return json;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"There was an error performing request.\n{e.Message}\n{e.StackTrace}");
            throw;
        }
    }
    //asynchronously downloads a file from the server using a semaphore limiter, a string for the filename, and a string for the download directory path
    public async Task DownloadFile(string file, string downloadDir, SemaphoreSlim limiter, CancellationToken cancellationToken)
    {
        //exit the method early if cancellation gets requested
        if (cancellationToken.IsCancellationRequested)
            return;

        //filepath is the downloaddir string concatenated with the file string
        var downloadPath = Path.Combine(downloadDir, file);
        VFS.CreateDirectory(downloadPath.GetDirectory());

        var retryCount = 0;

        //acquire a semaphore slot (to allow for concurrent downloads)
        await limiter.WaitAsync();
        //this while loop allows it to cancel the download file mid download
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                //create a new httpclient
                using var client = new HttpClient();

                //if retrycount is not 0 the timeout period is set to 10 minutes
                if (retryCount > 0)
                    client.Timeout = TimeSpan.FromMinutes(10);

                //start an asynchronous stream downloading from the server
                using var responseStream = await client.GetStreamAsync($"{RequestHandler.Host}/modsync/fetch/{file}");
                //start a filestream to write the content from responseStream
                using var fileStream = new FileStream(downloadPath, FileMode.Create);

                //copy the file content from the response stream to the filestream
                await responseStream.CopyToAsync(fileStream);
                //we are done downloading so release the semaphore slot
                limiter.Release();
                return;
            }
            catch (Exception e)
            {
                //check for cancellation request
                if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                //if there is an exception caught that isn't a cancellation request and we are below the max retry count(5) log it and retry after 500ms
                if (retryCount < 5)
                {
                    Plugin.Logger.LogError($"Failed to download '{file}'. Retrying ({retryCount + 1}/5)...");
                    Plugin.Logger.LogDebug(e);
                    await Task.Delay(500, cancellationToken);
                    retryCount++;
                    continue;
                }
                //if there is an exception caught and we are above the max rety count exit out
                Plugin.Logger.LogError($"Failed to download '{file}'. Exiting...");
                Plugin.Logger.LogError(e);
                throw;
            }
        }
    }

    public async Task<string> GetModSyncVersion()
    {
        return Json.Deserialize<string>(await GetJson("/modsync/version"));
    }

    public async Task<List<SyncPath>> GetModSyncPaths()
    {
        return Json.Deserialize<List<SyncPath>>(await GetJson("/modsync/paths"));
    }

    public async Task<List<string>> GetModSyncExclusions()
    {
        return Json.Deserialize<List<string>>(await GetJson("/modsync/exclusions"));
    }

    //asynchronously gets a SyncPathModFiles of remote file hashes from a list of SyncPath objects called syncpaths
    public async Task<SyncPathModFiles> GetRemoteModFileHashes(List<SyncPath> syncPaths)
    {
        return Json.Deserialize<SyncPathModFiles>(
                await GetJson($"/modsync/hashes?path={string.Join("&path=", syncPaths.Select(path => Uri.EscapeUriString(path.path.Replace(@"\", "/"))))}")
            )
            //convert the resulting deserialized data to a dictionary so that it can be returned as a SyncPathModFiles
            .ToDictionary(
                item => item.Key,
                item => item.Value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase
            );
    }
}
