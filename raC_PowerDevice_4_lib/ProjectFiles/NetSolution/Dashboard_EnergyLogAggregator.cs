using System;
using System.Collections.Generic;
using System.IO;
using OpcUa = UAManagedCore.OpcUa;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.UI;
using System.Globalization;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
using FTOptix.SerialPort;


public class Dashboard_EnergyLogAggregator : BaseNetLogic
{
    private IUAVariable selectedOrgNodeVar;
    private IUAVariable startDateVar;
    private IUAVariable endDateVar;
    private IUAVariable selectedDaysVar;
    private IUAVariable errorMessageVar;
    private Store commonStore;

    private const string ENERGY_LOG_TABLE = "EnergyLog_AllDevices";
    
    private DateTime timeRangeStartDate = DateTime.Now.AddDays(-30);
    private DateTime timeRangeEndDate = DateTime.Now;
    private int timeRangeDays = 30;

    public override void Start()
    {
        try
        {
            var navigationPanel = Owner.Owner.Owner;
            selectedOrgNodeVar = navigationPanel?.GetVariable("SelectedOrgNodePath");
            
            if (selectedOrgNodeVar == null)
            {
                Log.Warning("Dashboard_EnergyLogAggregator", "SelectedOrgNodePath variable not found");
                selectedOrgNodeVar = InformationModel.MakeVariable("SelectedOrgNodePath", OpcUa.DataTypes.String);
                LogicObject.Add(selectedOrgNodeVar);
                selectedOrgNodeVar.Value = "";
            }
            else
            {
                selectedOrgNodeVar.VariableChange += OnSelectedOrgNodeChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "Error getting SelectedOrgNodePath: " + ex.Message);
        }

        try
        {
            startDateVar = Owner.GetVariable("StartDate");
            endDateVar = Owner.GetVariable("EndDate");
            selectedDaysVar = Owner.GetVariable("SelectedDays");
            errorMessageVar = Owner.GetVariable("ErrorMessage");
            
            if (startDateVar != null && endDateVar != null)
            {
                startDateVar.Value = timeRangeStartDate;
                endDateVar.Value = timeRangeEndDate;
            }
            
            if (selectedDaysVar != null)
                selectedDaysVar.Value = timeRangeDays;
            
            if (errorMessageVar != null)
                errorMessageVar.Value = "";
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "Error getting date variables: " + ex.Message);
        }

