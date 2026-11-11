
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.UI;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;

public class Log_Energy : BaseNetLogic
{
    // ------------------------------------------------------------
    // Device resolution
    // ------------------------------------------------------------
    private string deviceName;
    private string ipAddress;

    // Optional variable exposed on the logic (debug/UI binding)
    private IUAVariable deviceNameVar;

    // Optional UI objects (only used if present in page)
    private Label logLabel;
    private WebBrowser webBrowser;

    // ------------------------------------------------------------
    // Date range variables (UI)
    // ------------------------------------------------------------
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;

    // ------------------------------------------------------------
    // Store / Table
    // ------------------------------------------------------------
    private Store commonStore;
    private const string ENERGY_LOG_TABLE = "EnergyLog_AllDevices";

    // Store alias details
    private const string StoreAliasName = "raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality";
    private const string StoreAliasParentPath =
        "Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores";

    // ------------------------------------------------------------
    // Output JSON location
    // ------------------------------------------------------------
    private const string OutputFolder = "res/TabEnergyLog";
    private const string OutputFile = "EnergyLogData.json";

    // Internal time range state
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;

    public override void Start()
    {
        // Optional UI objects (safe if not found)
        try { webBrowser = Owner.Get<WebBrowser>("wb_EnergyDashboard"); } catch { }
        try { logLabel = Owner.Get<Label>("lbl_Log"); } catch { }

        // 1) Resolve deviceName from Tag alias
        var tag = Owner.GetAlias("Tag");
        if (tag == null)
        {
            Log.Error("Log_Energy", "Tag alias not found");
            WriteEmptyEnergyDashboard();
            return;
        }

        var ipVar = tag.GetVariable("Val_IPAddress");
        if (ipVar != null && ipVar.Value != null && !string.IsNullOrWhiteSpace(ipVar.Value.ToString()))
            ipAddress = ipVar.Value.ToString();

        deviceName = tag.BrowseName;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            Log.Error("Log_Energy", "Device name missing or empty (Tag.BrowseName)");
            WriteEmptyEnergyDashboard();
            return;
        }

