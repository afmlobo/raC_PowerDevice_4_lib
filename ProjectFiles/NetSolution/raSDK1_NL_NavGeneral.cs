#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.AuditSigning;
#endregion

public class raSDK1_NL_NavGeneral : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]

    public void NavGeneral()
    {
        string lib = string.Empty;
        string lType = string.Empty;
        string sourceMsg = string.Empty;
        IUANode logixTag = null;
        IUAObject lButton = null;
        IUAObject launchAliasObj = null;
        DialogType dialogToOpen = null;
        string faceplateTypeName = string.Empty;

        //  Find the button owner object and the Ref_* tags associated with it.
        try
        {
            // Get button object
            lButton = Owner.Owner.GetObject(this.Owner.BrowseName);

            // Make Launch Object that will contain aliases
            launchAliasObj = InformationModel.MakeObject("LaunchAliasObj");

            // Get each alias from Launch Button and add them into Launch Object, and assign NodeId values 
            foreach (var inpTag in lButton.Children)
            {
                if (inpTag.BrowseName.Contains("Ref_"))
                {
                    // Make a variable with same name as alias of type NodeId
                    var newVar = InformationModel.MakeVariable(inpTag.BrowseName, OpcUa.DataTypes.NodeId);
                    //If inpTag is UAVariable type
                    if (inpTag is UAVariable)
                    {
                        // Assign alias value to new variable
                        var inpTagValue = ((UAVariable)inpTag).Value;

                        if (inpTagValue != null && inpTagValue.Value != null)
                        {
                            newVar.Value = inpTagValue;
                            // Add variable int launch object
                            launchAliasObj.Add(newVar);

                            if (inpTag.BrowseName == "Ref_Tag")
                            {
                                logixTag = InformationModel.Get(inpTagValue);
                            }
                        }
                        else if (inpTag.BrowseName == "Ref_Tag")
                        {
                            Log.Warning(this.GetType().Name, "Unable to create alias for object '" + inpTag.BrowseName + "'");
                            return;
                        }
                        else
                        {
                            //If no value is assigned to a Ref_ input, annunciate that it is missing a node assignment
                            Log.Warning(this.GetType().Name, "Missing node assignment to variable: " + inpTag.BrowseName);
                        }
                    }
                }
                else if (inpTag.BrowseName.Contains("Cfg_DialogBox"))
                {
                    try
                    {
                        // Assign dialog box to launch
                        dialogToOpen = (DialogType)InformationModel.Get(((UAVariable)inpTag).Value);
                    }
                    catch
                    {
                        //If no or bad value is assigned to Cfg_DialogBox, annunciate that dialog box is not found
                        Log.Warning(this.GetType().Name, "Unable to find Node assigned to Cfg_DialogBox");
                    }
                }
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Error creating alias Ref_* objects");
            return;
        }


        // Launch faceplate if Diagbox filled in, else try it by library and type strings
        if (dialogToOpen == null)
        {
            // Get strings for library and type 
            var fpType = lButton.GetVariable("Cfg_DisplayType");
            var cfgNavLibraryString = lButton.GetVariable("Cfg_NavLibraryString");
            var cfgNavInstructionString = lButton.GetVariable("Cfg_NavInstructionString");

            if (ValidateVar(cfgNavLibraryString, "Cfg_NavLibraryString") && ValidateVar(cfgNavInstructionString, "Cfg_NavInstructionString") && ValidateVar(logixTag, "Ref_Tag") && ValidateVar(fpType, "Cfg_DisplayType"))
            {
                try
                {
                    string cfgNavLibStrVal = cfgNavLibraryString.Value.Value.ToString();
                    string cfgNavLibInsStrVal = cfgNavInstructionString.Value.Value.ToString();
                    IUAVariable libVar = null;
                    IUAVariable lTypeVar = null;

                    if (cfgNavLibStrVal.Contains('.') || cfgNavLibInsStrVal.Contains('.'))
                    {
                        var cfgNavLibStrVals = cfgNavLibStrVal.Split('.');
                        var cfgNavLibInsStrVals = cfgNavLibInsStrVal.Split('.');

                        libVar = logixTag.Children.GetVariable(cfgNavLibStrVals[0])?.Children.GetVariable(cfgNavLibStrVals[1]);
                        lTypeVar = logixTag.Children.GetVariable(cfgNavLibInsStrVals[0])?.Children.GetVariable(cfgNavLibInsStrVals[1]);
                    }
                    else
                    {
                        // First try the extended tag properties
                        libVar = logixTag.Children.GetVariable(cfgNavLibStrVal);
                        lTypeVar = logixTag.Children.GetVariable(cfgNavLibInsStrVal);
                    }

                    lib = libVar?.Value?.Value as string;
                    lType = lTypeVar?.Value?.Value as string;

                    // The extended tag property was empty
                    if (string.IsNullOrEmpty(lib) || string.IsNullOrEmpty(lType))
                    {
                        lib = libVar?.RemoteRead()?.Value as string;
                        lType = lTypeVar?.RemoteRead()?.Value as string;

                        if (!string.IsNullOrEmpty(lib) && !string.IsNullOrEmpty(lType))
                        {
                            sourceMsg = "Check tag members '" + cfgNavLibraryString.Value + "' (" + lib + ") and '" + cfgNavInstructionString.Value + "' (" + lType + ")";
                        }
                    }
                    else
                    {
                        sourceMsg = "Check Extended Tag Properties '" + cfgNavLibraryString.Value + "' (" + lib + ") and '" + cfgNavInstructionString.Value + "' (" + lType + ")";
                    }

                    // Build the dialog box name and find it only if we have valid lib and lType
                    if (!string.IsNullOrEmpty(lib) && !string.IsNullOrEmpty(lType))
                    {
                        faceplateTypeName = lib.Replace('-', '_') + '_' + lType + '_' + fpType.Value;
                        var foundFp = Project.Current.Find(faceplateTypeName);                    

                        if (foundFp == null)
                        {
                            Log.Warning(this.GetType().Name, "Dialog Box '" + faceplateTypeName + "' not found for tag '" + logixTag.BrowseName + "'. " + sourceMsg);
                            return;
                        }
                        else
                        {
                            // if found is DialogType, than it is a faceplate type
                            if (foundFp.GetType() == typeof(DialogType))
                            {
                                dialogToOpen = (DialogType)foundFp;
                            }
                            else // found current instance of faceplate
                            {
                                // Get faceplate type from instance
                                System.Reflection.PropertyInfo objType = foundFp.GetType().GetProperty("ObjectType");
                                dialogToOpen = (DialogType)(objType.GetValue(foundFp, null));
                            }
                        }            
                    }
                }
                catch
                {
                    Log.Warning(this.GetType().Name, "Failed to launch dialog box.Please verify if the Cfg_DialogBox, Cfg_NavLibraryString, Cfg_NavInstructionString is configured correctly.");
                }
            }
        }

        // Launch the faceplate
        if (dialogToOpen != null)
        {
            try
            {
                // Launch DialogBox passing Launch Object that contains the aliases as an alias 
                UICommands.OpenDialog(lButton, dialogToOpen, launchAliasObj.NodeId);
            }
            catch
            {
                Log.Warning(this.GetType().Name, "Failed to launch dialog box '" + dialogToOpen.BrowseName + "'.");
                return;
            }
        }
        else
        {
            Log.Warning(this.GetType().Name, "Failed to launch dialog box.Please verify if the Cfg_DialogBox, Cfg_NavLibraryString, Cfg_NavInstructionString is configured correctly.");
            return;
        }

        // If configured, close the dialog box containing launch button
        try
        {
            bool cfgCloseCurrent = lButton.GetVariable("Cfg_CloseCurrentDisplay").Value;
            if (cfgCloseCurrent)
            {
                CloseCurrentDB(Owner);
            }
        }
        catch
        {
            Log.Warning(this.GetType().Name, "Failed to close current dialog box");
        }
    }

    public void CloseCurrentDB(IUANode inputNode)
    {
        if (inputNode == null)
        {
            return;
        }

        // if input node is of type Dialog, close it
        if (inputNode is Dialog)
        {
            // close dialog box
            ((Dialog)inputNode).Close();
            return;
        }
        // if input node is Main Window, no dialog box was found, return
        if (inputNode is Window)
        {
            return;
        }
        // continue search for Dialog or Main Window
        if (inputNode.Owner != null)
        {
            CloseCurrentDB(inputNode.Owner);
        }
    }

    private bool ValidateVar(object variable, string variableName)
    {
        if (variable == null)
        {
            Log.Warning(this.GetType().Name, $"{variableName} is null or not configured");
            return false;
        }
        return true;
    }
}