        try
        {
            commonStore = Project.Current.GetObject("Model/raC_4_01_raC_Dvc_PM5000_PQEM_Model/raC_4_01_raC_Dvc_PM5000_PQEM_DataStores")
                .Get<Store>("raC_4_01_raC_Dvc_PM5000_PQEM_db_PowerQuality");

            if (commonStore == null)
            {
                Log.Error("Dashboard_EnergyLogAggregator", "Common store not found!");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", "Failed to get common store: " + ex.Message);
            return;
        }

        // Load initial data if org is already selected
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            if (!string.IsNullOrEmpty(selectedPath))
            {
                LoadEnergyForDays(timeRangeDays);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "Error loading initial energy data: " + ex.Message);
        }
    }

    public override void Stop()
    {
        if (selectedOrgNodeVar != null)
        {
            selectedOrgNodeVar.VariableChange -= OnSelectedOrgNodeChanged;
        }
    }

    private void OnSelectedOrgNodeChanged(object sender, VariableChangeEventArgs e)
    {
        LoadEnergyForDays(timeRangeDays);
    }

    [ExportMethod]
    public void LoadEnergy24Hours() { LoadEnergyForDays(1); }

    [ExportMethod]
    public void LoadEnergy7Days() { LoadEnergyForDays(7); }

    [ExportMethod]
    public void LoadEnergy30Days() { LoadEnergyForDays(30); }

    [ExportMethod]
    public void LoadEnergyForDays(int days)
    {
        try
        {
            DateTime endDate = DateTime.Now;
            DateTime startDate = endDate.AddDays(-days);
            
            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            timeRangeDays = days;
            
            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = days;
            
            if (errorMessageVar != null) errorMessageVar.Value = "";
            
            AggregateEnergyData(startDate, endDate, days);
            RefreshEnergyDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", "Error loading energy data: " + ex.Message);
        }
    }

    [ExportMethod]
    public void LoadEnergyCustomRange(DateTime startDate, DateTime endDate)
    {
        try
        {
            if (startDate >= endDate)
            {
                string errorMsg = "Error: Start date must be before end date";
                Log.Warning("Dashboard_EnergyLogAggregator", errorMsg);
                
                // Display error message to user
                if (errorMessageVar != null)
                    errorMessageVar.Value = errorMsg;
                
                // Load blank data to clear dashboard
                WriteEmptyEnergyDashboard();
                RefreshEnergyDashboardBrowser();
                return;
            }
            
            timeRangeStartDate = startDate;
            timeRangeEndDate = endDate;
            
            if (startDateVar != null) startDateVar.Value = startDate;
            if (endDateVar != null) endDateVar.Value = endDate;
            if (selectedDaysVar != null) selectedDaysVar.Value = -1;
            
            // Clear any error messages
            if (errorMessageVar != null) errorMessageVar.Value = "";
            
            int days = (int)Math.Round((endDate - startDate).TotalDays);
            timeRangeDays = days;
            AggregateEnergyData(startDate, endDate, days);
            RefreshEnergyDashboardBrowser();
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", "Error loading custom range: " + ex.Message);
        }
    }

    private void AggregateEnergyData(DateTime startDate, DateTime endDate, int daysInRange)
    {
        try
        {
            string selectedPath = selectedOrgNodeVar?.Value ?? "";
            var deviceNames = GetDeviceNamesFromOrgNode(selectedPath);
            
            if (deviceNames.Count == 0)
            {
                WriteEmptyEnergyDashboard();
                return;
            }

            var dashboardData = new
            {
                orgName = StripModelPrefix(selectedPath),
                deviceCount = deviceNames.Count,
                timeRange = GetTimeRangeDescription(daysInRange),
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                kpis = CalculateEnergyKPIs(deviceNames, startDate, endDate),
                energyTrendChart = GetEnergyTrendData(deviceNames, startDate, endDate, daysInRange),
                deviceEnergyChart = GetDeviceEnergyDistribution(deviceNames, startDate, endDate),
                demandChart = GetDemandProfileData(deviceNames, startDate, endDate, daysInRange),
                activeReactiveChart = GetActiveReactiveData(deviceNames, startDate, endDate),
                powerFactorChart = GetPowerFactorData(deviceNames, startDate, endDate, daysInRange),
                statusChart = GetDeviceStatusDistribution(deviceNames, startDate, endDate)
            };

            WriteEnergyDashboardJSON(dashboardData);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", "Error aggregating: " + ex.Message);
            WriteEmptyEnergyDashboard();
        }
    }

    private string StripModelPrefix(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return "";

        const string prefix = "Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel/";
        if (fullPath.StartsWith(prefix))
            return fullPath.Substring(prefix.Length);

        return fullPath;
    }

    private object CalculateEnergyKPIs(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        double totalKWh = 0, peakDemandKW = 0, avgPowerFactor = 0, totalKVARh = 0, totalKVAh = 0;
        int deviceCount = 0;

        try
        {
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");
            
            foreach (var deviceName in deviceNames)
            {
                string escapedDeviceName = deviceName.Replace("'", "''");
                
                // Total energy
                string query1 = "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal " +
                               "FROM " + ENERGY_LOG_TABLE + " " +
                               "WHERE DeviceName = '" + escapedDeviceName + "' " +
                               "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                               
                object[,] results1;
                string[] columns1;
                commonStore.Query(query1, out columns1, out results1);
                
                if (results1 != null && results1.GetLength(0) > 0 && results1[0, 0] != DBNull.Value && results1[0, 1] != DBNull.Value)
                {
                    double maxVal = Convert.ToDouble(results1[0, 0]);
                    double minVal = Convert.ToDouble(results1[0, 1]);
                    totalKWh += (maxVal - minVal);
                }

                // Reactive/Apparent energy
                string query2 = "SELECT MAX(Total_kVARh_Net) as MaxKVARh, MIN(Total_kVARh_Net) as MinKVARh, " +
                               "MAX(Total_kVAh) as MaxKVAh, MIN(Total_kVAh) as MinKVAh " +
                               "FROM " + ENERGY_LOG_TABLE + " " +
                               "WHERE DeviceName = '" + escapedDeviceName + "' " +
                               "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                               
                object[,] results2;
                string[] columns2;
                commonStore.Query(query2, out columns2, out results2);
                
                if (results2 != null && results2.GetLength(0) > 0)
                {
                    if (results2[0, 0] != DBNull.Value && results2[0, 1] != DBNull.Value)
                        totalKVARh += (Convert.ToDouble(results2[0, 0]) - Convert.ToDouble(results2[0, 1]));
                    if (results2[0, 2] != DBNull.Value && results2[0, 3] != DBNull.Value)
                        totalKVAh += (Convert.ToDouble(results2[0, 2]) - Convert.ToDouble(results2[0, 3]));
                }

                // Peak demand
                string query3 = "SELECT MAX(kW_Demand) as peakKW FROM " + ENERGY_LOG_TABLE + " " +
                               "WHERE DeviceName = '" + escapedDeviceName + "' " +
                               "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                               
                object[,] results3;
                string[] columns3;
                commonStore.Query(query3, out columns3, out results3);
                
                if (results3 != null && results3.GetLength(0) > 0 && results3[0, 0] != DBNull.Value)
                {
                    double peak = Convert.ToDouble(results3[0, 0]);
                    if (peak > peakDemandKW) peakDemandKW = peak;
                }

                // Power factor
                string query4 = "SELECT AVG(Demand_PF) as avgPF FROM " + ENERGY_LOG_TABLE + " " +
                               "WHERE DeviceName = '" + escapedDeviceName + "' " +
                               "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                               
                object[,] results4;
                string[] columns4;
                commonStore.Query(query4, out columns4, out results4);
                
                if (results4 != null && results4.GetLength(0) > 0 && results4[0, 0] != DBNull.Value)
                {
                    avgPowerFactor += Convert.ToDouble(results4[0, 0]);
                    deviceCount++;
                }
            }

            if (deviceCount > 0) avgPowerFactor /= deviceCount;
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "KPI error: " + ex.Message);
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

    


private object GetEnergyTrendData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
{
    var timestamps = new List<string>();
    var deviceKWhData = new Dictionary<string, List<double>>(); // Key: device name, Value: kWh list
    var deviceKVArhData = new Dictionary<string, List<double>>(); // Key: device name, Value: kVARh list

    try
    {
        if (deviceNames == null || deviceNames.Count == 0)
        {
            var emptyResult = new Dictionary<string, object> { { "timestamps", timestamps } };
            return emptyResult;
        }

        // Initialize data lists for each device
        foreach (var device in deviceNames)
        {
            deviceKWhData[device] = new List<double>();
            deviceKVArhData[device] = new List<double>();
        }

        string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string endIso   = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        string deviceFilter = string.Join("','", deviceNames.Select(n => (n ?? "").Replace("'", "''")));

        string query =
            "SELECT Timestamp, DeviceName, Total_kWh_Net, Total_kVARh_Net " +
            "FROM " + ENERGY_LOG_TABLE + " " +
            "WHERE DeviceName IN ('" + deviceFilter + "') " +
            "AND Timestamp >= '" + startIso + "' AND Timestamp < '" + endIso + "' " +
            "ORDER BY Timestamp ASC, DeviceName ASC";

        object[,] results;
        string[] columns;
        commonStore.Query(query, out columns, out results);

        if (results == null || results.GetLength(0) == 0)
        {
            var emptyResult = new Dictionary<string, object> { { "timestamps", timestamps } };
            foreach (var device in deviceNames)
            {
                emptyResult[device + "_kWh"] = deviceKWhData[device];
                emptyResult[device + "_kVARh"] = deviceKVArhData[device];
            }
            return emptyResult;
        }

        
        var slotAgg = new Dictionary<string, Dictionary<string, (double minKWh, double maxKWh, double minKVARh, double maxKVARh)>>();
        var slotOrder = new SortedDictionary<DateTime, string>(); // keep stable chronological order

        for (int i = 0; i < results.GetLength(0); i++)
        {
            // Columns:
            // 0 -> Timestamp
            // 1 -> DeviceName
            // 2 -> kWh_Net
            // 3 -> kVARh_Net

            string dev = results[i, 1]?.ToString() ?? "";
            if (string.IsNullOrEmpty(dev))
                continue;

            // Convert timestamp safely
            DateTime ts;
            object tsObj = results[i, 0];

            if (tsObj is DateTime dt)
            {
                ts = dt;
            }
            else
            {
                // Optix can sometimes return string representations; try parse robustly.
                // Prefer ISO8601; fallback to general parsing.
                var tsStr = tsObj?.ToString() ?? "";
                if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                    !DateTime.TryParse(tsStr, out ts))
                {
                    continue;
                }
            }

            // Build timeslot
            string slotStr;
            DateTime slotKey; // for ordering

            if (daysInRange <= 1)
            {
                // Hourly resolution for 24-hour view (groups 15-min readings into hourly buckets)
                slotStr = ts.ToString("HH:00", CultureInfo.InvariantCulture);
                slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0);
            }
            else
            {
                // Day resolution: yyyy-MM-dd
                slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                slotKey = ts.Date;
            }

            if (!slotAgg.TryGetValue(slotStr, out var devMap))
            {
                devMap = new Dictionary<string, (double minKWh, double maxKWh, double minKVARh, double maxKVARh)>();
                slotAgg[slotStr] = devMap;

                // Keep the first occurrence ordering key; avoids collisions safely
                if (!slotOrder.ContainsKey(slotKey))
                    slotOrder[slotKey] = slotStr;
            }

            double kwh = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;
            double kvarh = (results[i, 3] != DBNull.Value && results[i, 3] != null) ? Convert.ToDouble(results[i, 3]) : 0.0;

            if (!devMap.TryGetValue(dev, out var agg))
                agg = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);

            // Track MIN and MAX for cumulative energy values
            agg.minKWh = Math.Min(agg.minKWh, kwh);
            agg.maxKWh = Math.Max(agg.maxKWh, kwh);
            agg.minKVARh = Math.Min(agg.minKVARh, kvarh);
            agg.maxKVARh = Math.Max(agg.maxKVARh, kvarh);

            devMap[dev] = agg;
        }

        // Emit timestamps in chronological order for ALL devices
        foreach (var kvp in slotOrder)
        {
            string slot = kvp.Value;
            timestamps.Add(slot);

            // Process each device (in original order)
            foreach (var deviceName in deviceNames)
            {
                if (slotAgg.TryGetValue(slot, out var devMap) && devMap.TryGetValue(deviceName, out var agg) && agg.maxKWh != double.MinValue)
                {
                    // Energy consumed = MAX - MIN (delta for that time slot)
                    double deltaKWh = agg.maxKWh - agg.minKWh;
                    double deltaKVARh = agg.maxKVARh - agg.minKVARh;
                    
                    deviceKWhData[deviceName].Add(Math.Round(deltaKWh, 2));
                    deviceKVArhData[deviceName].Add(Math.Round(deltaKVARh, 2));
                }
                else
                {
                    deviceKWhData[deviceName].Add(0);
                    deviceKVArhData[deviceName].Add(0);
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning("Dashboard_EnergyLogAggregator", "Trend error: " + ex.Message);
    }

    // Build result object dynamically with actual device names
    var result = new Dictionary<string, object> { { "timestamps", timestamps } };
    foreach (var device in deviceNames)
    {
        result[device + "_kWh"] = deviceKWhData[device];
        result[device + "_kVARh"] = deviceKVArhData[device];
    }

    return result;
}


    private object GetDeviceEnergyDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var devices = new List<object>();

        try
        {
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");
            
            foreach (var deviceName in deviceNames)
            {
                string escapedDeviceName = deviceName.Replace("'", "''");
                string query = "SELECT MAX(Total_kWh_Net) as MaxVal, MIN(Total_kWh_Net) as MinVal " +
                              "FROM " + ENERGY_LOG_TABLE + " " +
                              "WHERE DeviceName = '" + escapedDeviceName + "' " +
                              "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                
                object[,] results;
                string[] columns;
                commonStore.Query(query, out columns, out results);
                
                double totalKWh = 0;
                if (results != null && results.GetLength(0) > 0 && results[0, 0] != DBNull.Value && results[0, 1] != DBNull.Value)
                {
                    totalKWh = Convert.ToDouble(results[0, 0]) - Convert.ToDouble(results[0, 1]);
                }
                    
                devices.Add(new { name = deviceName, value = Math.Round(totalKWh, 2) });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "Distribution error: " + ex.Message);
        }

        return new { devices };
    }

    private object GetDemandProfileData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
    {
        var timestamps = new List<string>();
        var kWDemand = new List<double>();
        var kVARDemand = new List<double>();
        var kVADemand = new List<double>();
        var projectedKWDemand = new List<double>();

        try
        {
            string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            string endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

            // Query all demand readings (summed across all devices per timestamp)
            string query =
                "SELECT Timestamp, SUM(kW_Demand) as TotalKW, SUM(kVAR_Demand) as TotalKVAR, " +
                "SUM(kVA_Demand) as TotalKVA, AVG(Projected_kW_Demand) as AvgProjectedKW " +
                "FROM " + ENERGY_LOG_TABLE + " " +
                "WHERE Timestamp >= '" + startIso + "' AND Timestamp < '" + endIso + "' " +
                "GROUP BY Timestamp " +
                "ORDER BY Timestamp ASC";

            object[,] results;
            string[] columns;
            commonStore.Query(query, out columns, out results);

            if (results == null || results.GetLength(0) == 0)
            {
                return new { timestamps, kWDemand, kVARDemand, kVADemand, projectedKWDemand };
            }

            // Aggregation structure: slot -> (maxKW, maxKVAR, maxKVA, avgProjectedKW)
            var slotAgg = new Dictionary<string, (double maxKW, double maxKVAR, double maxKVA, double sumProjectedKW, int projCount)>();
            var slotOrder = new SortedDictionary<DateTime, string>(); // chronological ordering

            for (int i = 0; i < results.GetLength(0); i++)
            {
                // Columns: 0 Timestamp, 1 TotalKW, 2 TotalKVAR, 3 TotalKVA, 4 AvgProjectedKW

                // Convert timestamp safely
                DateTime ts;
                object tsObj = results[i, 0];

                if (tsObj is DateTime dt)
                {
                    ts = dt;
                }
                else
                {
                    string tsStr = tsObj?.ToString() ?? "";
                    if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                        !DateTime.TryParse(tsStr, out ts))
                    {
                        continue;
                    }
                }

                // Determine time slot
                string slotStr;
                DateTime slotKey;

                if (daysInRange <= 1)
                {
                    // 24 hours: Hourly MAX (HH:00 format)
                    slotStr = ts.ToString("HH:00", CultureInfo.InvariantCulture);
                    slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0);
                }
                else
                {
                    // >1 day: Daily MAX (yyyy-MM-dd format)
                    slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    slotKey = ts.Date;
                }

                double kw = (results[i, 1] != DBNull.Value && results[i, 1] != null) ? Convert.ToDouble(results[i, 1]) : 0.0;
                double kvar = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;
                double kva = (results[i, 3] != DBNull.Value && results[i, 3] != null) ? Convert.ToDouble(results[i, 3]) : 0.0;
                double projectedKw = (results[i, 4] != DBNull.Value && results[i, 4] != null) ? Convert.ToDouble(results[i, 4]) : 0.0;

                if (!slotAgg.TryGetValue(slotStr, out var agg))
                {
                    agg = (0.0, 0.0, 0.0, 0.0, 0);

                    // Keep first occurrence for ordering
                    if (!slotOrder.ContainsKey(slotKey))
                        slotOrder[slotKey] = slotStr;
                }

                // Track MAX for instantaneous demand (kW, kVAR, kVA are instantaneous, not cumulative)
                agg.maxKW = Math.Max(agg.maxKW, kw);
                agg.maxKVAR = Math.Max(agg.maxKVAR, kvar);
                agg.maxKVA = Math.Max(agg.maxKVA, kva);
                agg.sumProjectedKW += projectedKw;
                agg.projCount += 1;

                slotAgg[slotStr] = agg;
            }

            // Emit data in chronological order
            foreach (var kvp in slotOrder)
            {
                string slot = kvp.Value;
                timestamps.Add(slot);

                if (slotAgg.TryGetValue(slot, out var agg))
                {
                    kWDemand.Add(Math.Round(agg.maxKW, 2));
                    kVARDemand.Add(Math.Round(agg.maxKVAR, 2));
                    kVADemand.Add(Math.Round(agg.maxKVA, 2));
                    
                    double avgProjected = agg.projCount > 0 ? agg.sumProjectedKW / agg.projCount : 0.0;
                    projectedKWDemand.Add(Math.Round(avgProjected, 2));
                }
                else
                {
                    kWDemand.Add(0);
                    kVARDemand.Add(0);
                    kVADemand.Add(0);
                    projectedKWDemand.Add(0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", "Demand profile error: " + ex.Message);
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
        if (deviceNames == null || deviceNames.Count == 0)
            return new { labels, activeEnergy, reactiveEnergy, apparentEnergy };

        string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string endIso   = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        // Escape device names for SQL string literals
        var safeNames = deviceNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n.Replace("'", "''"))
            .ToList();

        if (safeNames.Count == 0)
            return new { labels, activeEnergy, reactiveEnergy, apparentEnergy };

        string deviceFilter = string.Join("','", safeNames);

        string query =
            "SELECT DeviceName, " +
            " MAX(Total_kWh_Net)   AS MaxKWh,   MIN(Total_kWh_Net)   AS MinKWh, " +
            " MAX(Total_kVARh_Net) AS MaxKVARh, MIN(Total_kVARh_Net) AS MinKVARh, " +
            " MAX(Total_kVAh)      AS MaxKVAh,  MIN(Total_kVAh)      AS MinKVAh " +
            "FROM " + ENERGY_LOG_TABLE + " " +
            "WHERE DeviceName IN ('" + deviceFilter + "') " +
            "AND Timestamp >= '" + startIso + "' AND Timestamp < '" + endIso + "' " +
            "GROUP BY DeviceName";

        object[,] results;
        string[] columns;

        commonStore.Query(query, out columns, out results);

        // Build a map: device -> computed deltas
        var map = new Dictionary<string, (double dKWh, double dKVARh, double dKVAh)>(StringComparer.Ordinal);

        if (results != null && results.GetLength(0) > 0)
        {
            for (int i = 0; i < results.GetLength(0); i++)
            {
                string dev = results[i, 0]?.ToString() ?? "";
                if (string.IsNullOrEmpty(dev))
                    continue;

                double maxKWh   = (results[i, 1] != DBNull.Value && results[i, 1] != null) ? Convert.ToDouble(results[i, 1]) : 0.0;
                double minKWh   = (results[i, 2] != DBNull.Value && results[i, 2] != null) ? Convert.ToDouble(results[i, 2]) : 0.0;

                double maxKVARh = (results[i, 3] != DBNull.Value && results[i, 3] != null) ? Convert.ToDouble(results[i, 3]) : 0.0;
                double minKVARh = (results[i, 4] != DBNull.Value && results[i, 4] != null) ? Convert.ToDouble(results[i, 4]) : 0.0;

                double maxKVAh  = (results[i, 5] != DBNull.Value && results[i, 5] != null) ? Convert.ToDouble(results[i, 5]) : 0.0;
                double minKVAh  = (results[i, 6] != DBNull.Value && results[i, 6] != null) ? Convert.ToDouble(results[i, 6]) : 0.0;

                // ✅ Compute deltas in C#
                double dKWh   = maxKWh   - minKWh;
                double dKVARh = maxKVARh - minKVARh;
                double dKVAh  = maxKVAh  - minKVAh;

                map[dev] = (dKWh, dKVARh, dKVAh);
            }
        }

        // Output in original device order
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

        // Totals
        labels.Add("Total");
        activeEnergy.Add(Math.Round(activeEnergy.Sum(), 2));
        reactiveEnergy.Add(Math.Round(reactiveEnergy.Sum(), 2));
        apparentEnergy.Add(Math.Round(apparentEnergy.Sum(), 2));
    }
    catch (Exception ex)
    {
        Log.Warning("Dashboard_EnergyLogAggregator", "Active/Reactive error: " + ex.Message);
    }

    return new { labels, activeEnergy, reactiveEnergy, apparentEnergy };
}

    
    