        // Optional: expose DeviceName variable on logic (for UI/debug)
        try
        {
            deviceNameVar = Owner.GetVariable("DeviceName");
            if (deviceNameVar == null)
            {
                deviceNameVar = InformationModel.MakeVariable("DeviceName", OpcUa.DataTypes.String);
                LogicObject.Add(deviceNameVar);
            }
            deviceNameVar.Value = deviceName;
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "DeviceName variable setup failed: " + ex.Message);
        }

        // 2) Date vars
        try
        {
            startDateVar = Owner.GetVariable("StartDate");
            endDateVar = Owner.GetVariable("EndDate");
            selectedDaysVar = Owner.GetVariable("SelectedDays");

            if (startDateVar != null) startDateVar.Value = timeRangeStartDate;
            if (endDateVar != null) endDateVar.Value = timeRangeEndDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = timeRangeDays;
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Error getting date variables: " + ex.Message);
        }

        // 3) Store connection
        try
        {
            var storeAliasParent = Project.Current.GetObject(StoreAliasParentPath);
            commonStore = storeAliasParent?.Get<Store>(StoreAliasName);

            if (commonStore == null)
            {
                Log.Error("Log_Energy",
                    $"Store '{StoreAliasName}' not found under '{StoreAliasParentPath}'");
                WriteEmptyEnergyDashboard();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Log_Energy", "Failed to get common store: " + ex.Message);
            WriteEmptyEnergyDashboard();
            return;
        }

        // 4) Initial load
        try
        {
            AggregateEnergyData(timeRangeStartDate, timeRangeEndDate, timeRangeDays);
            RefreshEnergyDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Initial load error: " + ex.Message);
            WriteEmptyEnergyDashboard();
        }
    }

    public override void Stop()
    {
        // No subscriptions used
    }

    // ------------------------------------------------------------
    // EXPORT METHODS (Preset buttons)
    // ------------------------------------------------------------
    [ExportMethod]
    public void LoadLast24Hours()
    {
        LoadEnergyForRange(DateTime.Now.AddHours(-24), DateTime.Now, 1);
    }

    [ExportMethod]
    public void LoadLast7Days()
    {
        LoadEnergyForRange(DateTime.Now.AddDays(-7), DateTime.Now, 7);
    }

    [ExportMethod]
    public void LoadLast30Days()
    {
        LoadEnergyForRange(DateTime.Now.AddDays(-30), DateTime.Now, 30);
    }

    // ✅ This is the correct "preset" worker: it MUST aggregate + refresh
    private void LoadEnergyForRange(DateTime start, DateTime end, int selectedDays)
    {
        try
        {
            // Update internal state
            timeRangeStartDate = start;
            timeRangeEndDate = end;
            timeRangeDays = selectedDays;

            // Update UI vars
            if (selectedDaysVar != null) selectedDaysVar.Value = selectedDays;
            if (startDateVar != null) startDateVar.Value = start;
            if (endDateVar != null) endDateVar.Value = end;

            // ✅ Missing earlier: actually regenerate dashboard JSON
            AggregateEnergyData(start, end, selectedDays);

            // Refresh browser
            RefreshEnergyDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Log_Energy", "LoadEnergyForRange error: " + ex.Message);
            if (logLabel != null)
                logLabel.Text = $"Error: {ex.Message}";
        }
    }

    // ------------------------------------------------------------
    // Search button method (custom date range)
    // Reads StartDate/EndDate variables and reloads
    // ------------------------------------------------------------
    [ExportMethod]
    public void ApplyDateFilter()
    {
        try
        {
            if (!TryGetDateFromVariable(startDateVar, out DateTime start))
            {
                Log.Warning("Log_Energy", "StartDate is invalid");
                WriteEmptyEnergyDashboard();
                RefreshEnergyDashboardBrowser();
                return;
            }

            if (!TryGetDateFromVariable(endDateVar, out DateTime end))
            {
                Log.Warning("Log_Energy", "EndDate is invalid");
                WriteEmptyEnergyDashboard();
                RefreshEnergyDashboardBrowser();
                return;
            }

            if (end < start)
            {
                Log.Warning("Log_Energy", "EndDate is earlier than StartDate");
                WriteEmptyEnergyDashboard();
                RefreshEnergyDashboardBrowser();
                return;
            }

            // ✅ Important fix: if start == end, expand to a valid range
            // SQL uses: Timestamp >= start AND Timestamp < end, so start==end returns nothing
            if (end == start)
            {
                start = start.Date;
                end = start.AddDays(1);
            }

            // Update internal state
            timeRangeStartDate = start;
            timeRangeEndDate = end;
            timeRangeDays = Math.Max(1, (int)Math.Ceiling((end - start).TotalDays));
            if (selectedDaysVar != null) selectedDaysVar.Value = -1; // "custom"

            // Aggregate + refresh
            AggregateEnergyData(start, end, timeRangeDays);
            RefreshEnergyDashboardBrowser();

            Log.Info("Log_Energy", $"ApplyDateFilter executed for {start} -> {end} on device {deviceName}");
        }
        catch (Exception ex)
        {
            Log.Error("Log_Energy", "ApplyDateFilter failed: " + ex.Message);
            WriteEmptyEnergyDashboard();
            RefreshEnergyDashboardBrowser();
        }
    }

    // Robust DateTime reader (works whether variable is DateTime or string)
    private bool TryGetDateFromVariable(IUAVariable uaVar, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (uaVar == null)
            return false;

        var uaValue = uaVar.Value;
        if (uaValue == null)
            return false;

        object raw = uaValue.Value;
        if (raw == null)
            return false;

        if (raw is DateTime d)
        {
            dt = d;
            return true;
        }

        string s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        s = s.Trim().Replace('T', ' ');

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;

        return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    // ------------------------------------------------------------
    // CORE AGGREGATION (Single device = Tag.BrowseName)
    // ------------------------------------------------------------
    private void AggregateEnergyData(DateTime startDate, DateTime endDate, int daysInRange)
    {
        try
        {
            // ✅ FIX: missing ||
            if (commonStore == null || string.IsNullOrWhiteSpace(deviceName))
            {
                WriteEmptyEnergyDashboard();
                return;
            }

            var deviceNames = new List<string> { deviceName };

            var dashboardData = new
            {
                orgName = "", // kept for JS stability
                deviceCount = 1,
                timeRange = GetTimeRangeDescription(daysInRange),
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                kpis = CalculateEnergyKPIs(deviceNames, startDate, endDate),
                energyTrendChart = GetEnergyTrendDataSingle(deviceNames, startDate, endDate, daysInRange),
                deviceEnergyChart = GetDeviceEnergyDistribution(deviceNames, startDate, endDate),
                demandChart = GetDemandProfileDataSingle(deviceNames, startDate, endDate),
                activeReactiveChart = GetActiveReactiveData(deviceNames, startDate, endDate),
                powerFactorChart = GetPowerFactorDataSingle(deviceNames, startDate, endDate, daysInRange),
                statusChart = GetDeviceStatusDistribution(deviceNames, startDate, endDate)
            };

            WriteEnergyDashboardJSON(dashboardData);
        }
        catch (Exception ex)
        {
            Log.Error("Log_Energy", "AggregateEnergyData error: " + ex.Message);
            WriteEmptyEnergyDashboard();
        }
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------
    private static string ToIso(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private object CalculateEnergyKPIs(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        double totalKWh = 0, peakDemandKW = 0, avgPowerFactor = 0, totalKVARh = 0, totalKVAh = 0;
        int deviceCount = 0;

        try
        {
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            foreach (var devName in deviceNames)
            {
                string escaped = (devName ?? "").Replace("'", "''");

                string q1 =
                    "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal " +
                    "FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";
                commonStore.Query(q1, out var c1, out var r1);

                if (r1 != null && r1.GetLength(0) > 0 && r1[0, 0] != DBNull.Value && r1[0, 1] != DBNull.Value)
                    totalKWh += (Convert.ToDouble(r1[0, 0]) - Convert.ToDouble(r1[0, 1]));

                string q2 =
                    "SELECT MAX(Total_kVARh_Net) as MaxKVARh, MIN(Total_kVARh_Net) as MinKVARh, " +
                    "MAX(Total_kVAh) as MaxKVAh, MIN(Total_kVAh) as MinKVAh " +
                    "FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";
                commonStore.Query(q2, out var c2, out var r2);

                if (r2 != null && r2.GetLength(0) > 0)
                {
                    if (r2[0, 0] != DBNull.Value && r2[0, 1] != DBNull.Value)
                        totalKVARh += (Convert.ToDouble(r2[0, 0]) - Convert.ToDouble(r2[0, 1]));

                    if (r2[0, 2] != DBNull.Value && r2[0, 3] != DBNull.Value)
                        totalKVAh += (Convert.ToDouble(r2[0, 2]) - Convert.ToDouble(r2[0, 3]));
                }

                string q3 =
                    "SELECT MAX(kW_Demand) as peakKW FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";
                commonStore.Query(q3, out var c3, out var r3);

                if (r3 != null && r3.GetLength(0) > 0 && r3[0, 0] != DBNull.Value)
                    peakDemandKW = Math.Max(peakDemandKW, Convert.ToDouble(r3[0, 0]));

                string q4 =
                    "SELECT AVG(Demand_PF) as avgPF FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";
                commonStore.Query(q4, out var c4, out var r4);

                if (r4 != null && r4.GetLength(0) > 0 && r4[0, 0] != DBNull.Value)
                {
                    double pfVal = Convert.ToDouble(r4[0, 0]);
                    // Normalize: if PF > 1, assume it's in 0-100 scale, convert to 0-1
                    if (pfVal > 1.0)
                        pfVal = pfVal / 100.0;
                    avgPowerFactor += pfVal;
                    deviceCount++;
                }
            }

            if (deviceCount > 0)
                avgPowerFactor /= deviceCount;
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "KPI error: " + ex.Message);
        }

        return new
        {
            totalEnergy = Math.Round(totalKWh, 2),
            peakDemand = Math.Round(peakDemandKW, 2),
            avgPowerFactor = Math.Round(avgPowerFactor, 3),
            reactiveEnergy = Math.Round(totalKVARh, 2),
            apparentEnergy = Math.Round(totalKVAh, 2)
        };
    }

    // Trend (single device) — keep device2 arrays for JS stability (all zeros)
    private object GetEnergyTrendDataSingle(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var device1KWh = new List<double>();
        var device2KWh = new List<double>();
        var device1KVARh = new List<double>();
        var device2KVARh = new List<double>();

        try
        {
            string dev = deviceNames[0].Replace("'", "''");
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            string query =
                "SELECT Timestamp, Total_kWh_Net, Total_kVARh_Net " +
                "FROM " + ENERGY_LOG_TABLE + " " +
                "WHERE DeviceName = '" + dev + "' " +
                "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" " +
                "ORDER BY Timestamp ASC";

            commonStore.Query(query, out var cols, out var results);

            
            if (results == null || results.GetLength(0) == 0)
                return new { timestamps, device1KWh, device2KWh, device1KVARh, device2KVARh };

            var slotAgg = new Dictionary<string, (double sumKWh, double sumKVARh, int count)>();
            var slotOrder = new SortedDictionary<DateTime, string>();

            for (int i = 0; i < results.GetLength(0); i++)
            {
                DateTime ts;
                object tsObj = results[i, 0];

                if (tsObj is DateTime dt) ts = dt;
                else
                {
                    string tsStr = tsObj?.ToString() ?? "";
                    if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                        !DateTime.TryParse(tsStr, out ts))
                        continue;
                }

                string slotStr;
                DateTime slotKey;

                if (daysInRange <= 1)
                {
                    slotStr = ts.ToString("HH:mm", CultureInfo.InvariantCulture);
                    slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0);
                }
                else
                {
                    slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    slotKey = ts.Date;
                }

                if (!slotAgg.TryGetValue(slotStr, out var a))
                {
                    a = (0.0, 0.0, 0);
                    slotAgg[slotStr] = a;
                    if (!slotOrder.ContainsKey(slotKey))
                        slotOrder[slotKey] = slotStr;
                }

                double kwh = (results[i, 1] != DBNull.Value && results[i, 1] != null) ? Convert.ToDouble(results[i, 1]) : 0.0;
                double kvarh = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;

                a.sumKWh += kwh;
                a.sumKVARh += kvarh;
                a.count += 1;
                slotAgg[slotStr] = a;
            }

            foreach (var kvp in slotOrder)
            {
                string slot = kvp.Value;
                timestamps.Add(slot);

                if (slotAgg.TryGetValue(slot, out var a) && a.count > 0)
                {
                    device1KWh.Add(a.sumKWh / a.count);
                    device1KVARh.Add(a.sumKVARh / a.count);
                }
                else
                {
                    device1KWh.Add(0);
                    device1KVARh.Add(0);
                }

                device2KWh.Add(0);
                device2KVARh.Add(0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Trend error: " + ex.Message);
        }

        return new { timestamps, device1KWh, device2KWh, device1KVARh, device2KVARh };
    }

    private object GetDeviceEnergyDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var devices = new List<object>();

        try
        {
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            foreach (var dn in deviceNames)
            {
                string escaped = dn.Replace("'", "''");

                string query =
                    "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal " +
                    "FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";

                commonStore.Query(query, out var columns, out var results);

                double totalKWh = 0;
                if (results != null && results.GetLength(0) > 0 &&
                    results[0, 0] != DBNull.Value && results[0, 1] != DBNull.Value)
                    totalKWh = Convert.ToDouble(results[0, 0]) - Convert.ToDouble(results[0, 1]);

                devices.Add(new { name = dn, value = Math.Round(totalKWh, 2) });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Distribution error: " + ex.Message);
        }

        return new { devices };
    }

    private object GetDemandProfileDataSingle(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var timestamps = new List<string>();
        var kWDemand = new List<double>();
        var kVARDemand = new List<double>();
        var kVADemand = new List<double>();
        var projectedKWDemand = new List<double>();

        try
        {
            string dev = deviceNames[0].Replace("'", "''");
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            string query =
                "SELECT Timestamp, kW_Demand, kVAR_Demand, kVA_Demand, Projected_kW_Demand " +
                "FROM " + ENERGY_LOG_TABLE + " " +
                "WHERE DeviceName = '" + dev + "' " +
                "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" " +
                "ORDER BY Timestamp";

            commonStore.Query(query, out var columns, out var results);

            if (results != null && results.GetLength(0) > 0)
            {
                int sampleRate = Math.Max(1, results.GetLength(0) / 20);
                for (int i = 0; i < results.GetLength(0); i += sampleRate)
                {
                    // Extract time portion (HH:MM) from timestamp
                    string tsStr = results[i, 0]?.ToString() ?? "";
                    string timeDisplay = "00:00";
                    
                    if (tsStr.Length >= 5)
                    {
                        // Try to parse as DateTime first
                        if (DateTime.TryParse(tsStr, out DateTime dt))
                            timeDisplay = dt.ToString("HH:mm");
                        else if (tsStr.Contains("T"))
                        {
                            // ISO format: extract time part after "T"
                            int tIndex = tsStr.IndexOf('T');
                            if (tIndex >= 0 && tsStr.Length > tIndex + 5)
                                timeDisplay = tsStr.Substring(tIndex + 1, 5);
                        }
                    }
                    
                    timestamps.Add(timeDisplay);

                    kWDemand.Add(results[i, 1] != DBNull.Value ? Convert.ToDouble(results[i, 1]) : 0);
                    kVARDemand.Add(results[i, 2] != DBNull.Value ? Convert.ToDouble(results[i, 2]) : 0);
                    kVADemand.Add(results[i, 3] != DBNull.Value ? Convert.ToDouble(results[i, 3]) : 0);
                    projectedKWDemand.Add(results[i, 4] != DBNull.Value ? Convert.ToDouble(results[i, 4]) : 0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Demand profile error: " + ex.Message);
        }

        return new { timestamps, kWDemand, kVARDemand, kVADemand, projectedKWDemand };
    }

    private object GetActiveReactiveData(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var labels = new List<string>();
        var activeEnergy = new List<double>();
        var reactiveEnergy = new List<double>();
        var apparentEnergy = new List<double>();

        try
        {
            // ✅ FIX: missing ||
            if (deviceNames == null || deviceNames.Count == 0)
                return new { labels, activeEnergy, reactiveEnergy, apparentEnergy };

            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            var safeNames = deviceNames
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n.Replace("'", "''"))
                .ToList();

            string deviceFilter = string.Join("','", safeNames);

            string query =
                "SELECT DeviceName, " +
                " MAX(Total_kWh_Net) AS MaxKWh, MIN(Total_kWh_Net) AS MinKWh, " +
                " MAX(Total_kVARh_Net) AS MaxKVARh, MIN(Total_kVARh_Net) AS MinKVARh, " +
                " MAX(Total_kVAh) AS MaxKVAh, MIN(Total_kVAh) AS MinKVAh " +
                "FROM " + ENERGY_LOG_TABLE + " " +
                "WHERE DeviceName IN ('" + deviceFilter + "') " +
                "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" " +
                "GROUP BY DeviceName";

            commonStore.Query(query, out var columns, out var results);

            var map = new Dictionary<string, (double dKWh, double dKVARh, double dKVAh)>(StringComparer.Ordinal);

            if (results != null && results.GetLength(0) > 0)
            {
                for (int i = 0; i < results.GetLength(0); i++)
                {
                    string dev = results[i, 0]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(dev)) continue;

                    double maxKWh = (results[i, 1] != DBNull.Value && results[i, 1] != null) ? Convert.ToDouble(results[i, 1]) : 0.0;
                    double minKWh = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;
                    double maxKVARh = (results[i, 3] != DBNull.Value && results[i, 3] != null) ? Convert.ToDouble(results[i, 3]) : 0.0;
                    double minKVARh = (results[i, 4] != DBNull.Value && results[i, 4] != null) ? Convert.ToDouble(results[i, 4]) : 0.0;
                    double maxKVAh = (results[i, 5] != DBNull.Value && results[i, 5] != null) ? Convert.ToDouble(results[i, 5]) : 0.0;
                    double minKVAh = (results[i, 6] != DBNull.Value && results[i, 6] != null) ? Convert.ToDouble(results[i, 6]) : 0.0;

                    map[dev] = (maxKWh - minKWh, maxKVARh - minKVARh, maxKVAh - minKVAh);
                }
            }

            foreach (var dev in deviceNames)
            {
                labels.Add(dev);

                if (!string.IsNullOrEmpty(dev) && map.TryGetValue(dev, out var v))
                {
                    activeEnergy.Add(Math.Round(v.dKWh, 2));
                    reactiveEnergy.Add(Math.Round(v.dKVARh, 2));
                    apparentEnergy.Add(Math.Round(v.dKVAh, 2));
                }
                else
                {
                    activeEnergy.Add(0);
                    reactiveEnergy.Add(0);
                    apparentEnergy.Add(0);
                }
            }

            labels.Add("Total");
            activeEnergy.Add(Math.Round(activeEnergy.Sum(), 2));
            reactiveEnergy.Add(Math.Round(reactiveEnergy.Sum(), 2));
            apparentEnergy.Add(Math.Round(apparentEnergy.Sum(), 2));
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Active/Reactive error: " + ex.Message);
        }

        return new { labels, activeEnergy, reactiveEnergy, apparentEnergy };
    }

    private object GetPowerFactorDataSingle(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var device1PF = new List<double>();
        var device2PF = new List<double>();
        const double DefaultPF = 0.95;

        try
        {
            string dev = deviceNames[0].Replace("'", "''");
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            string query =
                "SELECT Timestamp, Demand_PF " +
                "FROM " + ENERGY_LOG_TABLE + " " +
                "WHERE DeviceName = '" + dev + "' " +
                "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\" " +
                "ORDER BY Timestamp ASC";

            commonStore.Query(query, out var columns, out var results);

            if (results == null || results.GetLength(0) == 0)
            {
                return new
                {
                    timestamps,
                    device1PF,
                    device2PF,
                    targetPF = new List<double>()
                };
            }

            var slotAgg = new Dictionary<string, (double sum, int count)>();
            var slotOrder = new SortedDictionary<DateTime, string>();

            for (int i = 0; i < results.GetLength(0); i++)
            {
                DateTime ts;
                object tsObj = results[i, 0];

                if (tsObj is DateTime dt) ts = dt;
                else
                {
                    string tsStr = tsObj?.ToString() ?? "";
                    if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                        !DateTime.TryParse(tsStr, out ts))
                        continue;
                }

                string slotStr;
                DateTime slotKey;

                if (daysInRange <= 1)
                {
                    slotStr = ts.ToString("HH:mm", CultureInfo.InvariantCulture);
                    slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0);
                }
                else
                {
                    slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    slotKey = ts.Date;
                }

                if (!slotAgg.TryGetValue(slotStr, out var a))
                {
                    a = (0.0, 0);
                    slotAgg[slotStr] = a;
                    if (!slotOrder.ContainsKey(slotKey))
                        slotOrder[slotKey] = slotStr;
                }

                double pf = (results[i, 1] != DBNull.Value && results[i, 1] != null)
                    ? Convert.ToDouble(results[i, 1])
                    : DefaultPF;

                // Normalize: if PF > 1, assume it's in 0-100 scale, convert to 0-1
                if (pf > 1.0)
                    pf = pf / 100.0;

                a.sum += pf;
                a.count += 1;
                slotAgg[slotStr] = a;
            }

            foreach (var kvp in slotOrder)
            {
                string slot = kvp.Value;
                timestamps.Add(slot);

                if (slotAgg.TryGetValue(slot, out var a) && a.count > 0)
                    device1PF.Add(Math.Round(a.sum / a.count, 3));
                else
                    device1PF.Add(DefaultPF);

                device2PF.Add(0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Power factor error: " + ex.Message);
        }

        return new
        {
            timestamps,
            device1PF,
            device2PF,
            targetPF = Enumerable.Repeat(DefaultPF, Math.Max(1, timestamps.Count)).ToList()
        };
    }

    private object GetDeviceStatusDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var labels = new List<string>();
        var status1 = new List<int>();
        var status2 = new List<int>();
        var status3 = new List<int>();
        var status4 = new List<int>();

        try
        {
            string startIso = ToIso(startDate);
            string endIso = ToIso(endDate);

            foreach (var dn in deviceNames)
            {
                labels.Add(dn);
                string escaped = dn.Replace("'", "''");

                string query =
                    "SELECT SUM(Status_1_Count_x1) as Status1, " +
                    "SUM(Status_2_Count_x1) as Status2, " +
                    "SUM(Status_3_Count_x1) as Status3, " +
                    "SUM(Status_4_Count_x1) as Status4 " +
                    "FROM " + ENERGY_LOG_TABLE + " " +
                    "WHERE DeviceName = '" + escaped + "' " +
                    "AND Timestamp >= \"" + startIso + "\" AND Timestamp < \"" + endIso + "\"";

                commonStore.Query(query, out var columns, out var results);

                if (results != null && results.GetLength(0) > 0)
                {
                    status1.Add(results[0, 0] != DBNull.Value ? Convert.ToInt32(results[0, 0]) : 0);
                    status2.Add(results[0, 1] != DBNull.Value ? Convert.ToInt32(results[0, 1]) : 0);
                    status3.Add(results[0, 2] != DBNull.Value ? Convert.ToInt32(results[0, 2]) : 0);
                    status4.Add(results[0, 3] != DBNull.Value ? Convert.ToInt32(results[0, 3]) : 0);
                }
                else
                {
                    status1.Add(0);
                    status2.Add(0);
                    status3.Add(0);
                    status4.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Status distribution error: " + ex.Message);
        }

        return new { labels, status1, status2, status3, status4 };
    }

    // ------------------------------------------------------------
    // JSON WRITE + REFRESH
    // ------------------------------------------------------------
    private void WriteEnergyDashboardJSON(object dashboardData)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dashboardData, options);

            var projectUri = new ResourceUri("%PROJECTDIR%/");
            string baseDirectory = projectUri.Uri;

            string directory = Path.Combine(baseDirectory, OutputFolder);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string filePath = Path.Combine(directory, OutputFile);
            File.WriteAllText(filePath, json);

            Log.Info("Log_Energy", "Dashboard JSON written: " + filePath);
        }
        catch (Exception ex)
        {
            Log.Error("Log_Energy", "Error writing JSON file: " + ex.Message);
        }
    }

    private void WriteEmptyEnergyDashboard()
    {
        var emptyData = new
        {
            orgName = "No Data",
            deviceCount = 0,
            timeRange = "N/A",
            lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            kpis = new { totalEnergy = 0, peakDemand = 0, avgPowerFactor = 0, reactiveEnergy = 0, apparentEnergy = 0 },

            energyTrendChart = new
            {
                timestamps = new List<string>(),
                device1KWh = new List<double>(),
                device2KWh = new List<double>(),
                device1KVARh = new List<double>(),
                device2KVARh = new List<double>()
            },

            deviceEnergyChart = new { devices = new List<object>() },

            demandChart = new
            {
                timestamps = new List<string>(),
                kWDemand = new List<double>(),
                kVARDemand = new List<double>(),
                kVADemand = new List<double>(),
                projectedKWDemand = new List<double>()
            },

            activeReactiveChart = new
            {
                labels = new List<string>(),
                activeEnergy = new List<double>(),
                reactiveEnergy = new List<double>(),
                apparentEnergy = new List<double>()
            },

            powerFactorChart = new
            {
                timestamps = new List<string>(),
                device1PF = new List<double>(),
                device2PF = new List<double>(),
                targetPF = new List<double>()
            },

            statusChart = new
            {
                labels = new List<string>(),
                status1 = new List<int>(),
                status2 = new List<int>(),
                status3 = new List<int>(),
                status4 = new List<int>()
            }
        };

        WriteEnergyDashboardJSON(emptyData);
    }

    private void RefreshEnergyDashboardBrowser()
    {
        try
        {
            // Get or cache the browser
            if (webBrowser == null)
                webBrowser = Owner.Get<WebBrowser>("WebBrowser");

            if (webBrowser != null)
            {
                // Reload forces fresh fetch of JSON file
                webBrowser.Refresh();
                Log.Info("Log_Energy", "Browser reloaded with fresh data");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Log_Energy", "Refresh browser error: " + ex.Message);
        }
    }

    private string GetTimeRangeDescription(int days)
    {
        if (days == 1) return "Last 24 Hours";
        if (days == 7) return "Last 7 Days";
        if (days == 30) return "Last 30 Days";
        if (days <= 0) return "Custom Range";
        return $"Last {days} Days";
    }
}
