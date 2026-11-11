#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.SerialPort;
using FTOptix.Core;
using System.IO;
using System.Linq;
using FTOptix.OPCUAClient;
using System.Xml;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class raC_4_01_raC_Dvc_PM5000_PQEM_Setup : BaseNetLogic
{
    [ExportMethod]
    public void SetupInitialConfiguration()
    {
        try
        {
            var usersFolder = Owner.Get("raC_4_01_raC_Dvc_PM5000_PQEM_Users");
            if (usersFolder == null)
            {
                Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Users folder not found");
                return;
            }

            // Get the RetentivityStorage nodes
            var rsNodes = Owner.Get<RetentivityStorage>("raC_4_01_raC_Dvc_PM5000_PQEM_DataStores/raC_4_01_raC_Dvc_PM5000_PQEM_rs_Users")?.Nodes;
            if (rsNodes == null)
            {
                Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Storage configuration not accessible");
                return;
            }
            var usersNode = rsNodes.Get("users");
            if (usersNode == null)
            {
                Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Users node not found in storage");
                return;
            }
            usersNode.Value = usersFolder.NodeId;

            // Setup Report Schedules based on Dashboard Model availability
            SetupReportSchedulesNode();

            Log.Info("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Initial Setup completed successfully");
        }
        catch (Exception ex)
        {
            Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", $"Error in SetupInitialConfiguration: {ex.Message}");
        }
    }

    private void SetupReportSchedulesNode()
    {
        try
        {
            // Check if Dashboard Model is imported
            var dashboardModel = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model");
            
            if (dashboardModel != null)
            {
                Log.Info("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Configuring report schedules");
                SetupReportSchedulesBinding();
            }
            else
            {
                Log.Info("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Dashboard not imported - removing report schedules");
                RemoveReportSchedulesNode();
            }
        }
        catch (Exception ex)
        {
            Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", $"Error in SetupReportSchedulesNode: {ex.Message}");
        }
    }

    private void SetupReportSchedulesBinding()
    {
        try
        {
            // Get the RetentivityStorage for rs_Users
            var rsUsers = Owner.Get<RetentivityStorage>("raC_4_01_raC_Dvc_PM5000_PQEM_DataStores/raC_4_01_raC_Dvc_PM5000_PQEM_rs_Users");
            if (rsUsers == null)
            {
                Log.Warning("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Storage not accessible");
                return;
            }

            var rsNodes = rsUsers.Nodes;
            if (rsNodes == null)
            {
                Log.Warning("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Storage nodes not accessible");
                return;
            }

            // Check if reportSchedules node exists, if not create it
            var reportSchedulesNode = rsNodes.Get("reportSchedules");
            if (reportSchedulesNode == null)
            {
                reportSchedulesNode = InformationModel.MakeVariable("reportSchedules", OpcUa.DataTypes.NodeId);
                rsNodes.Add(reportSchedulesNode);
            }

            // Get the ReportSchedules folder
            var reportSchedulesFolder = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_ReportSchedules");
            if (reportSchedulesFolder == null)
            {
                Log.Warning("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Report schedules folder not found");
                return;
            }

            // Bind the folder to reportSchedules node
            reportSchedulesNode.Value = reportSchedulesFolder.NodeId;
            Log.Info("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Report schedules configured");
        }
        catch (Exception ex)
        {
            Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", $"Error in SetupReportSchedulesBinding: {ex.Message}");
        }
    }

    private void RemoveReportSchedulesNode()
    {
        try
        {
            // Get the RetentivityStorage for rs_Users
            var rsUsers = Owner.Get<RetentivityStorage>("raC_4_01_raC_Dvc_PM5000_PQEM_DataStores/raC_4_01_raC_Dvc_PM5000_PQEM_rs_Users");
            if (rsUsers == null)
            {
                return;
            }

            var rsNodes = rsUsers.Nodes;
            if (rsNodes == null)
            {
                return;
            }

            // Check if reportSchedules node exists and remove it
            var reportSchedulesNode = rsNodes.Get("reportSchedules");
            if (reportSchedulesNode != null)
            {
                rsNodes.Remove(reportSchedulesNode);
                Log.Info("raC_4_01_raC_Dvc_PM5000_PQEM_Model", "Report schedules node removed");
            }
        }
        catch (Exception ex)
        {
            Log.Error("raC_4_01_raC_Dvc_PM5000_PQEM_Model", $"Error in RemoveReportSchedulesNode: {ex.Message}");
        }
    }

}