private object GetPowerFactorData(List<string> deviceNames, DateTime startDate, DateTime endDate, int daysInRange)
{
    var timestamps = new List<string>();
    var devicePFData = new Dictionary<string, List<double>>(); // Key: actual device name, Value: PF list

    try
    {
        if (deviceNames == null || deviceNames.Count == 0)
        {
            var emptyResult = new Dictionary<string, object> { { "timestamps", timestamps }, { "targetPF", new List<double>() } };
            return emptyResult;
        }

        // Initialize PF data lists for each device
        foreach (var device in deviceNames)
        {
            devicePFData[device] = new List<double>();
        }

        string startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        // Escape device names
        var safeNames = deviceNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n.Replace("'", "''"))
            .ToList();

        if (safeNames.Count == 0)
        {
            var emptyResult = new Dictionary<string, object> { { "timestamps", timestamps }, { "targetPF", new List<double>() } };
            return emptyResult;
        }

        string deviceFilter = string.Join("','", safeNames);

        string query =
            "SELECT Timestamp, DeviceName, Demand_PF " +
            "FROM " + ENERGY_LOG_TABLE + " " +
            "WHERE DeviceName IN ('" + deviceFilter + "') " +
            "AND Timestamp >= '" + startIso + "' AND Timestamp < '" + endIso + "' " +
            "ORDER BY Timestamp ASC, DeviceName ASC";

