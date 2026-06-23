using System.Text;
using System.Text.Json;

namespace TheTagHag;

/// <summary>
/// Harvest dedupe state (ported from CivitaiHarvesterApp). Three disjoint id sets — harvested /
/// skipped / rejected. On load, rejected fold into skipped so they're never re-harvested, and
/// harvested ids are reconciled from dataset.jsonl. Same JSON shape as the PowerShell original.
/// </summary>
public sealed class HarvestState
{
    public HashSet<int> Harvested { get; } = new();
    public HashSet<int> Skipped { get; } = new();
    public HashSet<int> Rejected { get; } = new();

    public bool Seen(int id) => Harvested.Contains(id) || Skipped.Contains(id);
}

public static class StateStore
{
    public static HarvestState Load(string stateFile, string datasetFile, Action<HarvestEvent> log)
    {
        var st = new HarvestState();
        if (File.Exists(stateFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(stateFile, Encoding.UTF8));
                var root = doc.RootElement;
                foreach (var id in root.Arr("harvestedImageIds")) st.Harvested.Add(id.GetInt32());
                foreach (var id in root.Arr("skippedImageIds")) st.Skipped.Add(id.GetInt32());
                foreach (var id in root.Arr("rejectedImageIds"))
                {
                    st.Rejected.Add(id.GetInt32());
                    st.Skipped.Add(id.GetInt32());
                }
            }
            catch (Exception ex) { log(new("WARN", $"state.json unreadable, starting fresh: {ex.Message}")); }
        }

        if (File.Exists(datasetFile))
        {
            try
            {
                foreach (var line in File.ReadLines(datasetFile, Encoding.UTF8))
                {
                    if (line.Trim().Length == 0) continue;
                    using var d = JsonDocument.Parse(line);
                    if (d.RootElement.TryProp("id", out var idEl)) st.Harvested.Add(idEl.GetInt32());
                }
            }
            catch (Exception ex) { log(new("WARN", $"could not reconcile from dataset.jsonl: {ex.Message}")); }
        }
        return st;
    }

    public static void Save(string stateFile, HarvestState st)
    {
        var skippedOnly = st.Skipped.Where(i => !st.Rejected.Contains(i)).OrderBy(i => i).ToArray();
        var obj = new
        {
            harvestedImageIds = st.Harvested.OrderBy(i => i).ToArray(),
            skippedImageIds = skippedOnly,
            rejectedImageIds = st.Rejected.OrderBy(i => i).ToArray(),
            lastRun = DateTime.Now.ToString("o"),
            version = AppInfo.Version,
        };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(stateFile, json, new UTF8Encoding(false));
    }
}
