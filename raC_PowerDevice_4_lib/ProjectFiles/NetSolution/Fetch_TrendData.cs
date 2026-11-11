
#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.UI;
using UAManagedCore;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;
using OpcUa = UAManagedCore.OpcUa;
#endregion

public class Fetch_TrendData : BaseNetLogic
{
    private const string TABLE = "DataLogger_AllDevices";
    private const string TABLE_ENERGY = "EnergyLog_AllDevices";

    private const string STORE_PARENT_PATH =
        "Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores";
    private const string STORE_ALIAS_NAME =
        "raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality";

    private const string RES_OUTPUT_FOLDER = "res/TabTrend";
    private const int MAX_TAG_SELECTION = 5;

    // Fixed Energy Log columns from EnergyLog_AllDevices table
    private static readonly List<string> ENERGY_LOG_COLUMNS = new()
    {
        "kW_Demand", "kVAR_Demand", "kVA_Demand", "Demand_PF",
        "Projected_kW_Demand", "Projected_kVAR_Demand", "Projected_kVA_Demand",
        "Total_kWh_Fwd", "Total_kWh_Rev", "Total_kWh_Net",
        "Total_kVARh_Fwd", "Total_kVARh_Rev", "Total_kVARh_Net", "Total_kVAh"
    };

    private Store dataStore;
    private string deviceName;

    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;
    private IUAVariable selectedTagVar;
    private IUAVariable selectedMultipleTagsVar;

    private Dictionary<string, CheckBox> tagCheckboxes = new();