        object[,] results;
        string[] columns;
        commonStore.Query(query, out columns, out results);

        if (results == null || results.GetLength(0) == 0)
        {
            // No data -> return empty lists
            var emptyResult = new Dictionary<string, object> { { "timestamps", timestamps }, { "targetPF", new List<double>() } };
            foreach (var device in deviceNames)
            {
                emptyResult[device] = devicePFData[device];
            }
            return emptyResult;
        }

        // Aggregation: slot -> device -> (sum, count)
        var slotAgg = new Dictionary<string, Dictionary<string, (double sum, int count)>>();
        var slotOrder = new SortedDictionary<DateTime, string>(); // stable chronological slot order

        for (int i = 0; i < results.GetLength(0); i++)
        {
            // Columns: 0 Timestamp, 1 DeviceName, 2 Demand_PF
            string dev = results[i, 1]?.ToString() ?? "";
            if (string.IsNullOrEmpty(dev))
                continue;

            // Convert timestamp safely (Optix may return DateTime or string)
            DateTime ts;
            object tsObj = results[i, 0];

            if (tsObj is DateTime dt)
            {
                ts = dt;
            }
            else
            {
                string tsStr = tsObj?.ToString() ?? "";
                if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts) &&
                    !DateTime.TryParse(tsStr, out ts))
                {
                    continue;
                }
            }

            // Determine slot
            string slotStr;
            DateTime slotKey;

            if (daysInRange <= 1)
            {
                // Minute resolution
                slotStr = ts.ToString("HH:mm", CultureInfo.InvariantCulture);
                slotKey = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0);
            }
            else
            {
                // Day resolution
                slotStr = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                slotKey = ts.Date;
            }

            if (!slotAgg.TryGetValue(slotStr, out var devMap))
            {
                devMap = new Dictionary<string, (double sum, int count)>();
                slotAgg[slotStr] = devMap;

                if (!slotOrder.ContainsKey(slotKey))
                    slotOrder[slotKey] = slotStr;
            }

            // PF value (already in 0-100 scale from database)
            double pf = (results[i, 2] != DBNull.Value && results[i, 2] != null)
                ? Convert.ToDouble(results[i, 2])
                : 0.0;

            if (pf <= 0) continue; // Skip invalid/zero values

            if (!devMap.TryGetValue(dev, out var a))
                a = (0.0, 0);

            a.sum += pf;
            a.count += 1;
            devMap[dev] = a;
        }

        // Emit arrays in chronological order for ALL devices
        foreach (var kvp in slotOrder)
        {
            string slot = kvp.Value;
            timestamps.Add(slot);

            // Process each device (in original order)
            foreach (var deviceName in deviceNames)
            {
                if (slotAgg.TryGetValue(slot, out var devMap) &&
                    devMap.TryGetValue(deviceName, out var agg) &&
                    agg.count > 0)
                {
                    devicePFData[deviceName].Add(Math.Round(agg.sum / agg.count, 1)); // PF is 0-100, round to 1 decimal
                }
                else
                {
                    devicePFData[deviceName].Add(0); // No data -> 0
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning("Dashboard_EnergyLogAggregator", "Power factor error: " + ex.Message);
    }

    // Build result object dynamically with actual device names
    var result = new Dictionary<string, object> { { "timestamps", timestamps } };
    foreach (var device in deviceNames)
    {
        result[device] = devicePFData[device];
    }
    result["targetPF"] = new List<double>();

    return result;
}

    
    private object GetDeviceStatusDistribution(List<string> deviceNames, DateTime startDate, DateTime endDate)
    {
        var labels = new List<string>();
        var status1 = new List<long>();
        var status2 = new List<long>();
        var status3 = new List<long>();
        var status4 = new List<long>();
        
        try
        {
            string startDateStr = startDate.ToString("yyyy-MM-ddTHH:mm:ss");
            string endDateStr = endDate.ToString("yyyy-MM-ddTHH:mm:ss");
            
            foreach (var deviceName in deviceNames)
            {
                labels.Add(deviceName);
                string escapedDeviceName = deviceName.Replace("'", "''");

                // Get MAX values (odometer counters keep incrementing, so MAX is the final count)
                // Formula: (Status_X_Count_xM * 1,000,000) + Status_X_Count_x1
                string query = "SELECT " +
                              "MAX(Status_1_Count_xM) as S1_xM, MAX(Status_1_Count_x1) as S1_x1, " +
                              "MAX(Status_2_Count_xM) as S2_xM, MAX(Status_2_Count_x1) as S2_x1, " +
                              "MAX(Status_3_Count_xM) as S3_xM, MAX(Status_3_Count_x1) as S3_x1, " +
                              "MAX(Status_4_Count_xM) as S4_xM, MAX(Status_4_Count_x1) as S4_x1 " +
                              "FROM " + ENERGY_LOG_TABLE + " " +
                              "WHERE DeviceName = '" + escapedDeviceName + "' " +
                              "AND Timestamp >= '" + startDateStr + "' AND Timestamp <= '" + endDateStr + "'";
                
                object[,] results;
                string[] columns;
                commonStore.Query(query, out columns, out results);
                
                if (results != null && results.GetLength(0) > 0)
                {
                    // Status 1: (xM * 1,000,000) + x1
                    long s1_xM = (results[0, 0] != DBNull.Value && results[0, 0] != null) ? Convert.ToInt64(results[0, 0]) : 0;
                    long s1_x1 = (results[0, 1] != DBNull.Value && results[0, 1] != null) ? Convert.ToInt64(results[0, 1]) : 0;
                    status1.Add((s1_xM * 1000000) + s1_x1);
                    
                    // Status 2
                    long s2_xM = (results[0, 2] != DBNull.Value && results[0, 2] != null) ? Convert.ToInt64(results[0, 2]) : 0;
                    long s2_x1 = (results[0, 3] != DBNull.Value && results[0, 3] != null) ? Convert.ToInt64(results[0, 3]) : 0;
                    status2.Add((s2_xM * 1000000) + s2_x1);
                    
                    // Status 3
                    long s3_xM = (results[0, 4] != DBNull.Value && results[0, 4] != null) ? Convert.ToInt64(results[0, 4]) : 0;
                    long s3_x1 = (results[0, 5] != DBNull.Value && results[0, 5] != null) ? Convert.ToInt64(results[0, 5]) : 0;
                    status3.Add((s3_xM * 1000000) + s3_x1);
                    
                    // Status 4
                    long s4_xM = (results[0, 6] != DBNull.Value && results[0, 6] != null) ? Convert.ToInt64(results[0, 6]) : 0;
                    long s4_x1 = (results[0, 7] != DBNull.Value && results[0, 7] != null) ? Convert.ToInt64(results[0, 7]) : 0;
                    status4.Add((s4_xM * 1000000) + s4_x1);
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
            Log.Warning("Dashboard_EnergyLogAggregator", "Status distribution error: " + ex.Message);
        }

        return new { labels, status1, status2, status3, status4 };
    }

    private List<string> GetDeviceNamesFromOrgNode(string orgNodePath)
    {
        var devices = new List<string>();

        try
        {
            if (string.IsNullOrEmpty(orgNodePath))
            {
                return devices;
            }

            // Navigate to specific org node
            var orgNode = Project.Current.Get(orgNodePath);
            if (orgNode == null)
            {
                Log.Warning("Dashboard_EnergyLogAggregator", $"Org node not found: {orgNodePath}");
                return devices;
            }

            // Collect device nodes under this org node
            var deviceNodes = new List<IUANode>();
            CollectDeviceNodes(orgNode, deviceNodes);

            // Extract device names
            foreach (var deviceNode in deviceNodes)
            {
                if (deviceNode != null && !devices.Contains(deviceNode.BrowseName))
                {
                    devices.Add(deviceNode.BrowseName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", $"Error getting device names from org node: {ex.Message}");
        }

        return devices;
    }

    private void CollectDeviceNodes(IUANode node, List<IUANode> devices)
    {
        // Check if this node is a device (has DeviceAlarms and DeviceStatus)
        if (node.GetObject("DeviceAlarms") != null && node.GetObject("DeviceStatus") != null)
        {
            devices.Add(node);
        }

        // Recursively check children
        foreach (var child in node.Children)
        {
            CollectDeviceNodes(child, devices);
        }
    }

    private string GetTimeRangeDescription(int days)
    {
        if (days == 1) return "Last 24 Hours";
        if (days == 7) return "Last 7 Days";
        if (days == 30) return "Last 30 Days";
        return $"Last {days} Days";
    }

    private void WriteEnergyDashboardJSON(object dashboardData)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(dashboardData, options);

            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res/TabCommon_EnergyDashboard");
            string filePath = Path.Combine(directory, "energy-dashboard-data.json");
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", $"Error writing JSON file: {ex.Message}");
        }
    }

    private void WriteEmptyEnergyDashboard()
    {
        try
        {
            var emptyData = new
            {
                orgName = "No Data",
                deviceCount = 0,
                timeRange = "N/A",
                lastUpdate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                kpis = new { totalEnergy = 0, peakDemand = 0, avgPowerFactor = 0, reactiveEnergy = 0, apparentEnergy = 0 },
                energyTrendChart = new { timestamps = new List<string>() },
                deviceEnergyChart = new { devices = new List<object>() },
                demandChart = new { timestamps = new List<string>(), kWDemand = new List<double>(), kVARDemand = new List<double>(), kVADemand = new List<double>(), projectedKWDemand = new List<double>() },
                activeReactiveChart = new { labels = new List<string>(), activeEnergy = new List<double>(), reactiveEnergy = new List<double>(), apparentEnergy = new List<double>() },
                powerFactorChart = new { timestamps = new List<string>(), targetPF = new List<double>() },
                statusChart = new { labels = new List<string>(), status1 = new List<int>(), status2 = new List<int>(), status3 = new List<int>(), status4 = new List<int>() }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(emptyData, options);

            var filePathValue = new ResourceUri("%PROJECTDIR%\\");
            string baseDirectory = filePathValue.Uri;
            string directory = Path.Combine(baseDirectory, "res/TabCommon_EnergyDashboard");
            string filePath = Path.Combine(directory, "energy-dashboard-data.json");
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard_EnergyLogAggregator", $"Error writing empty dashboard: {ex.Message}");
        }
    }

    private void RefreshEnergyDashboardBrowser()
    {
        try
        {
            var webBrowser = Owner.Get<WebBrowser>("wb_EnergyDashboard");            
            
            if (webBrowser != null)
            {
                webBrowser.Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Dashboard_EnergyLogAggregator", $"Error refreshing dashboard browser: {ex.Message}");
        }
    }

}
