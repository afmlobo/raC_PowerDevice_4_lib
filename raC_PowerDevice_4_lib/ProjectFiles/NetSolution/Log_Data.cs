
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using FTOptix.WebUI;
using System.Globalization;
using System.Collections.Generic;
using FTOptix.Store;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

public class Log_Data : BaseNetLogic
{
    private string deviceName;
    private string ipAddress;
    private Label  logLabel;
    private Store  dataStore;

    // UI variables
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;
    private const int CustomSelectedDays = -1;


    // Internal time range state
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;
   

    private const string DashboardFolderRel = "res/TabPowerDashboard";
    private const string TableName = "DataLogger_AllDevices";
    private const string StoreAliasName = "raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality";
    private const string StoreAliasParentPath =
        "Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores";

    public override void Start()
    {
        var tag = Owner.GetAlias("Tag");
        if (tag == null) { Log.Error("Log_Data", "Tag alias not found"); return; }

        var ipVariable = tag.GetVariable("Val_IPAddress");
        if (ipVariable == null || string.IsNullOrWhiteSpace(ipVariable.Value))
        { Log.Error("Log_Data", "Val_IPAddress variable missing or empty"); return; }
        ipAddress = ipVariable.Value;

        deviceName = tag.BrowseName;
        if (string.IsNullOrWhiteSpace(deviceName))
        { Log.Error("Log_Data", "Device name missing or empty"); return; }

        logLabel = Owner.Get<Label>("Status");
        if (logLabel == null)
        { Log.Error("Log_Data", "Label 'Status' not found"); return; }

        var storeAliasParent = Project.Current.GetObject(StoreAliasParentPath);
        dataStore = storeAliasParent?.Get<Store>(StoreAliasName);
        if (dataStore == null)
        { Log.Error("Log_Data", $"Store '{StoreAliasName}' not found"); logLabel.Text = "Error: DB store not found"; return; }

        // Page variables for date range
        startDateVar = Owner.GetVariable("StartDate");
        endDateVar   = Owner.GetVariable("EndDate");
        
try
        {
            var verticalLayout = Owner.Get("VerticalLayout1"); // type-agnostic lookup
            selectedDaysVar = verticalLayout?.GetVariable("SelectedDays") ?? Owner.GetVariable("SelectedDays");
        }
        catch (Exception e)
        {
            Log.Warning("Log_Data", $"SelectedDays variable not resolved: {e.Message}");
        }


        SetupDeviceSpecificHtml();
        //logLabel.Text = "Select Start/End and press Search to load DB data";
        LoadLast30Days();
    }

    public override void Stop() { }

    private void SetupDeviceSpecificHtml()
    {
        try
        {
            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string resDirectory = Path.Combine(baseDirectory, "res", "TabPowerDashboard");
            if (!Directory.Exists(resDirectory)) Directory.CreateDirectory(resDirectory);

            string htmlTemplatePath = Path.Combine(resDirectory, "Template_PowerDashboard.html");
            string deviceHtmlPath   = Path.Combine(resDirectory, $"Template_PowerDashboard_{deviceName}.html");

            if (!File.Exists(htmlTemplatePath))
            { Log.Error("Log_Data", $"HTML template not found at {htmlTemplatePath}"); logLabel.Text = "Error: HTML template not found"; return; }

            string htmlContent = File.ReadAllText(htmlTemplatePath);
            htmlContent = htmlContent.Replace("{{deviceName}}", deviceName ?? string.Empty);
            File.WriteAllText(deviceHtmlPath, htmlContent);

            var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
            if (webBrowser == null)
            { Log.Error("Log_Data", "WebBrowser control 'WebBrowser' not found"); logLabel.Text = "Error: WebBrowser control not found"; return; }

            var deviceHtmlUri =
                ResourceUri.FromProjectRelativePath($"res/TabPowerDashboard/Template_PowerDashboard_{deviceName}.html");
            webBrowser.URL = deviceHtmlUri;
        }
        catch (Exception e)
        { Log.Error("Log_Data", $"Error setting up HTML for {deviceName}: {e.Message}"); logLabel.Text = $"Error setting up HTML: {e.Message}"; }
    }

    // === PRIMARY ACTION: Apply date range from StartDate/EndDate and read from SQLite ===
    [ExportMethod]
    public void ApplyDateFilter()
    {
        // Manual/custom search -> no preset highlight
        ApplyDateFilterInternal(fromPreset: false);
    }

    private void ApplyDateFilterInternal(bool fromPreset)
    