    private static readonly HashSet<string> EXCLUDE_COLUMNS = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timestamp", "DeviceName", "SourceFile",
        "Record_ID", "Month_Day", "Hour_Minute", "Sec_MilliSec", "Year"
    };

    // Internal state (optional but useful like Energy)
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;

    public override void Start()
    {
        // Get the device from Tag alias
        var Tag = Owner.GetAlias("Tag");
        if (Tag == null)
        {
            Log.Error("Fetch_TrendData", "Tag alias not found");
            return;
        }

        deviceName = Tag.BrowseName;
        if (string.IsNullOrEmpty(deviceName))
        {
            Log.Error("Fetch_TrendData", "Device name is null or empty");
            return;
        }

        var storeAliasParent = Project.Current.GetObject(STORE_PARENT_PATH);
        dataStore = storeAliasParent?.Get<Store>(STORE_ALIAS_NAME);

        if (dataStore == null)
        {
            Log.Error("Fetch_TrendData", $"Store '{STORE_ALIAS_NAME}' not found under '{STORE_PARENT_PATH}'");
            return;
        }

        startDateVar = Owner.GetVariable("StartDate");
        endDateVar = Owner.GetVariable("EndDate");
        selectedDaysVar = Owner.GetVariable("SelectedDays");

        if (startDateVar != null) startDateVar.Value = timeRangeStartDate;
        if (endDateVar != null) endDateVar.Value = timeRangeEndDate;
        if (selectedDaysVar != null) selectedDaysVar.Value = timeRangeDays;

        // Ensure SelectedTag exists
        selectedTagVar = Owner.GetVariable("SelectedTag");
        if (selectedTagVar == null)
        {
            selectedTagVar = InformationModel.MakeVariable("SelectedTag", OpcUa.DataTypes.String);
            Owner.Add(selectedTagVar);
            selectedTagVar.Value = "";
        }

        // Ensure SelectedMultipleTags exists (JSON array of selected tags)
        selectedMultipleTagsVar = Owner.GetVariable("SelectedMultipleTags");
        if (selectedMultipleTagsVar == null)
        {
            selectedMultipleTagsVar = InformationModel.MakeVariable("SelectedMultipleTags", OpcUa.DataTypes.String);
            Owner.Add(selectedMultipleTagsVar);
            selectedMultipleTagsVar.Value = "[]";
        }

        try
        {
            var vl_Tags = Owner.Get<ColumnLayout>("HorizontalLayout1/Rectangle1/HorizontalLayout1/VerticalLayout1/TagScrollView/vl_Tags");
            if (vl_Tags != null)
            {
                InitializeTagCheckboxes(vl_Tags);
            }
            else
            {
                Log.Warning("Fetch_TrendData", "Tag list container (vl_Tags) not found");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Fetch_TrendData", $"Error initializing tag list: {ex.Message}");
        }

        WriteInfoJson("Select Tags To View Trending", "Please select one or more tags from the filter panel and click a time range button to load trend data.");
    }

    public override void Stop()
    {
        // No subscriptions
    }


    [ExportMethod]
    public void LoadLast24Hours()
    {
        LoadTrendForRange(DateTime.Now.AddHours(-24), DateTime.Now, 1);
    }

    [ExportMethod]
    public void LoadLast7Days()
    {
        LoadTrendForRange(DateTime.Now.AddDays(-7), DateTime.Now, 7);
    }

    [ExportMethod]
    public void LoadLast30Days()
    {
        LoadTrendForRange(DateTime.Now.AddDays(-30), DateTime.Now, 30);
    }

    private void LoadTrendForRange(DateTime start, DateTime end, int days)
    {
        try
        {
            timeRangeStartDate = start;
            timeRangeEndDate = end;
            timeRangeDays = days;

            if (selectedDaysVar != null) selectedDaysVar.Value = days;
            if (startDateVar != null) startDateVar.Value = start;
            if (endDateVar != null) endDateVar.Value = end;

            ExportMultipleTagsTrend();
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", "LoadTrendForRange error: " + ex.Message);
            WriteInfoJson("Load Error", $"Failed to load trend data: {ex.Message}");
            try
            {
                var wb = Owner.Get<WebBrowser>("HorizontalLayout1/TrendWebBrowser");
                wb?.Refresh();
            }
            catch { }
        }
    }

   
    [ExportMethod]
    public void ApplyDateFilter()
    {
        WebBrowser wb = null;
        try
        {
            wb = Owner.Get<WebBrowser>("HorizontalLayout1/TrendWebBrowser");
        }
        catch { }

        try
        {
            if (!TryGetDateFromVariable(startDateVar, out DateTime start))
            {
                WriteInfoJson("Invalid Start Date", "The start date is invalid. Please enter a valid start date.");
                wb?.Refresh();
                return;
            }

            if (!TryGetDateFromVariable(endDateVar, out DateTime end))
            {
                WriteInfoJson("Invalid End Date", "The end date is invalid. Please enter a valid end date.");
                wb?.Refresh();
                return;
            }

            if (end < start)
            {
                WriteInfoJson("Invalid Date Range", "End date is earlier than start date. Please correct the date range.");
                wb?.Refresh();
                return;
            }

            if (end == start)
            {
                start = start.Date;
                end = start.AddDays(1);
            }

            timeRangeStartDate = start;
            timeRangeEndDate = end;
            timeRangeDays = Math.Max(1, (int)Math.Ceiling((end - start).TotalDays));
            if (selectedDaysVar != null) selectedDaysVar.Value = -1; // custom

            ExportMultipleTagsTrend();

        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", "ApplyDateFilter failed: " + ex.Message);
            WriteInfoJson("Apply Filter Failed", $"Failed to apply date filter: {ex.Message}");
            wb?.Refresh();
        }
    }

    
    private void InitializeTagCheckboxes(ColumnLayout vl_Tags)
    {
        try
        {
            dataStore.Query($"SELECT * FROM {Q(TABLE)} LIMIT 1", out string[] colsDataLog, out _);

            var tagCols = new List<string>();
            tagCols.AddRange(ENERGY_LOG_COLUMNS);

            if (colsDataLog != null && colsDataLog.Length > 0)
            {
                var dataLogCols = colsDataLog
                    .Where(c => !string.IsNullOrWhiteSpace(c) && !EXCLUDE_COLUMNS.Contains(c))
                    .ToList();
                tagCols.AddRange(dataLogCols);
            }
            else
            {
                Log.Warning("Fetch_TrendData", "No columns found in DataLogger_AllDevices.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            tagCols = tagCols.Where(t => seen.Add(t)).ToList();

            if (tagCols.Count == 0)
            {
                Log.Warning("Fetch_TrendData", "No columns available after merging DataLogger and EnergyLog.");
                return;
            }

            tagCheckboxes.Clear();

            // Remove only dynamically created checkboxes, keep Label1
            var existingCheckboxes = vl_Tags.Children.OfType<CheckBox>().ToList();
            foreach (var cb in existingCheckboxes)
            {
                vl_Tags.Remove(cb);
            }

            foreach (var tag in tagCols)
            {
                var checkbox = InformationModel.Make<CheckBox>($"Chk_{SanitizeNodeName(tag)}");
                checkbox.Text = FormatDisplayName(tag);
                checkbox.Checked = false;
                checkbox.Height = 30;
                checkbox.Width = -1;

                checkbox.OnUserValueChanged += (sender, e) =>
                {
                    OnTagCheckboxChanged(tag, checkbox.Checked);
                };

                vl_Tags.Add(checkbox);
                tagCheckboxes[tag] = checkbox;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error initializing tag checkboxes: {ex.Message}");
        }
    }

    private void OnTagCheckboxChanged(string tag, bool isChecked)
    {
        try
        {
            var selectedTags = GetSelectedTags();
            
            if (isChecked && selectedTags.Count > MAX_TAG_SELECTION)
            {
                if (tagCheckboxes.TryGetValue(tag, out var checkbox))
                {
                    checkbox.Checked = false;
                }
                return;
            }
            
            UpdateSelectedTagsList();
            UpdateTagCheckboxStates();
            ExportMultipleTagsTrend();
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error in OnTagCheckboxChanged: {ex.Message}");
        }
    }

    private List<string> GetSelectedTags()
    {
        var selected = new List<string>();
        foreach (var kvp in tagCheckboxes)
        {
            if (kvp.Value.Checked)
                selected.Add(kvp.Key);
        }
        return selected;
    }

    private void UpdateSelectedTagsList()
    {
        try
        {
            var selectedTags = GetSelectedTags();
            var jsonArray = JsonSerializer.Serialize(selectedTags);

            if (selectedMultipleTagsVar != null)
                selectedMultipleTagsVar.Value = jsonArray;
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error updating SelectedTagsList: {ex.Message}");
        }
    }

    private void UpdateTagCheckboxStates()
    {
        try
        {
            var selectedTags = GetSelectedTags();
            bool limitReached = selectedTags.Count >= MAX_TAG_SELECTION;

            foreach (var kvp in tagCheckboxes)
            {
                kvp.Value.Enabled = !limitReached || kvp.Value.Checked;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Fetch_TrendData", $"Error updating checkbox states: {ex.Message}");
        }
    }

    [ExportMethod]
    public void ExportMultipleTagsTrend()
    {
        WebBrowser wb = null;
        try
        {
            wb = Owner.Get<WebBrowser>("HorizontalLayout1/TrendWebBrowser");
        }
        catch { }

        try
        {
            if (!TryGetDateFromVariable(startDateVar, out DateTime start))
            {
                WriteInfoJson("Invalid Start Date", "The start date is invalid. Please select a valid start date.");
                wb?.Refresh();
                return;
            }

            if (!TryGetDateFromVariable(endDateVar, out DateTime end))
            {
                WriteInfoJson("Invalid End Date", "The end date is invalid. Please select a valid end date.");
                wb?.Refresh();
                return;
            }

            if (end < start)
            {
                WriteInfoJson("Invalid Date Range", "End date is earlier than start date. Please correct the date range.");
                wb?.Refresh();
                return;
            }

            if (end == start)
            {
                start = start.Date;
                end = start.AddDays(1);
            }

            var selectedTags = GetSelectedTags();
            if (selectedTags.Count == 0)
            {
                WriteInfoJson("No Tags Selected", "Please select at least one tag from the filter panel to view trend data.");
                wb?.Refresh();
                return;
            }

            ExportMultipleTagsJson(start, end, selectedTags);
            wb?.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"ExportMultipleTagsTrend error: {ex.Message}");
            WriteInfoJson("Error", $"Failed to export trend data: {ex.Message}");
            wb?.Refresh();
        }
    }

    private void ExportMultipleTagsJson(DateTime start, DateTime end, List<string> tags)
    {

        try
        {
            string startIso = ToIso(start);
            string endIso = ToIso(end);

            // Group tags by source table
            var dataLogTags = tags.Where(t => !IsEnergyLogTag(t)).ToList();
            var energyLogTags = tags.Where(t => IsEnergyLogTag(t)).ToList();

            var seriesData = new List<Dictionary<string, object>>();

            // Query DataLogger_AllDevices if there are DataLog tags
            if (dataLogTags.Count > 0)
            {
                var tagList = string.Join(", ", dataLogTags.Select(t => Q(t)));
                string sql =
                    $"SELECT {Q("Timestamp")}, {tagList} " +
                    $"FROM {Q(TABLE)} " +
                    $"WHERE {Q("DeviceName")} = \"{deviceName}\" AND {Q("Timestamp")} >= \"{startIso}\" AND {Q("Timestamp")} <= \"{endIso}\" " +
                    $"ORDER BY {Q("Timestamp")} ASC";

                dataStore.Query(sql, out string[] cols, out object[,] rows);

                if (rows != null && rows.GetLength(0) > 0)
                {
                    int rowCount = rows.GetLength(0);
                    for (int i = 0; i < rowCount; i++)
                    {
                        DateTime ts = ParseTimestamp(rows[i, 0]);
                        for (int j = 0; j < dataLogTags.Count; j++)
                        {
                            double? val = TryParseDouble(rows[i, j + 1]);
                            seriesData.Add(new Dictionary<string, object>
                            {
                                ["ts"] = ts == DateTime.MinValue ? "" : ToIso(ts),
                                ["tag"] = dataLogTags[j],
                                ["v"] = val
                            });
                        }
                    }
                }
            }

            // Query EnergyLog_AllDevices if there are Energy tags
            if (energyLogTags.Count > 0)
            {
                var tagList = string.Join(", ", energyLogTags.Select(t => Q(t)));
                string sql =
                    $"SELECT {Q("Timestamp")}, {tagList} " +
                    $"FROM {Q(TABLE_ENERGY)} " +
                    $"WHERE {Q("DeviceName")} = \"{deviceName}\" AND {Q("Timestamp")} >= \"{startIso}\" AND {Q("Timestamp")} <= \"{endIso}\" " +
                    $"ORDER BY {Q("Timestamp")} ASC";

                dataStore.Query(sql, out string[] cols, out object[,] rows);

                if (rows != null && rows.GetLength(0) > 0)
                {
                    int rowCount = rows.GetLength(0);
                    for (int i = 0; i < rowCount; i++)
                    {
                        DateTime ts = ParseTimestamp(rows[i, 0]);
                        for (int j = 0; j < energyLogTags.Count; j++)
                        {
                            double? val = TryParseDouble(rows[i, j + 1]);
                            seriesData.Add(new Dictionary<string, object>
                            {
                                ["ts"] = ts == DateTime.MinValue ? "" : ToIso(ts),
                                ["tag"] = energyLogTags[j],
                                ["v"] = val
                            });
                        }
                    }
                }
            }

            // Check if we got any data
            if (seriesData.Count == 0)
            {
                Log.Warning("Fetch_TrendData", "No data found in either table");
                string tagsDisplay = string.Join(", ", tags.Select(t => FormatDisplayName(t)));
                WriteInfoJson(
                    "No Data Found",
                    $"No data available for the selected tags ({tagsDisplay}) in the date range {start:yyyy-MM-dd} to {end:yyyy-MM-dd}. Please try a different date range or check if data logging is active."
                );
                return;
            }

            const int MAX_POINTS = 50000;
            if (seriesData.Count > MAX_POINTS)
            {
                int step = (int)Math.Ceiling(seriesData.Count / (double)MAX_POINTS);
                seriesData = seriesData.Where((p, idx) => idx % step == 0).ToList();
            }

            var payload = new
            {
                tags = tags,
                start = ToIso(start),
                end = ToIso(end),
                count = seriesData.Count,
                series = seriesData
            };

            WriteMultipleTagsJson(payload);
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error exporting multiple tags: {ex.Message}");
            if (ex.InnerException != null)
                Log.Error("Fetch_TrendData", $"Inner Exception: {ex.InnerException.Message}");
            WriteInfoJson("Export Error", $"Failed to export trend data: {ex.Message}");
        }
    }

    private void WriteMultipleTagsJson(object data)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);

            var projectUri = new ResourceUri("%PROJECTDIR%/");
            string baseDirectory = projectUri.Uri;

            // Multi-tag output file
            string resDir = Path.Combine(baseDirectory, RES_OUTPUT_FOLDER);
            Directory.CreateDirectory(resDir);
            string resPath = Path.Combine(resDir, "TrendData.json");
            File.WriteAllText(resPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error writing multi-tag JSON: {ex.Message}");
        }
    }

    private void WriteInfoJson(string title, string message)
    {
        try
        {
            var infoData = new
            {
                type = "info",
                title = title,
                message = message,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(infoData, options);

            var projectUri = new ResourceUri("%PROJECTDIR%/");
            string baseDirectory = projectUri.Uri;

            string resDir = Path.Combine(baseDirectory, RES_OUTPUT_FOLDER);
            Directory.CreateDirectory(resDir);
            string resPath = Path.Combine(resDir, "TrendData.json");
            File.WriteAllText(resPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Fetch_TrendData", $"Error writing info JSON: {ex.Message}");
        }
    }

    private bool TryGetDateFromVariable(IUAVariable uaVar, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (uaVar == null) return false;

        var uaValue = uaVar.Value;
        if (uaValue == null) return false;

        object raw = uaValue.Value;
        if (raw == null) return false;

        if (raw is DateTime d)
        {
            dt = d;
            return true;
        }

        string s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim().Replace('T', ' ');

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;

        return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    private static string ToIso(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private DateTime ParseTimestamp(object tsObj)
    {
        if (tsObj == null || tsObj == DBNull.Value) return DateTime.MinValue;

        if (tsObj is DateTime dt) return dt;

        string s = tsObj.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;

        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            return parsed;

        return DateTime.MinValue;
    }

    private double? TryParseDouble(object vObj)
    {
        if (vObj == null || vObj == DBNull.Value) return null;

        if (vObj is IConvertible && vObj is not string)
        {
            try { return Convert.ToDouble(vObj, CultureInfo.InvariantCulture); }
            catch { }
        }

        var s = vObj.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
            return dv;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out dv))
            return dv;

        return null;
    }

    private string Q(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "\"\"";
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private string SanitizeNodeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var safe = new string(raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (char.IsDigit(safe[0])) safe = "_" + safe;
        return safe;
    }

    private bool IsEnergyLogTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        return ENERGY_LOG_COLUMNS.Any(c => string.Equals(c, tagName, StringComparison.OrdinalIgnoreCase));
    }

    private string FormatDisplayName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return "Unknown";

        try
        {
            // Remove trailing underscore and % character
            string formatted = columnName.TrimEnd('_', '%');

            // Replace underscores with spaces
            formatted = formatted.Replace('_', ' ');

            return formatted;
        }
        catch
        {
            return columnName;
        }
    }
}
