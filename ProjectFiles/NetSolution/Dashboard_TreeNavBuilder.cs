#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.SerialPort;
using FTOptix.Core;
using FTOptix.AuditSigning;
using FTOptix.DataLogger;
#endregion

public class Dashboard_TreeNavBuilder : BaseNetLogic
{
    private const int BaseIndent = 20;
    private ColumnLayout leftLayout;
    private IUANode orgModelRoot;

    public override void Start()
    {
        leftLayout = Owner.Get("treescroll/left") as ColumnLayout;
        if (leftLayout == null)
        {
            Log.Error("TreeNavLogic", "Left panel not found or is not a ColumnLayout.");
            return;
        }

        // Set consistent spacing on the main layout
        leftLayout.VerticalGap = 5;

        ClearContainer(leftLayout);

        orgModelRoot = Project.Current.Get("Model/raC_4_01_raC_Dvc_PM5000_PQEMDashboard_Model/PM5000_PQEM_OrgModel");
        if (orgModelRoot == null)
        {
            Log.Error("TreeNavLogic", "Organization model 'PM5000_PQEM_OrgModel' not found.");
            return;
        }

        BuildTreeLevel(leftLayout, orgModelRoot, depth: 0);
        SelectFirstOrgNodeByDefault(orgModelRoot);
    }

    public override void Stop()
    {
    }

    private void BuildTreeLevel(IUANode container, IUANode currentNode, int depth)
    {
        if (currentNode == null || container == null) return;

        foreach (var child in currentNode.Children)
        {
            if (child.NodeClass != NodeClass.Object) continue;

            string typeName = child.GetType().FullName ?? "";
            bool isOrgNode = typeName.Contains("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType") || 
                           child.BrowseName.Contains("OrgNode") ||
                           HasOrgNodeTypeId(child);
            bool isDevice = typeName.Contains("raC_4_01_raC_Dvc_PM5000_PQEM") && !isOrgNode;

            if (!isOrgNode && !isDevice) continue;

            if (isOrgNode)
                CreateOrgNodeButton(container, child, depth);
            else if (isDevice)
                CreateDeviceButton(container, child, depth);
        }
    }

