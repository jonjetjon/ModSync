namespace ModSync.Utility;

public class SyncPath(string path, string name = "", bool enabled = true, bool enforced = false, bool silent = false, bool restartRequired = true)
{
    public readonly string path = path; //relative path to the sync directory
    public readonly string name = string.IsNullOrEmpty(name) ? path : name; //the name for this syncpath defaults to the path, this name is used in the f12 configurator as the display name
    public readonly bool enabled = enabled;
    public readonly bool enforced = enforced; 
    public readonly bool silent = silent; 
    public readonly bool restartRequired = restartRequired;
}