    {
        try
        {
            // Preset buttons keep their highlight; manual search clears it
            if (!fromPreset && selectedDaysVar != null)
                selectedDaysVar.Value = CustomSelectedDays;
            // If user is doing custom search, clear preset highlight
            
            // Read StartDate / EndDate via robust variable reader
            DateTime start, end;
            //if (selectedDaysVar != null) selectedDaysVar.Value = -1;
            if (!TryGetDateFromVariable(startDateVar, out start))
            {
                logLabel.Text = "Start time is not a valid date/time.";
                return;
            }
            if (!TryGetDateFromVariable(endDateVar, out end))
            {
                logLabel.Text = "End time is not a valid date/time.";
                return;
            }

            // Validate range
            if (end < start)
            {
                logLabel.Text = "Error: End time is earlier than Start time";
                return;
            }

            // Normalize for SQL (with milliseconds)
            string startSql = start.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            string endSql   = end.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

            string sql =
                $"SELECT * FROM {TableName} " +
                $"WHERE DeviceName = '{deviceName}' " +
                $"AND Timestamp BETWEEN '{startSql}' AND '{endSql}' " +
                $"ORDER BY Timestamp ASC";

            dataStore.Query(sql, out string[] colNames, out object[,] rs);
            int rows = (rs == null) ? 0 : rs.GetLength(0);

            if (rows == 0)
            {
                SaveEmptyJsonForRange(start, end);
                logLabel.Text = "No data in selected range";
                RefreshBrowser();
                return;
            }

            // Resolve indices dynamically (underscored variants supported)
            int idxTimestamp   = FindIdx(colNames, "Timestamp");
            int idxYear        = FindIdx(colNames, "Year");
            int idxMonthDay    = FindIdx(colNames, "Month_Day", "MonthDay");
            int idxHourMinute  = FindIdx(colNames, "Hour_Minute", "HourMinute");
            int idxSecMilliSec = FindIdx(colNames, "Sec_MilliSec", "SecMilliSec", "SecondMilli");

            int idxVoltage      = FindIdx(colNames, "Voltage", "V", "Avg_V_N_Volts");
            int idxCurrent      = FindIdx(colNames, "Current", "I", "Avg_Amps");
            int idxFrequency    = FindIdx(colNames, "Frequency", "Hz", "Frequency_Hz");
            int idxTotalKW      = FindIdx(colNames, "Total_kW", "TotalKW");
            int idxTotalKVAR    = FindIdx(colNames, "Total_kVAR", "TotalKVAR");
            int idxTotalKVA     = FindIdx(colNames, "Total_kVA", "TotalKVA");
            int idxVoltUnbal    = FindIdx(colNames, "VoltageUnbalance", "Voltage_Unbalance");
            int idxCurrUnbal    = FindIdx(colNames, "CurrentUnbalance", "Current_Unbalance");

            // Build arrays for chart JSON (matches your dashboard)
            string[] xAxis            = new string[rows];
            float[]  voltage          = new float[rows];
            float[]  current          = new float[rows];
            float[]  total_kW         = new float[rows];
            float[]  total_kVAR       = new float[rows];
            float[]  total_kVA        = new float[rows];
            float[]  frequency        = new float[rows];
            float[]  voltageUnbalance = new float[rows];
            float[]  currentUnbalance = new float[rows];

            for (int r = 0; r < rows; r++)
            {
                // xAxis: prefer split fields, else DB Timestamp
                if (idxYear >= 0 && idxMonthDay >= 0 && idxHourMinute >= 0 && idxSecMilliSec >= 0)
                {
                    string y  = ToStr(rs[r, idxYear]);        // "2025"
                    string md = ToStr(rs[r, idxMonthDay]);     // "MMDD"
                    string hm = ToStr(rs[r, idxHourMinute]);   // "HHmm"
                    string sm = ToStr(rs[r, idxSecMilliSec]);  // "ssfff"
                    xAxis[r] = FormatTimestamp(y, md, hm, sm); // "yyyy-MM-dd HH:mm:ss.fff"
                }
                else if (idxTimestamp >= 0 && rs[r, idxTimestamp] != DBNull.Value)
                {
                    DateTime ts = Convert.ToDateTime(rs[r, idxTimestamp]);
                    xAxis[r] = ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }
                else
                {
                    xAxis[r] = string.Empty;
                }

                voltage[r]          = ToFloat(rs, r, idxVoltage);
                current[r]          = ToFloat(rs, r, idxCurrent);
                total_kW[r]         = ToFloat(rs, r, idxTotalKW);
                total_kVAR[r]       = ToFloat(rs, r, idxTotalKVAR);
                total_kVA[r]        = ToFloat(rs, r, idxTotalKVA);
                frequency[r]        = ToFloat(rs, r, idxFrequency);
                voltageUnbalance[r] = ToFloat(rs, r, idxVoltUnbal);
                currentUnbalance[r] = ToFloat(rs, r, idxCurrUnbal);
            }

            var chartData = new
            {
                fileName = $"DB:{startSql} → {endSql}",
                xAxis,
                voltage,
                current,
                total_kW,
                total_kVAR,
                total_kVA,
                frequency,
                voltageUnbalance,
                currentUnbalance
            };

            string projectDir  = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\").Uri;
            string jsonFile    = Path.Combine(projectDir, DashboardFolderRel, $"{deviceName}_PowerDashboardData.json");
            string jsonPayload = JsonConvert.SerializeObject(
                chartData,
                new JsonSerializerSettings
                {
                    StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                    Formatting = Formatting.Indented
                });

            File.WriteAllText(jsonFile, jsonPayload);

            logLabel.Text = $"Loaded {rows} DB records";
            RefreshBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Log_Data", $"ApplyDateFilter failed: {ex.Message}");
            logLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void SaveEmptyJsonForRange(DateTime start, DateTime end)
    {
        try
        {
            var chartData = new
            {
                fileName = $"DB:{start.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} → {end.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}",
                xAxis = Array.Empty<string>(),
                voltage = Array.Empty<float>(),
                current = Array.Empty<float>(),
                total_kW = Array.Empty<float>(),
                total_kVAR = Array.Empty<float>(),
                total_kVA = Array.Empty<float>(),
                frequency = Array.Empty<float>(),
                voltageUnbalance = Array.Empty<float>(),
                currentUnbalance = Array.Empty<float>()
            };

            string projectDir  = new FTOptix.Core.ResourceUri("%PROJECTDIR%\\").Uri;
            string jsonFile    = Path.Combine(projectDir, DashboardFolderRel, $"{deviceName}_PowerDashboardData.json");
            string jsonPayload = JsonConvert.SerializeObject(
                chartData,
                new JsonSerializerSettings
                {
                    StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                    Formatting = Formatting.Indented
                });

            File.WriteAllText(jsonFile, jsonPayload);
        }
        catch (Exception ex)
        { Log.Error("Log_Data", $"SaveEmptyJsonForRange failed: {ex.Message}"); }
    }

    private void RefreshBrowser()
    {
        var webBrowser = Owner.Get<WebBrowser>("WebBrowser");
        webBrowser?.Refresh();
    }

    // === Helpers ===

    // Read a DateTime from IUAVariable (converts Value.ToString via robust parsing)
    private bool TryGetDateFromVariable(IUAVariable var, out DateTime dt)
    {
        dt = DateTime.MinValue;

        if (var == null || var.Value == null)
            return false;

        string s = var.Value.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return TryParseUserDate(s, out dt);
    }

    // Accept many formats; normalize to DateTime
    private bool TryParseUserDate(string text, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().Replace('T', ' ');

        // Deterministic formats
        string[] formats =
        {
            // ISO-like
            "yyyy-MM-dd HH:mm:ss.fffffff",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd",

            // Slash formats
            "dd/MM/yyyy HH:mm:ss.fffffff",
            "dd/MM/yyyy HH:mm:ss.fff",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy",

            "MM/dd/yyyy HH:mm:ss.fffffff",
            "MM/dd/yyyy HH:mm:ss.fff",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy HH:mm",
            "MM/dd/yyyy",

            // Hyphen formats
            "dd-MM-yyyy HH:mm:ss.fffffff",
            "dd-MM-yyyy HH:mm:ss.fff",
            "dd-MM-yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm",
            "dd-MM-yyyy",

            "MM-dd-yyyy HH:mm:ss.fffffff",
            "MM-dd-yyyy HH:mm:ss.fff",
            "MM-dd-yyyy HH:mm:ss",
            "MM-dd-yyyy HH:mm",
            "MM-dd-yyyy",

            // Year-first alternates
            "yyyy/MM/dd HH:mm:ss.fff",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd"
        };

        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AllowWhiteSpaces, out dt))
            return true;

        // Heuristic: slashes or hyphens (dd first vs mm first)
        try
        {
            var parts = s.Split(' ');
            var datePart = parts[0];
            var timePart = parts.Length > 1 ? parts[1] : "00:00:00";

            char sep = datePart.Contains("/") ? '/' : (datePart.Contains("-") ? '-' : '\0');
            if (sep != '\0')
            {
                var dmy = datePart.Split(sep);
                if (dmy.Length == 3)
                {
                    int p0, p1, p2;
                    if (int.TryParse(dmy[0], out p0) && int.TryParse(dmy[1], out p1) && int.TryParse(dmy[2], out p2))
                    {
                        string ddMM = string.Format(CultureInfo.InvariantCulture, "{0:D2}{3}{1:D2}{3}{2:D4} {4}", p0, p1, p2, sep, timePart);
                        string MMdd = string.Format(CultureInfo.InvariantCulture, "{0:D2}{3}{1:D2}{3}{2:D4} {4}", p1, p0, p2, sep, timePart);

                        string[] ddFormats = (sep == '/')
                            ? new[] { "dd/MM/yyyy HH:mm:ss.fffffff", "dd/MM/yyyy HH:mm:ss.fff", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy" }
                            : new[] { "dd-MM-yyyy HH:mm:ss.fffffff", "dd-MM-yyyy HH:mm:ss.fff", "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy" };

                        if (DateTime.TryParseExact(ddMM, ddFormats, CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AllowWhiteSpaces, out dt))
                            return true;

                        string[] mmFormats = (sep == '/')
                            ? new[] { "MM/dd/yyyy HH:mm:ss.fffffff", "MM/dd/yyyy HH:mm:ss.fff", "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "MM/dd/yyyy" }
                            : new[] { "MM-dd-yyyy HH:mm:ss.fffffff", "MM-dd-yyyy HH:mm:ss.fff", "MM-dd-yyyy HH:mm:ss", "MM-dd-yyyy HH:mm", "MM-dd-yyyy" };

                        if (DateTime.TryParseExact(MMdd, mmFormats, CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AllowWhiteSpaces, out dt))
                            return true;
                    }
                }
            }
        }
        catch { /* ignore heuristic errors */ }

        // Broad fallback
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AllowWhiteSpaces, out dt);
    }

    private static int FindIdx(string[] cols, params string[] candidates)
    {
        if (cols == null || candidates == null) return -1;
        for (int i = 0; i < cols.Length; i++)
        {
            string cUpper = (cols[i] ?? string.Empty).ToUpperInvariant();
            foreach (var cand in candidates)
            {
                if (cUpper == (cand ?? string.Empty).ToUpperInvariant())
                    return i;
            }
        }
        return -1;
    }

    private static string ToStr(object v)
    {
        return (v == null || v == DBNull.Value) ? string.Empty : v.ToString();
    }

    private static float ToFloat(object[,] rs, int r, int idx)
    {
        if (idx < 0) return 0f;
        var v = rs[r, idx];
        if (v == null || v == DBNull.Value) return 0f;

        double d;
        if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return (float)d;

        return 0f;
    }

    // Timestamp formatter (Year, MonthDay=MMDD, HourMinute=HHmm, SecMilliSec=ssfff)
    private string FormatTimestamp(string year, string monthDay, string hourMinute, string secMilliSec)
    {
        try
        {
            int md = int.Parse((monthDay ?? "0").Trim());
            string month = (md / 100).ToString().PadLeft(2, '0');
            string day   = (md % 100).ToString().PadLeft(2, '0');

            int hm = int.Parse((hourMinute ?? "0").Trim());
            string hour   = (hm / 100).ToString().PadLeft(2, '0');
            string minute = (hm % 100).ToString().PadLeft(2, '0');

            int sm = int.Parse((secMilliSec ?? "0").Trim());
            string seconds      = (sm / 1000).ToString().PadLeft(2, '0');
            string milliseconds = (sm % 1000).ToString().PadLeft(3, '0');

            return string.Format(CultureInfo.InvariantCulture,
                                 "{0}-{1}-{2} {3}:{4}:{5}.{6}",
                                 year, month, day, hour, minute, seconds, milliseconds);
        }
               catch
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }
    
// === Quick Range Buttons (Last 24H / 7D / 30D) ===


// === Quick Range Buttons (SelectedDays + ApplyDateFilter) ===

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

        // Update internal state
            timeRangeStartDate = start;
            timeRangeEndDate = end;
            timeRangeDays = selectedDays;
        // Update UI state variable to support button highlight
        if (selectedDaysVar != null)selectedDaysVar.Value = selectedDays;

        // Update date range variables (same ones used by ApplyDateFilter)
        if (startDateVar != null)startDateVar.Value = start;

        if (endDateVar != null)endDateVar.Value = end;

        // Use existing logic (no DB/query code changes)
         ApplyDateFilterInternal(fromPreset: true);
    }
    catch (Exception ex)
    {
        Log.Error("Log_Data", $"ApplyRelativeRange failed: {ex.Message}");
        if (logLabel != null)
            logLabel.Text = $"Error: {ex.Message}";
       }


}
}