    private void CreateOrgNodeButton(IUANode container, IUANode orgNode, int depth)
    {
        var orgNavType = Project.Current.Find("raC_4_01_raC_Dvc_PM5000_PQEM_OrgNodeType_NavGraphical");
        if (orgNavType == null)
        {
            Log.Error("TreeNavLogic", "OrgNode nav type 'raC_4_01_raC_Dvc_PM5000_PQEM_OrgNodeType_NavGraphical' not found.");
            return;
        }

        try
        {
            var navButton = InformationModel.MakeObject($"OrgNav_{orgNode.BrowseName}", orgNavType.NodeId);

            var leftMarginVar = navButton.GetVariable("LM");
            if (leftMarginVar != null)
                leftMarginVar.Value = BaseIndent * depth;

            var orgNodePtr = navButton.GetVariable("OrgNode");
            if (orgNodePtr != null)
                orgNodePtr.Value = orgNode.NodeId;
            else
                Log.Warning("TreeNavLogic", $"OrgNode pointer variable not found in button type for '{orgNode.BrowseName}'.");

            container.Add(navButton);

            var childContainer = InformationModel.MakeObject<ColumnLayout>($"Children_{orgNode.BrowseName}");
            childContainer.LeftMargin = 0;
            childContainer.TopMargin = 0;
            childContainer.RightMargin = 0;
            childContainer.BottomMargin = 0;
            childContainer.VerticalGap = 5;
            childContainer.HorizontalAlignment = HorizontalAlignment.Left;
            childContainer.Width = -1;
            childContainer.Visible = true;
            container.Add(childContainer);

            BuildTreeLevel(childContainer, orgNode, depth + 1);

            if (!HasChildren(childContainer))
            {
                var hasChildrenVar = navButton.GetVariable("HasChildren");
                if (hasChildrenVar != null)
                    hasChildrenVar.Value = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("TreeNavLogic", $"Error creating OrgNode button for '{orgNode.BrowseName}': {ex.Message}");
        }
    }

    private bool HasChildren(IUANode container)
    {
        foreach (var child in container.Children)
        {
            if (child.NodeClass == NodeClass.Object)
                return true;
        }
        return false;
    }

    private void CreateDeviceButton(IUANode container, IUANode deviceNode, int depth)
    {
        var deviceNavType = Project.Current.Find("raC_4_01_raC_Dvc_PM5000_PQEM_NavGraphical_Horizontal_ForTree");
        if (deviceNavType == null)
        {
            Log.Error("TreeNavLogic", "Device nav type 'raC_4_01_raC_Dvc_PM5000_PQEM_NavGraphical_Horizontal_ForTree' not found.");
            return;
        }

        try
        {
            var deviceButton = InformationModel.MakeObject($"DevNav_{deviceNode.BrowseName}", deviceNavType.NodeId);

            var leftMarginVar = deviceButton.GetVariable("LM");
            if (leftMarginVar != null)
                leftMarginVar.Value = BaseIndent * depth;

            var refTagPtr = deviceButton.GetVariable("Ref_Tag");
            if (refTagPtr != null)
                refTagPtr.Value = deviceNode.NodeId;
            else
                Log.Warning("TreeNavLogic", $"Ref_Tag pointer variable not found in device button type for '{deviceNode.BrowseName}'.");

            deviceButton.GetVariable("Cfg_Label").Value = deviceNode.BrowseName;
            container.Add(deviceButton);
        }
        catch (Exception ex)
        {
            Log.Error("TreeNavLogic", $"Error creating Device button for '{deviceNode.BrowseName}': {ex.Message}");
        }
    }

    private void ClearContainer(IUANode container)
    {
        if (container == null) return;

        var childrenToRemove = new System.Collections.Generic.List<IUANode>();
        foreach (var child in container.Children)
        {
            childrenToRemove.Add(child);
        }

        foreach (var child in childrenToRemove)
        {
            container.Remove(child);
        }
    }

    private bool HasOrgNodeTypeId(IUANode node)
    {
        try
        {
            var typeDefId = node.GetVariable("TypeDefinition")?.Value;
            if (typeDefId != null)
            {
                var typeDef = InformationModel.Get(typeDefId);
                if (typeDef != null && typeDef.BrowseName.Contains("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType"))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private void SelectFirstOrgNodeByDefault(IUANode orgModelRoot)
    {
        IUANode firstOrgNode = null;
        foreach (var child in orgModelRoot.Children)
        {
            if (child.NodeClass != NodeClass.Object) continue;

            string typeName = child.GetType().FullName ?? "";
            bool isOrgNode = typeName.Contains("raC_4_01_raC_Dvc_PM5000_PQEMDashboard_OrgNodeType") || 
                           child.BrowseName.Contains("OrgNode") ||
                           HasOrgNodeTypeId(child);

            if (isOrgNode)
            {
                firstOrgNode = child;
                break;
            }
        }

        if (firstOrgNode == null)
        {
            Log.Warning("TreeNavLogic", "No default organization node found to select.");
            return;
        }

        try
        {
            string orgNodePath = GetNodePath(firstOrgNode);
            var navigationPanel = Owner.Owner.Get("hl_Dashboard/NavigationPanelright");
            if (navigationPanel != null)
            {
                var selectedOrgPathVar = navigationPanel.GetVariable("SelectedOrgNodePath");
                if (selectedOrgPathVar != null)
                    selectedOrgPathVar.Value = orgNodePath;
                else
                    Log.Warning("TreeNavLogic", "SelectedOrgNodePath variable not found in NavigationPanelright");
            }
            else
                Log.Warning("TreeNavLogic", "NavigationPanelright not found");
        }
        catch (Exception ex)
        {
            Log.Error("TreeNavLogic", $"Error selecting default org node: {ex.Message}");
        }
    }

    private string GetNodePath(IUANode node)
    {
        var pathParts = new System.Collections.Generic.List<string>();
        IUANode currentNode = node;

        while (currentNode != null && currentNode != Project.Current)
        {
            pathParts.Insert(0, currentNode.BrowseName);
            currentNode = currentNode.Owner;
        }

        return string.Join("/", pathParts);
    }
}
