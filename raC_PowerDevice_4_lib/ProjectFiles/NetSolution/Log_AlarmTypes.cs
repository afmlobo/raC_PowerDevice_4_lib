
#region Using directives
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.WebUI;
using FTOptix.Store;
using FTOptix.Core;

using Newtonsoft.Json;
using FTOptix.SerialPort;
#endregion

public class Log_AlarmTypes : BaseNetLogic
{
    private const string ALARM_TABLE_NAME = "AlarmLog_AllDevices";
    private IUAVariable selectedDaysVar;
    private const int CustomSelectedDays = -1;

    private string deviceName;
    private Store alarmStore;
    private Label logLabel;
    private WebBrowser webBrowser;
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("Log_AlarmTypes", "Tag alias not found");
            return;
        }

        deviceName = tag.BrowseName;

        logLabel = Owner.Get<Label>("log");
        webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        startDateVar = Owner.GetVariable("StartDate");
        endDateVar = Owner.GetVariable("EndDate");
        selectedDaysVar = Owner.GetVariable("SelectedDays");

        if (selectedDaysVar != null)
            selectedDaysVar.Value = 7;

        if (startDateVar != null && endDateVar != null)
        {
            endDateVar.Value = DateTime.Now;
            startDateVar.Value = DateTime.Now.AddDays(-7);
        }

        alarmStore = GetAlarmStore();

        if (alarmStore == null)
        {
            Log.Error("Log_AlarmTypes", "PowerQuality SQLite Store not found.");
            if (logLabel != null)
                logLabel.Text = "DB Store not found";
            return;
        }

        if (logLabel != null)
            logLabel.Text = "Ready";

        // Load last 30 days report initially
        LoadLast30Days();
    }

    public override void Stop()
    {
    }

    [ExportMethod]
    public void ProcessAlarmLog()
    {
        ApplyDateFilterInternal(fromPreset: false);
    }

    private void ApplyDateFilterInternal(bool fromPreset)
    {
        try
        {
            if (!fromPreset && selectedDaysVar != null)
                selectedDaysVar.Value = CustomSelectedDays;

            if (alarmStore == null)
                alarmStore = GetAlarmStore();

            if (alarmStore == null)
                throw new Exception("Alarm Store not found.");

            DateTime startDate = startDateVar?.Value ?? DateTime.Now.AddDays(-7);
            DateTime endDate = endDateVar?.Value ?? DateTime.Now;

            if (startDate > endDate)
            {
                if (logLabel != null)
                    logLabel.Text = "Error: Start date must be earlier than end date";
                Log.Error("Log_AlarmTypes", "Start date must be earlier than end date");
                return;
            }

            if (logLabel != null)
                logLabel.Text = "Querying DB...";

            var alarmDataList = ReadAlarmDataFromDb(startDate, endDate);

            string projectDir = new ResourceUri("%PROJECTDIR%\\").Uri;
            SaveJsonData(projectDir, alarmDataList);
            SetupDeviceSpecificHtml();

            if (logLabel != null)
                logLabel.Text = $"Total Alarms: ({alarmDataList.Count})";

            webBrowser?.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error("Log_AlarmTypes", $"Error: {ex.Message}");
            if (logLabel != null)
                logLabel.Text = $"Failed: {ex.Message}";
        }
    }

    private Store GetAlarmStore()
    {
        try
        {
            return Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");
        }
        catch (Exception ex)
        {
            Log.Error("Log_AlarmTypes", $"Failed to get alarm store: {ex.Message}");
            return null;
        }
    }

    private List<AlarmData> ReadAlarmDataFromDb(DateTime start, DateTime end)
    {
        // Build query with fixed column names and ISO date format
        string query = $@"
            SELECT Timestamp, AlarmTypeCode, AlarmTypeDescription, AlarmCode, AlarmCodeDescription
            FROM {ALARM_TABLE_NAME}
            WHERE DeviceName = '{deviceName.Replace("'", "''")}' 
              AND Timestamp BETWEEN '{start:yyyy-MM-ddTHH:mm:ss}' AND '{end:yyyy-MM-ddTHH:mm:ss}'
            ORDER BY Timestamp DESC
            LIMIT 5000";

        alarmStore.Query(query, out string[] _, out object[,] resultSet);

        var list = new List<AlarmData>();
        if (resultSet == null || resultSet.GetLength(0) == 0)
            return list;

        // Read data directly - columns are in order: Timestamp, AlarmTypeCode, AlarmTypeDescription, AlarmCode, AlarmCodeDescription
        for (int r = 0; r < resultSet.GetLength(0); r++)
        {
            list.Add(new AlarmData
            {
                Alarm_Record_Identifier = r + 1,
                Alarm_Timestamp = SafeToDateTime(resultSet[r, 0]).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Alarm_Type_Code = SafeToInt(resultSet[r, 1]),
                Alarm_Type_Description = SafeToString(resultSet[r, 2]),
                Alarm_Code = SafeToInt(resultSet[r, 3]),
                Alarm_Code_Description = SafeToString(resultSet[r, 4])
            });
        }

        return list;
    }

    private void SaveJsonData(string baseDirectory, List<AlarmData> alarmDataList)
    {
        var chartData = new
        {
            headers = new[] { "ID", "Timestamp", "Alarm_Type", "Alarm_Code", "Description" },
            alarms = alarmDataList.Select(a => new
            {
                id = a.Alarm_Record_Identifier,
                timestamp = a.Alarm_Timestamp,
                alarmTypeCode = a.Alarm_Type_Code,
                alarmTypeDescription = a.Alarm_Type_Description,
                alarmCode = a.Alarm_Code,
                alarmCodeDescription = a.Alarm_Code_Description
            }).ToArray()
        };

        string jsonFilePath = Path.Combine(baseDirectory, "res", "TabAlarmLog", $"{deviceName}_AlarmLogData.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
        File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(chartData, Formatting.Indented));
    }

    private void SetupDeviceSpecificHtml()
    {
        string baseDirectory = new ResourceUri("%PROJECTDIR%\\").Uri;
        string resDirectory = Path.Combine(baseDirectory, "res", "TabAlarmLog");
        Directory.CreateDirectory(resDirectory);

        string htmlTemplatePath = Path.Combine(resDirectory, "Template_AlarmLog.html");
        string deviceHtmlPath = Path.Combine(resDirectory, $"{deviceName}_AlarmLog.html");

        if (!File.Exists(htmlTemplatePath))
        {
            Log.Error("Log_AlarmTypes", $"HTML template not found at {htmlTemplatePath}");
            return;
        }

        string htmlContent = File.ReadAllText(htmlTemplatePath);
        htmlContent = htmlContent.Replace("{{deviceName}}", deviceName);
        File.WriteAllText(deviceHtmlPath, htmlContent);

        if (webBrowser != null)
            webBrowser.URL = ResourceUri.FromProjectRelativePath($"res/TabAlarmLog/{deviceName}_AlarmLog.html");
    }

    private static int SafeToInt(object o)
    {
        if (o == null || o == DBNull.Value) return 0;
        if (o is int i) return i;
        if (o is long l) return (int)l;
        if (int.TryParse(o?.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static DateTime SafeToDateTime(object o)
    {
        if (o == null || o == DBNull.Value) return DateTime.MinValue;
        if (o is DateTime dt) return dt;
        if (DateTime.TryParse(o?.ToString(), out var parsed)) return parsed;
        return DateTime.MinValue;
    }

    private static string SafeToString(object o)
    {
        if (o == null || o == DBNull.Value) return string.Empty;
        return o.ToString();
    }

    // ----------------------------
    // Data model (same as your old code)
    // ----------------------------
    private class AlarmData
    {
        public int Alarm_Record_Identifier { get; set; }
        public string Alarm_Timestamp { get; set; }
        public int Alarm_Type_Code { get; set; }
        public string Alarm_Type_Description { get; set; }
        public int Alarm_Code { get; set; }
        public string Alarm_Code_Description { get; set; }
    }


    [ExportMethod]
    public void LoadLast24Hours()
    {
        ApplyRelativeRange(DateTime.Now.AddHours(-24), DateTime.Now, 1);
    }

    [ExportMethod]
    public void LoadLast7Days()
    {
        ApplyRelativeRange(DateTime.Now.AddDays(-7), DateTime.Now, 7);
    }

    [ExportMethod]
    public void LoadLast30Days()
    {
        ApplyRelativeRange(DateTime.Now.AddDays(-30), DateTime.Now, 30);
    }

    private void ApplyRelativeRange(DateTime start, DateTime end, int selectedDays)
    {
        try
        {
            if (selectedDaysVar != null)
                selectedDaysVar.Value = selectedDays;

            if (startDateVar != null)
                startDateVar.Value = start;

            if (endDateVar != null)
                endDateVar.Value = end;

            ApplyDateFilterInternal(fromPreset: true);
        }
        catch (Exception ex)
        {
            Log.Error("Log_AlarmTypes", $"ApplyRelativeRange failed: {ex.Message}");
            if (logLabel != null)
                logLabel.Text = $"Error: {ex.Message}";
        }
    }
}
