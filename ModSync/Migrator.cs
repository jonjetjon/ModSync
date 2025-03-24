using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModSync.Utility;
using Newtonsoft.Json.Linq;
using SPT.Common.Utils;

namespace ModSync;

//Migrator is responsible for handling version migrations of ModSync data.
//it detects previous versions, cleans up old files, and updates the structure of sync data when needed.
public class Migrator(string baseDir)
{
    private string MODSYNC_DIR => Path.Combine(baseDir, "ModSync_Data");
    private string VERSION_PATH => Path.Combine(MODSYNC_DIR, "Version.txt");
    private string PREVIOUS_SYNC_PATH => Path.Combine(MODSYNC_DIR, "PreviousSync.json");
    private string MODSYNC_PATH => Path.Combine(baseDir, ".modsync");

    private List<string> CLEANUP_FILES => [MODSYNC_PATH, Path.Combine(baseDir, @"BepInEx\patchers\Corter-ModSync-Patcher.dll")];

    //use stored version information to figure out what version we are migrating from
    private Version DetectPreviousVersion()
    {
        try
        {
            //if /modsync_data exists and version.txt is in it parse that and return the path from that
            if (Directory.Exists(MODSYNC_DIR) && File.Exists(VERSION_PATH))
                return Version.Parse(File.ReadAllText(VERSION_PATH));

            //if that check failed we need to check for the old version of modsync by searching for the file .modsync
            if (File.Exists(MODSYNC_PATH))
            {
                var persist = JObject.Parse(File.ReadAllText(MODSYNC_PATH));
                if (persist.ContainsKey("version") && persist["version"] != null)
                {
                    return persist["version"].Value<int>() switch
                    {
                        //version 7 maps to 0.7.0
                        7 => Version.Parse("0.7.0"),
                        //unknown version maps to 0.0.0 by default
                        _ => Version.Parse("0.0.0")
                    };
                }
            }
        }
        catch (Exception e)
        {
            //if we failed log it and return 0.0.0 as a default
            Plugin.Logger.LogWarning("Failed to identify previous version. Cleaning up and attempting to continue.");
            Plugin.Logger.LogWarning(e);
        }

        return Version.Parse("0.0.0");
    }

    //cleans up the old modsync data and initializes a fresh structure
    private void Cleanup(Version pluginVersion)
    {
        //delete the modsync_data folder if it exists
        if (Directory.Exists(MODSYNC_DIR))
            Directory.Delete(MODSYNC_DIR, true);
        //delete the .modsync file and the old version of the dll if they exist
        foreach (var file in CLEANUP_FILES.Where(File.Exists))
            File.Delete(file);
        //create a new modsync_data folder
        Directory.CreateDirectory(MODSYNC_DIR);
        //make a new version.txt with the current version number
        File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
    }

    //attempts to migrate from an older version to the current one
    public void TryMigrate(Version pluginVersion, List<SyncPath> syncPaths)
    {
        var oldVersion = DetectPreviousVersion();

        //if it is an uknown version just delete the files and return
        if (oldVersion == Version.Parse("0.0.0"))
        {
            Cleanup(pluginVersion);
            return;
        }

        //if the old version is not uknown but is before 0.8.0
        if (oldVersion < Version.Parse("0.8.0"))
        {
            var persist = JObject.Parse(File.ReadAllText(MODSYNC_PATH));

            //if there's no previous sync data just delete the files and return
            if (!persist.ContainsKey("previousSync") || persist["previousSync"] == null)
            {
                Cleanup(pluginVersion);
                return;
            }
            
            //there is previous sync data store that to oldPreviousSync
            var oldPreviousSync = (JObject)persist["previousSync"];
            var newPreviousSync = new JObject();

            //add each syncpath from the old previous sync data to the new one
            foreach (var syncPath in syncPaths)
                newPreviousSync.Add(syncPath.path, new JObject());

            //take the old previous sync data and put it into the new data structure for syncPaths
            foreach (var property in oldPreviousSync.Properties())
            {
                var syncPath = syncPaths.Find(s => property.Name.StartsWith($"{s.path}\\"));
                if (syncPath == null)
                {
                    Plugin.Logger.LogWarning($"Could not migrate previous sync of '{property.Name}'. Does not match any current sync paths.");
                    continue;
                }

                var modFile = (JObject)property.Value;
                if (!modFile.ContainsKey("crc"))
                {
                    Plugin.Logger.LogWarning($"Could not migrate previous sync of '{property.Name}'. Does not contain crc.");
                    continue;
                }

                (newPreviousSync.Property(syncPath.path)!.Value as JObject)!.Add(property.Name, new JObject() { ["crc"] = modFile["crc"]!.Value<uint>(), });
            }

            //check if modsync_data folder exists, if it doesn't make it
            if (!Directory.Exists(MODSYNC_DIR))
                Directory.CreateDirectory(MODSYNC_DIR);

            //create the new previoussync file
            File.WriteAllText(PREVIOUS_SYNC_PATH, Json.Serialize(newPreviousSync));
            //make the new version.txt in the modsync_data folder
            File.WriteAllText(VERSION_PATH, pluginVersion.ToString());

            //manually delete the files, we can't call the cleanup function because it will delete the folder we just made and stored things in
            foreach (var file in CLEANUP_FILES.Where(File.Exists))
                File.Delete(file);
        }
        //if old version was between 0.8.0 and 0.9.0
        if (oldVersion < Version.Parse("0.9.0"))
        {
            var previousSync = JObject.Parse(File.ReadAllText(PREVIOUS_SYNC_PATH));

            //we need to go through previousSync and remove deprecated fields and add new ones
            foreach (var property in previousSync.Properties())
            {
                foreach (var file in (property.Value as JObject)!.Properties())
                {
                    var fileObject = (file.Value as JObject)!;

                    fileObject.Property("nosync")?.Remove();
                    fileObject.Property("crc")?.Remove();
                    fileObject.Add("hash", "");
                    fileObject.Add("directory", false);
                }
            }

            //overwrite the previous sync with the updated one
            File.WriteAllText(PREVIOUS_SYNC_PATH, Json.Serialize(previousSync));
            //update the version.txt
            File.WriteAllText(VERSION_PATH, pluginVersion.ToString());
        }
        //if none of that was true then it's probably only minor changes, put something in the log and move on
        else if (oldVersion.Minor == pluginVersion.Minor && oldVersion != pluginVersion)
        {
            Plugin.Logger.LogWarning("Previous sync was made with a different version of the plugin. This may cause issues. Continuing...");
        }
    }
}
