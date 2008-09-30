/***************************************************************************

Copyright (c) Microsoft Corporation. All rights reserved.
This code is licensed under the Visual Studio SDK license terms.
THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.

***************************************************************************/

using System;
using System.IO;
using System.Resources;
using System.Diagnostics;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using Microsoft.Win32;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio;
using System.Runtime.Serialization.Formatters.Binary;
using MsVsShell = Microsoft.VisualStudio.Shell;
using ErrorHandler = Microsoft.VisualStudio.ErrorHandler;

namespace Microsoft.Samples.VisualStudio.SourceControlIntegration.SccProvider
{
	/////////////////////////////////////////////////////////////////////////////
	// SccProvider
	[MsVsShell.DefaultRegistryRoot("Software\\Microsoft\\VisualStudio\\9.0Exp")]
	// Register the package to have information displayed in Help/About dialog box
	[MsVsShell.InstalledProductRegistration(false, "#100", "#101", "1.0.0.0",
		IconResourceID = CommandId.iiconProductIcon)]
	// Declare that resources for the package are to be found in the managed assembly resources, and not in a satellite dll
	[MsVsShell.PackageRegistration(UseManagedResourcesOnly = true)]
	// Register the resource ID of the CTMENU section (generated from compiling the VSCT file), so the IDE will know how to merge this package's menus with the rest of the IDE when "devenv /setup" is run
	// The menu resource ID needs to match the ResourceName number defined in the csproj project file in the VSCTCompile section
	// Everytime the version number changes VS will automatically update the menus on startup; if the version doesn't change, you will need to run manually "devenv /setup /rootsuffix:Exp" to see VSCT changes reflected in IDE
	[MsVsShell.ProvideMenuResource(1000, 1)]
	// Register a sample options page visible as Tools/Options/SourceControl/SampleOptionsPage when the provider is active
	[MsVsShell.ProvideOptionPageAttribute(typeof (SccProviderOptions),
		"Source Control", "Sample Options Page", 106, 107, false)]
	[ProvideToolsOptionsPageVisibility("Source Control", "Sample Options Page",
		"B0BAC05D-0000-41D1-A6C3-704E6C1A3DE2")]
	// Register a sample tool window visible only when the provider is active
	[MsVsShell.ProvideToolWindow(typeof (SccProviderToolWindow))]
	[MsVsShell.ProvideToolWindowVisibility(typeof (SccProviderToolWindow),
		"B0BAC05D-0000-41D1-A6C3-704E6C1A3DE2")]
	// Register the source control provider's service (implementing IVsScciProvider interface)
	[MsVsShell.ProvideService(typeof (SccProviderService),
		ServiceName = "Source Control Sample Provider Service")]
	// Register the source control provider to be visible in Tools/Options/SourceControl/Plugin dropdown selector
	[ProvideSourceControlProvider("Managed Source Control Sample Provider",
		"#100")]
	// Pre-load the package when the command UI context is asserted (the provider will be automatically loaded after restarting the shell if it was active last time the shell was shutdown)
	[MsVsShell.ProvideAutoLoad("B0BAC05D-0000-41D1-A6C3-704E6C1A3DE2")]
	// Register the key used for persisting solution properties, so the IDE will know to load the source control package when opening a controlled solution containing properties written by this package
	[ProvideSolutionProps(_strSolutionPersistanceKey)]
	// Declare the package guid
	[Guid("B0BAC05D-2000-41D1-A6C3-704E6C1A3DE2")]
	public sealed class SccProvider : MsVsShell.Package,
	                                  IOleCommandTarget,
	                                  IVsPersistSolutionProps
		// We'll write properties in the solution file to track when solution is controlled; the interface needs to be implemented by the package object
	{
		// The service provider implemented by the package
		private SccProviderService sccService = null;
		// The name of this provider (to be written in solution and project files)
		// As a best practice, to be sure the provider has an unique name, a guid like the provider guid can be used as a part of the name
		private const string _strProviderName =
			"Sample Source Control Provider:{B0BAC05D-2000-41D1-A6C3-704E6C1A3DE2}";

		// The name of the solution section used to persist provider options (should be unique)
		private const string _strSolutionPersistanceKey =
			"SampleSourceControlProviderSolutionProperties";

		// The name of the section in the solution user options file used to persist user-specific options (should be unique, shorter than 31 characters and without dots)
		private const string _strSolutionUserOptionsKey =
			"SampleSourceControlProvider";

		// The names of the properties stored by the provider in the solution file
		private const string _strSolutionControlledProperty = "SolutionIsControlled";
		private const string _strSolutionBindingsProperty = "SolutionBindings";
		// Whether the solution was just added to source control and the provider needs to saved source control properties in the solution file when the solution is saved
		private bool _solutionHasDirtyProps = false;
		// The guid of solution folders
		private readonly Guid guidSolutionFolderProject = new Guid(0x2150e333, 0x8fdc, 0x42a3,
		                                                  0x94, 0x74, 0x1a, 0x39,
		                                                  0x56, 0xd4, 0x6d, 0xe8);

		public SccProvider()
		{
			Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture,
			                              "Entering constructor for: {0}",
			                              this.ToString()));

			// The provider implements the IVsPersistSolutionProps interface which is derived from IVsPersistSolutionOpts,
			// The base class MsVsShell.Package also implements IVsPersistSolutionOpts, so we're overriding its functionality
			// Therefore, to persist user options in the suo file we will not use the set of AddOptionKey/OnLoadOptions/OnSaveOptions 
			// set of functions, but instead we'll use the IVsPersistSolutionProps functions directly.
		}

		/////////////////////////////////////////////////////////////////////////////
		// SccProvider Package Implementation

		#region Package Members

		public new Object GetService(Type serviceType)
		{
			return base.GetService(serviceType);
		}

		protected override void Initialize()
		{
			Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture,
			                              "Entering Initialize() of: {0}",
			                              this.ToString()));
			base.Initialize();

			// Proffer the source control service implemented by the provider
			sccService = new SccProviderService(this);
			((IServiceContainer) this).AddService(typeof (SccProviderService),
			                                      sccService, true);

			// Add our command handlers for menu (commands must exist in the .vsct file)
			MsVsShell.OleMenuCommandService mcs =
				GetService(typeof (IMenuCommandService)) as
				MsVsShell.OleMenuCommandService;
			if (mcs != null)
			{
				// ToolWindow Command
				CommandID cmd = new CommandID(GuidList.guidSccProviderCmdSet,
				                              CommandId.icmdViewToolWindow);
				MenuCommand menuCmd = new MenuCommand(Exec_icmdViewToolWindow, cmd);
				mcs.AddCommand(menuCmd);

				// ToolWindow's ToolBar Command
				cmd = new CommandID(GuidList.guidSccProviderCmdSet,
				                    CommandId.icmdToolWindowToolbarCommand);
				menuCmd = new MenuCommand(Exec_icmdToolWindowToolbarCommand, cmd);
				mcs.AddCommand(menuCmd);

				// Source control menu commmads
				cmd = new CommandID(GuidList.guidSccProviderCmdSet,
				                    CommandId.icmdAddToSourceControl);
				menuCmd = new MenuCommand(Exec_icmdAddToSourceControl, cmd);
				mcs.AddCommand(menuCmd);

				cmd = new CommandID(GuidList.guidSccProviderCmdSet,
					CommandId.icmdCheckin);

				menuCmd = new MenuCommand(Exec_icmdCheckin, cmd);
				mcs.AddCommand(menuCmd);

				cmd = new CommandID(GuidList.guidSccProviderCmdSet,
					CommandId.icmdCheckout);
				menuCmd = new MenuCommand(Exec_icmdCheckout, cmd);
				mcs.AddCommand(menuCmd);

				cmd = new CommandID(GuidList.guidSccProviderCmdSet,
				                    CommandId.icmdUseSccOffline);
				menuCmd = new MenuCommand(Exec_icmdUseSccOffline, cmd);
				mcs.AddCommand(menuCmd);
			}

			// Register the provider with the source control manager
			// If the package is to become active, this will also callback on OnActiveStateChange and the menu commands will be enabled
			IVsRegisterScciProvider rscp =
				(IVsRegisterScciProvider) GetService(typeof (IVsRegisterScciProvider));
			rscp.RegisterSourceControlProvider(GuidList.guidSccProvider);
		}

		protected override void Dispose(bool disposing)
		{
			Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture,
			                              "Entering Dispose() of: {0}", this.ToString()));

			sccService.Dispose();

			base.Dispose(disposing);
		}

		// This function is called by the IVsSccProvider service implementation when the active state of the provider changes
		// If the package needs to refresh UI or perform other tasks, this is a good place to add the code
		public void OnActiveStateChange()
		{
		}

		// Returns the name of the source control provider
		public string ProviderName
		{
			get { return _strProviderName; }
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsPersistSolutionProps specific functions
		//--------------------------------------------------------------------------------

		#region IVsPersistSolutionProps interface functions

		public int OnProjectLoadFailure([InAttribute] IVsHierarchy pStubHierarchy,
		                                [InAttribute] string pszProjectName,
		                                [InAttribute] string pszProjectMk,
		                                [InAttribute] string pszKey)
		{
			return VSConstants.S_OK;
		}

		public int QuerySaveSolutionProps([InAttribute] IVsHierarchy pHierarchy,
		                                  [OutAttribute] VSQUERYSAVESLNPROPS[]
		                                  	pqsspSave)
		{
			// This function is called by the IDE to determine if something needs to be saved in the solution.
			// If the package returns that it has dirty properties, the shell will callback on SaveSolutionProps

			// We will write solution properties only for the solution
			// A provider may consider writing in the solution project-binding-properties for each controlled project
			// that could help it locating the projects in the store during operations like OpenFromSourceControl
			if (!sccService.IsProjectControlled(null))
			{
				pqsspSave[0] = VSQUERYSAVESLNPROPS.QSP_HasNoProps;
			}
			else
			{
				if (SolutionHasDirtyProps)
				{
					pqsspSave[0] = VSQUERYSAVESLNPROPS.QSP_HasDirtyProps;
				}
				else
				{
					pqsspSave[0] = VSQUERYSAVESLNPROPS.QSP_HasNoDirtyProps;
				}
			}

			return VSConstants.S_OK;
		}

		public int SaveSolutionProps([InAttribute] IVsHierarchy pHierarchy,
		                             [InAttribute] IVsSolutionPersistence
		                             	pPersistence)
		{
			// This function gets called by the shell after determining the package has dirty props.
			// The package will pass in the key under which it wants to save its properties, 
			// and the IDE will call back on WriteSolutionProps

			// The properties will be saved in the Pre-Load section
			// When the solution will be reopened, the IDE will call our package to load them back before the projects in the solution are actually open
			// This could help if the source control package needs to persist information like projects translation tables, that should be read from the suo file
			// and should be available by the time projects are opened and the shell start calling IVsSccEnlistmentPathTranslation functions.
			pPersistence.SavePackageSolutionProps(1, null, this,
			                                      _strSolutionPersistanceKey);

			// Once we saved our props, the solution is not dirty anymore
			SolutionHasDirtyProps = false;

			return VSConstants.S_OK;
		}

		public int WriteSolutionProps([InAttribute] IVsHierarchy pHierarchy,
		                              [InAttribute] string pszKey,
		                              [InAttribute] IPropertyBag pPropBag)
		{
			// The package will only save one property in the solution, to indicate that solution is controlled

			// A good provider may need to persist as solution properties the controlled status of projects and their locations, too.
			// If an operation like OpenFromSourceControl has sense for the provider, and the user has selected to open from 
			// source control the solution file, the bindings written as solution properties will help identifying where the 
			// project files are in the source control database. The source control provider can download the project files 
			// before they are needed by the IDE to be opened.
			string strControlled = true.ToString();
			object obj = strControlled;
			pPropBag.Write(_strSolutionControlledProperty, ref obj);
			string strSolutionLocation = "<Solution Location In Database>";
			obj = strSolutionLocation;
			pPropBag.Write(_strSolutionBindingsProperty, ref obj);

			return VSConstants.S_OK;
		}

		public int ReadSolutionProps([InAttribute] IVsHierarchy pHierarchy,
		                             [InAttribute] string pszProjectName,
		                             [InAttribute] string pszProjectMk,
		                             [InAttribute] string pszKey,
		                             [InAttribute] int fPreLoad,
		                             [InAttribute] IPropertyBag pPropBag)
		{
			// This function gets called by the shell when a solution controlled by this provider is opened in IDE.
			// The shell encounters the _strSolutionPersistanceKey section in the solution, and based based on 
			// registration info written by ProvideSolutionProps identifies this package as the section owner, 
			// loads this package if necessary and call the package to read the persisted solution options.

			if (_strSolutionPersistanceKey.CompareTo(pszKey) == 0)
			{
				// We were called to read the key written by this source control provider
				// First thing to do: register the source control provider with the source control manager.
				// This allows the scc manager to switch the active source control provider if necessary,
				// and set this provider active; the provider will be later called to provide source control services for this solution.
				// (This is how automatic source control provider switching on solution opening is implemented)
				IVsRegisterScciProvider rscp =
					(IVsRegisterScciProvider) GetService(typeof (IVsRegisterScciProvider));
				rscp.RegisterSourceControlProvider(GuidList.guidSccProvider);

				// Now we can read all the data and store it in memory
				// The read data will be used when the solution has completed opening
				object pVar;
				pPropBag.Read(_strSolutionControlledProperty, out pVar, null, 0, null);
				if (pVar.ToString().CompareTo(true.ToString()) == 0)
				{
					pPropBag.Read(_strSolutionBindingsProperty, out pVar, null, 0, null);
					sccService.LoadingControlledSolutionLocation = pVar.ToString();
				}
			}
			return VSConstants.S_OK;
		}

		public int SaveUserOptions([InAttribute] IVsSolutionPersistence pPersistence)
		{
			// This function gets called by the shell when the SUO file is saved.
			// The provider calls the shell back to let it know which options keys it will use in the suo file.
			// The shell will create a stream for the section of interest, and will call back the provider on 
			// IVsPersistSolutionProps.WriteUserOptions() to save specific options under the specified key.
			int pfResult = 0;
			sccService.AnyItemsUnderSourceControl(out pfResult);
			if (pfResult > 0)
			{
				pPersistence.SavePackageUserOpts(this, _strSolutionUserOptionsKey);
			}
			return VSConstants.S_OK;
		}

		public int WriteUserOptions([InAttribute] IStream pOptionsStream,
		                            [InAttribute] string pszKey)
		{
			// This function gets called by the shell to let the package write user options under the specified key.
			// The key was declared in SaveUserOptions(), when the shell started saving the suo file.
			Debug.Assert(pszKey.CompareTo(_strSolutionUserOptionsKey) == 0,
			             "The shell called to read an key that doesn't belong to this package");

			Hashtable hashProjectsUserData = new Hashtable();
			IVsSolution solution = (IVsSolution) GetService(typeof (SVsSolution));
			// get the list of controllable projects
			var hash = GetLoadedControllableProjectsEnum();
			// add the solution to the controllable projects list
			IVsHierarchy solHier = (IVsHierarchy) GetService(typeof (SVsSolution));
			hash.Add(solHier);
			
			// collect all projects that are controlled and offline
			foreach (IVsHierarchy pHier in hash)
			{
				if (sccService.IsProjectControlled(pHier) &&
				    sccService.IsProjectOffline(pHier))
				{
					// The information we'll persist in the suo file needs to be usable if the solution is moved in a diffrent location
					// therefore we'll store project names as known by the solution (mostly relativized to the solution's folder)
					string projUniqueName;
					if (solution.GetUniqueNameOfProject(pHier, out projUniqueName) ==
					    VSConstants.S_OK)
					{
						hashProjectsUserData[projUniqueName] = true;
					}
				}
			}

			// The easiest way to read/write the data of interest is by using a binary formatter class
			// This way, we can write a map of information about projects with one call 
			// (each element in the map needs to be serializable though)
			// The alternative is to write binary data in any byte format you'd like using pOptionsStream.Write
			
			var pStream = new DataStreamFromComStream(pOptionsStream);
			var formatter = new BinaryFormatter();
			formatter.Serialize(pStream, hashProjectsUserData);

			return VSConstants.S_OK;
		}

		public int LoadUserOptions(
			[InAttribute] IVsSolutionPersistence pPersistence,
			[InAttribute] uint grfLoadOpts)
		{
			// This function gets called by the shell when a solution is opened and the SUO file is read.
			// Note this can be during opening a new solution, or may be during merging of 2 solutions.
			// The provider calls the shell back to let it know which options keys from the suo file were written by this provider.
			// If the shell will find in the suo file a section that belong to this package, it will create a stream, 
			// and will call back the provider on IVsPersistSolutionProps.ReadUserOptions() to read specific options 
			// under that option key.
			pPersistence.LoadPackageUserOpts(this, _strSolutionUserOptionsKey);
			return VSConstants.S_OK;
		}

		public int ReadUserOptions([InAttribute] IStream pOptionsStream,
		                           [InAttribute] string pszKey)
		{
			// This function is called by the shell if the _strSolutionUserOptionsKey section declared
			// in LoadUserOptions() as being written by this package has been found in the suo file. 
			// Note this can be during opening a new solution, or may be during merging of 2 solutions.
			// A good source control provider may need to persist this data until OnAfterOpenSolution or OnAfterMergeSolution is called

			// The easiest way to read/write the data of interest is by using a binary formatter class
			var pStream = new DataStreamFromComStream(pOptionsStream);
			Hashtable hashProjectsUserData = new Hashtable();
			if (pStream.Length > 0)
			{
				var formatter = new BinaryFormatter();
				hashProjectsUserData = formatter.Deserialize(pStream) as Hashtable;
			}

			IVsSolution solution = (IVsSolution) GetService(typeof (SVsSolution));
			foreach (string projUniqueName in hashProjectsUserData.Keys)
			{
				// If this project is recognizable as part of the solution
				IVsHierarchy pHier = null;
				if (solution.GetProjectOfUniqueName(projUniqueName, out pHier) ==
				    VSConstants.S_OK &&
				    pHier != null)
				{
					sccService.ToggleOfflineStatus(pHier);
				}
			}

			return VSConstants.S_OK;
		}

		#endregion

		#region Source Control Command Enabling

		/// <summary>
		/// The shell call this function to know if a menu item should be visible and
		/// if it should be enabled/disabled.
		/// Note that this function will only be called when an instance of this editor
		/// is open.
		/// </summary>
		/// <param name="guidCmdGroup">Guid describing which set of command the current command(s) belong to</param>
		/// <param name="cCmds">Number of command which status are being asked for</param>
		/// <param name="prgCmds">Information for each command</param>
		/// <param name="pCmdText">Used to dynamically change the command text</param>
		/// <returns>HRESULT</returns>
		public int QueryStatus(ref Guid guidCmdGroup, uint cCmds, OLECMD[] prgCmds,
		                       IntPtr pCmdText)
		{
			Debug.Assert(cCmds == 1, "Multiple commands");
			Debug.Assert(prgCmds != null, "NULL argument");

			if (prgCmds == null)
				return VSConstants.E_INVALIDARG;

			// Filter out commands that are not defined by this package
			if (guidCmdGroup != GuidList.guidSccProviderCmdSet)
			{
				return
					(int)
					(Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED);
			}

			OLECMDF cmdf = OLECMDF.OLECMDF_SUPPORTED;

			// All source control commands needs to be hidden and disabled when the provider is not active
			if (!sccService.Active)
			{
				cmdf = cmdf | OLECMDF.OLECMDF_INVISIBLE;
				cmdf = cmdf & ~(OLECMDF.OLECMDF_ENABLED);

				prgCmds[0].cmdf = (uint) cmdf;
				return VSConstants.S_OK;
			}

			// Process our Commands
			switch (prgCmds[0].cmdID)
			{
				case CommandId.icmdAddToSourceControl:
					cmdf |= QueryStatus_icmdAddToSourceControl();
					break;

				case CommandId.icmdCheckin:
					cmdf |= QueryStatus_icmdCheckin();
					break;

				case CommandId.icmdCheckout:
					cmdf |= QueryStatus_icmdCheckout();
					break;

				case CommandId.icmdUseSccOffline:
					cmdf |= QueryStatus_icmdUseSccOffline();
					break;

				case CommandId.icmdViewToolWindow:
				case CommandId.icmdToolWindowToolbarCommand:
					// These commmands are always enabled when the provider is active
					cmdf |= OLECMDF.OLECMDF_ENABLED;
					break;

				default:
					return
						(int)
						(Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED);
			}

			prgCmds[0].cmdf = (uint) cmdf;

			return VSConstants.S_OK;
		}

		private OLECMDF QueryStatus_icmdCheckin()
		{
			if (!IsThereASolution())
			{
				return OLECMDF.OLECMDF_INVISIBLE;
			}

			IList<string> files = GetSelectedFilesInControlledProjects();
			foreach (string file in files)
			{
				SourceControlStatus status = sccService.GetFileStatus(file);
				if (status == SourceControlStatus.scsCheckedIn)
				{
					continue;
				}

				if (status == SourceControlStatus.scsCheckedOut)
				{
					return OLECMDF.OLECMDF_ENABLED;
				}

				// If the file is uncontrolled, enable the command only if the file is part of a controlled project
				IList<VSITEMSELECTION> nodes =
					sccService.GetControlledProjectsContainingFile(file);
				if (nodes.Count > 0)
				{
					return OLECMDF.OLECMDF_ENABLED;
				}
			}

			return OLECMDF.OLECMDF_SUPPORTED;
		}

		private OLECMDF QueryStatus_icmdCheckout()
		{
			if (!IsThereASolution())
			{
				return OLECMDF.OLECMDF_INVISIBLE;
			}

			IList<string> files = GetSelectedFilesInControlledProjects();
			foreach (string file in files)
			{
				if (sccService.GetFileStatus(file) == SourceControlStatus.scsCheckedIn)
				{
					return OLECMDF.OLECMDF_ENABLED;
				}
			}

			return OLECMDF.OLECMDF_SUPPORTED;
		}

		private OLECMDF QueryStatus_icmdAddToSourceControl()
		{
			if (!IsThereASolution())
			{
				return OLECMDF.OLECMDF_INVISIBLE;
			}

			IList<VSITEMSELECTION> sel = GetSelectedNodes();
			bool isSolutionSelected = false;
			var hash = GetSelectedHierarchies(sel, out isSolutionSelected);

			// The command is enabled when the solution is selected and is uncontrolled yet
			// or when an uncontrolled project is selected
			if (isSolutionSelected)
			{
				if (!sccService.IsProjectControlled(null))
				{
					return OLECMDF.OLECMDF_ENABLED;
				}
			}
			else
			{
				foreach (IVsHierarchy pHier in hash)
				{
					if (!sccService.IsProjectControlled(pHier))
					{
						return OLECMDF.OLECMDF_ENABLED;
					}
				}
			}

			return OLECMDF.OLECMDF_SUPPORTED;
		}

		private OLECMDF QueryStatus_icmdUseSccOffline()
		{
			if (!IsThereASolution())
			{
				return OLECMDF.OLECMDF_INVISIBLE;
			}

			IList<VSITEMSELECTION> sel = GetSelectedNodes();
			bool isSolutionSelected = false;
			var hash = GetSelectedHierarchies(sel, out isSolutionSelected);
			if (isSolutionSelected)
			{
				IVsHierarchy solHier = (IVsHierarchy) GetService(typeof (SVsSolution));
				hash.Remove(solHier);
//                hash[solHier] = null;
			}

			bool selectedOffline = false;
			bool selectedOnline = false;
			foreach (IVsHierarchy pHier in hash)
			{
				if (!sccService.IsProjectControlled(pHier))
				{
					// If a project is not controlled, set both flags to disalbe the command
					selectedOffline = selectedOnline = true;
				}

				if (sccService.IsProjectOffline(pHier))
				{
					selectedOffline = true;
				}
				else
				{
					selectedOnline = true;
				}
			}

			// For mixed selection, or if nothing is selected, disable the command
			if (selectedOnline && selectedOffline ||
			    !selectedOnline && !selectedOffline)
			{
				return OLECMDF.OLECMDF_SUPPORTED;
			}

			return OLECMDF.OLECMDF_ENABLED |
			       (selectedOffline ? OLECMDF.OLECMDF_LATCHED : OLECMDF.OLECMDF_ENABLED);
		}

		#endregion

		#region Source Control Commands Execution

		private void Exec_icmdCheckin(object sender, EventArgs e)
		{
			if (!IsThereASolution())
			{
				return;
			}

			IList<VSITEMSELECTION> selectedNodes = null;
			IList<string> files =
				GetSelectedFilesInControlledProjects(out selectedNodes);
			foreach (string file in files)
			{
				SourceControlStatus status = sccService.GetFileStatus(file);
				if (status == SourceControlStatus.scsCheckedOut)
				{
					sccService.CheckinFile(file);
				}
				else if (status == SourceControlStatus.scsUncontrolled)
				{
					sccService.AddFileToSourceControl(file);
				}
			}

			// now refresh the selected nodes' glyphs
			RefreshNodesGlyphs(selectedNodes);
		}

		private void Exec_icmdCheckout(object sender, EventArgs e)
		{
			if (!IsThereASolution())
			{
				return;
			}

			IList<VSITEMSELECTION> selectedNodes = null;
			IList<string> files =
				GetSelectedFilesInControlledProjects(out selectedNodes);
			foreach (string file in files)
			{
				SourceControlStatus status = sccService.GetFileStatus(file);
				if (status == SourceControlStatus.scsCheckedIn)
				{
					sccService.CheckoutFile(file);
				}
			}

			// now refresh the selected nodes' glyphs
			RefreshNodesGlyphs(selectedNodes);
		}

		private void Exec_icmdAddToSourceControl(object sender, EventArgs e)
		{
			if (!IsThereASolution())
			{
				Debug.Assert(false, "The command should have been disabled");
				return;
			}

			IList<VSITEMSELECTION> sel = GetSelectedNodes();
			bool isSolutionSelected = false;
			var hash = GetSelectedHierarchies(sel, out isSolutionSelected);

			var hashUncontrolledProjects = new Hashtable();
			if (isSolutionSelected)
			{
				// When the solution is selected, all the uncontrolled projects in the solution will be added to scc
				hash = GetLoadedControllableProjectsEnum();
			}

			foreach (IVsHierarchy pHier in hash)
			{
				if (!sccService.IsProjectControlled(pHier))
				{
					hashUncontrolledProjects[pHier] = true;
				}
			}

			sccService.AddProjectsToSourceControl(hashUncontrolledProjects,
			                                      isSolutionSelected);
		}

		private void Exec_icmdUseSccOffline(object sender, EventArgs e)
		{
			if (!IsThereASolution())
			{
				return;
			}

			IList<VSITEMSELECTION> sel = GetSelectedNodes();
			bool isSolutionSelected = false;
			var hash = GetSelectedHierarchies(sel, out isSolutionSelected);
			if (isSolutionSelected)
			{
				IVsHierarchy solHier = (IVsHierarchy) GetService(typeof (SVsSolution));
				hash.Remove(solHier);
//				hash[solHier] = null;
			}

			foreach (IVsHierarchy pHier in hash)
			{
				// If a project is not controlled, skip it
				if (!sccService.IsProjectControlled(pHier))
				{
					continue;
				}

				sccService.ToggleOfflineStatus(pHier);
			}
		}

		// The function can be used to bring back the provider's toolwindow if it was previously closed
		private void Exec_icmdViewToolWindow(object sender, EventArgs e)
		{
			MsVsShell.ToolWindowPane window =
				this.FindToolWindow(typeof (SccProviderToolWindow), 0, true);
			IVsWindowFrame windowFrame = null;
			if (window != null && window.Frame != null)
			{
				windowFrame = (IVsWindowFrame) window.Frame;
			}
			if (windowFrame != null)
			{
				ErrorHandler.ThrowOnFailure(windowFrame.Show());
			}
		}

		private void Exec_icmdToolWindowToolbarCommand(object sender, EventArgs e)
		{
			SccProviderToolWindow window =
				(SccProviderToolWindow)
				this.FindToolWindow(typeof (SccProviderToolWindow), 0, true);

			if (window != null)
			{
				window.ToolWindowToolbarCommand();
			}
		}

		#endregion

		#region Source Control Utility Functions

		/// <summary>
		/// Returns whether suorce control properties must be saved in the solution file
		/// </summary>
		public bool SolutionHasDirtyProps
		{
			get { return _solutionHasDirtyProps; }
			set { _solutionHasDirtyProps = value; }
		}

		/// <summary>
		/// Returns a list of controllable projects in the solution
		/// </summary>
		private HashSet<IVsHierarchy> GetLoadedControllableProjectsEnum()
		{
			var mapHierarchies = new HashSet<IVsHierarchy>();

			IVsSolution sol = (IVsSolution) GetService(typeof (SVsSolution));
			Guid rguidEnumOnlyThisType = new Guid();
			IEnumHierarchies ppenum = null;
			ErrorHandler.ThrowOnFailure(
				sol.GetProjectEnum((uint) __VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
				                   ref rguidEnumOnlyThisType, out ppenum));

			IVsHierarchy[] rgelt = new IVsHierarchy[1];
			uint pceltFetched = 0;
			while (ppenum.Next(1, rgelt, out pceltFetched) == VSConstants.S_OK
			       && pceltFetched == 1
				)
			{
				IVsSccProject2 sccProject2 = rgelt[0] as IVsSccProject2;
				if (sccProject2 != null)
				{
					mapHierarchies.Add(rgelt[0]);
				}
			}

			return mapHierarchies;
		}

		/// <summary>
		/// Checks whether a solution exist
		/// </summary>
		/// <returns>True if a solution was created.</returns>
		private bool IsThereASolution()
		{
			return (GetSolutionFileName() != null);
		}

		/// <summary>
		/// Gets the list of selected controllable project hierarchies
		/// </summary>
		/// <returns>True if a solution was created.</returns>
		private HashSet<IVsHierarchy> GetSelectedHierarchies(
			IList<VSITEMSELECTION> sel, out bool solutionSelected)
		{
			// Initialize output arguments
			solutionSelected = false;

			var mapHierarchies = new HashSet<IVsHierarchy>();
			foreach (VSITEMSELECTION vsItemSel in sel)
			{
				if (vsItemSel.pHier == null
				    || (vsItemSel.pHier as IVsSolution) != null
					)
				{
					solutionSelected = true;
				}

				// See if the selected hierarchy implements the IVsSccProject2 interface
				// Exclude from selection projects like FTP web projects that don't support SCC
				IVsSccProject2 sccProject2 = vsItemSel.pHier as IVsSccProject2;
				if (sccProject2 != null)
				{
					// FIXME: null
					mapHierarchies.Add(vsItemSel.pHier);
				}
			}

			return mapHierarchies;
		}

		/// <summary>
		/// Gets the list of directly selected VSITEMSELECTION objects
		/// </summary>
		/// <returns>A list of VSITEMSELECTION objects</returns>
		private IList<VSITEMSELECTION> GetSelectedNodes()
		{
			// Retrieve shell interface in order to get current selection
			IVsMonitorSelection monitorSelection =
				this.GetService(typeof (IVsMonitorSelection)) as IVsMonitorSelection;
			Debug.Assert(monitorSelection != null,
			             "Could not get the IVsMonitorSelection object from the services exposed by this project");
			if (monitorSelection == null)
			{
				throw new InvalidOperationException();
			}

			var selectedNodes = new List<VSITEMSELECTION>();
			IntPtr hierarchyPtr = IntPtr.Zero;
			IntPtr selectionContainer = IntPtr.Zero;
			try
			{
				// Get the current project hierarchy, project item, and selection container for the current selection
				// If the selection spans multiple hierachies, hierarchyPtr is Zero
				uint itemid;
				IVsMultiItemSelect multiItemSelect = null;
				ErrorHandler.ThrowOnFailure(
					monitorSelection.GetCurrentSelection(out hierarchyPtr, out itemid,
					                                     out multiItemSelect,
					                                     out selectionContainer));

				if (itemid != VSConstants.VSITEMID_SELECTION)
				{
					// We only care if there are nodes selected in the tree
					if (itemid != VSConstants.VSITEMID_NIL)
					{
						if (hierarchyPtr == IntPtr.Zero)
						{
							// Solution is selected
							VSITEMSELECTION vsItemSelection;
							vsItemSelection.pHier = null;
							vsItemSelection.itemid = itemid;
							selectedNodes.Add(vsItemSelection);
						}
						else
						{
							IVsHierarchy hierarchy =
								(IVsHierarchy) Marshal.GetObjectForIUnknown(hierarchyPtr);
							// Single item selection
							VSITEMSELECTION vsItemSelection;
							vsItemSelection.pHier = hierarchy;
							vsItemSelection.itemid = itemid;
							selectedNodes.Add(vsItemSelection);
						}
					}
				}
				else
				{
					if (multiItemSelect != null)
					{
						// This is a multiple item selection.

						//Get number of items selected and also determine if the items are located in more than one hierarchy
						uint numberOfSelectedItems;
						int isSingleHierarchyInt;
						ErrorHandler.ThrowOnFailure(
							multiItemSelect.GetSelectionInfo(out numberOfSelectedItems,
							                                 out isSingleHierarchyInt));
						bool isSingleHierarchy = (isSingleHierarchyInt != 0);

						// Now loop all selected items and add them to the list 
						Debug.Assert(numberOfSelectedItems > 0, "Bad number of selected itemd");
						if (numberOfSelectedItems > 0)
						{
							VSITEMSELECTION[] vsItemSelections =
								new VSITEMSELECTION[numberOfSelectedItems];
							ErrorHandler.ThrowOnFailure(multiItemSelect.GetSelectedItems(0,
							                                                             numberOfSelectedItems,
							                                                             vsItemSelections));
							foreach (VSITEMSELECTION vsItemSelection in vsItemSelections)
							{
								selectedNodes.Add(vsItemSelection);
							}
						}
					}
				}
			}
			finally
			{
				if (hierarchyPtr != IntPtr.Zero)
				{
					Marshal.Release(hierarchyPtr);
				}
				if (selectionContainer != IntPtr.Zero)
				{
					Marshal.Release(selectionContainer);
				}
			}

			return selectedNodes;
		}

		/// <summary>
		/// Returns a list of source controllable files in the selection (recursive)
		/// </summary>
		private IList<string> GetSelectedFilesInControlledProjects()
		{
			IList<VSITEMSELECTION> selectedNodes = null;
			return GetSelectedFilesInControlledProjects(out selectedNodes);
		}

		/// <summary>
		/// Returns a list of source controllable files in the selection (recursive)
		/// </summary>
		private IList<string> GetSelectedFilesInControlledProjects(
			out IList<VSITEMSELECTION> selectedNodes)
		{
			IList<string> sccFiles = new List<string>();

			selectedNodes = GetSelectedNodes();
			bool isSolutionSelected = false;
			var hash = GetSelectedHierarchies(selectedNodes, out isSolutionSelected);
			if (isSolutionSelected)
			{
				// Replace the selection with the root items of all controlled projects
				selectedNodes.Clear();
				var hashControllable = GetLoadedControllableProjectsEnum();
				foreach (IVsHierarchy pHier in hashControllable)
				{
					if (sccService.IsProjectControlled(pHier))
					{
						VSITEMSELECTION vsItemSelection;
						vsItemSelection.pHier = pHier;
						vsItemSelection.itemid = VSConstants.VSITEMID_ROOT;
						selectedNodes.Add(vsItemSelection);
					}
				}

				// Add the solution file to the list
				if (sccService.IsProjectControlled(null))
				{
					IVsHierarchy solHier = (IVsHierarchy) GetService(typeof (SVsSolution));
					VSITEMSELECTION vsItemSelection;
					vsItemSelection.pHier = solHier;
					vsItemSelection.itemid = VSConstants.VSITEMID_ROOT;
					selectedNodes.Add(vsItemSelection);
				}
			}

			// now look in the rest of selection and accumulate scc files
			foreach (VSITEMSELECTION vsItemSel in selectedNodes)
			{
				var pscp2 = vsItemSel.pHier as IVsSccProject2;
				if (pscp2 == null)
				{
					// solution case
					sccFiles.Add(GetSolutionFileName());
				}
				else
				{
					IList<string> nodefilesrec = GetProjectFiles(pscp2, vsItemSel.itemid);
					foreach (string file in nodefilesrec)
					{
						sccFiles.Add(file);
					}
				}
			}

			return sccFiles;
		}

		/// <summary>
		/// Returns a list of source controllable files associated with the specified node
		/// </summary>
		public IList<string> GetNodeFiles(IVsHierarchy hier, uint itemid)
		{
			var pscp2 = hier as IVsSccProject2;
			return GetNodeFiles(pscp2, itemid);
		}

		/// <summary>
		/// Returns a list of source controllable files associated with the specified node
		/// </summary>
		private IList<string> GetNodeFiles(IVsSccProject2 pscp2, uint itemid)
		{
			// NOTE: the function returns only a list of files, containing both regular files and special files
			// If you want to hide the special files (similar with solution explorer), you may need to return 
			// the special files in a hastable (key=master_file, values=special_file_list)

			// Initialize output parameters
			IList<string> sccFiles = new List<string>();
			if (pscp2 != null)
			{
				CALPOLESTR[] pathStr = new CALPOLESTR[1];
				CADWORD[] flags = new CADWORD[1];

				if (pscp2.GetSccFiles(itemid, pathStr, flags) == 0)
				{
					for (int elemIndex = 0; elemIndex < pathStr[0].cElems; elemIndex++)
					{
						IntPtr pathIntPtr = Marshal.ReadIntPtr(pathStr[0].pElems, elemIndex);
						String path = Marshal.PtrToStringAuto(pathIntPtr);

						sccFiles.Add(path);

						// See if there are special files
						if (flags.Length > 0 && flags[0].cElems > 0)
						{
							int flag = Marshal.ReadInt32(flags[0].pElems, elemIndex);

							if (flag != 0)
							{
								// We have special files
								CALPOLESTR[] specialFiles = new CALPOLESTR[1];
								CADWORD[] specialFlags = new CADWORD[1];

								pscp2.GetSccSpecialFiles(itemid, path, specialFiles, specialFlags);
								for (int i = 0; i < specialFiles[0].cElems; i++)
								{
									IntPtr specialPathIntPtr = Marshal.ReadIntPtr(
										specialFiles[0].pElems, i * IntPtr.Size);
									String specialPath = Marshal.PtrToStringAuto(specialPathIntPtr);

									sccFiles.Add(specialPath);
									Marshal.FreeCoTaskMem(specialPathIntPtr);
								}

								if (specialFiles[0].cElems > 0)
								{
									Marshal.FreeCoTaskMem(specialFiles[0].pElems);
								}
							}
						}

						Marshal.FreeCoTaskMem(pathIntPtr);
					}
					if (pathStr[0].cElems > 0)
					{
						Marshal.FreeCoTaskMem(pathStr[0].pElems);
					}
				}
			}

			return sccFiles;
		}

		/// <summary>
		/// Refreshes the glyphs of the specified hierarchy nodes
		/// </summary>
		public void RefreshNodesGlyphs(IList<VSITEMSELECTION> selectedNodes)
		{
			foreach (VSITEMSELECTION vsItemSel in selectedNodes)
			{
				var sccProject2 = vsItemSel.pHier as IVsSccProject2;
				if (vsItemSel.itemid == VSConstants.VSITEMID_ROOT)
				{
					if (sccProject2 == null)
					{
						// Note: The solution's hierarchy does not implement IVsSccProject2, IVsSccProject interfaces
						// It may be a pain to treat the solution as special case everywhere; a possible workaround is 
						// to implement a solution-wrapper class, that will implement IVsSccProject2, IVsSccProject and
						// IVsHierarhcy interfaces, and that could be used in provider's code wherever a solution is needed.
						// This approach could unify the treatment of solution and projects in the provider's code.

						// Until then, solution is treated as special case
						string[] rgpszFullPaths = new string[1];
						rgpszFullPaths[0] = GetSolutionFileName();
						VsStateIcon[] rgsiGlyphs = new VsStateIcon[1];
						uint[] rgdwSccStatus = new uint[1];
						sccService.GetSccGlyph(1, rgpszFullPaths, rgsiGlyphs, rgdwSccStatus);

						// Set the solution's glyph directly in the hierarchy
						IVsHierarchy solHier = (IVsHierarchy) GetService(typeof (SVsSolution));
						solHier.SetProperty(VSConstants.VSITEMID_ROOT,
						                    (int) __VSHPROPID.VSHPROPID_StateIconIndex,
						                    rgsiGlyphs[0]);
					}
					else
					{
						// Refresh all the glyphs in the project; the project will call back GetSccGlyphs() 
						// with the files for each node that will need new glyph
						sccProject2.SccGlyphChanged(0, null, null, null);
					}
				}
				else
				{
					// It may be easier/faster to simply refresh all the nodes in the project, 
					// and let the project call back on GetSccGlyphs, but just for the sake of the demo, 
					// let's refresh ourselves only one node at a time
					IList<string> sccFiles = GetNodeFiles(sccProject2, vsItemSel.itemid);

					// We'll use for the node glyph just the Master file's status (ignoring special files of the node)
					if (sccFiles.Count > 0)
					{
						string[] rgpszFullPaths = new string[1];
						rgpszFullPaths[0] = sccFiles[0];
						VsStateIcon[] rgsiGlyphs = new VsStateIcon[1];
						uint[] rgdwSccStatus = new uint[1];
						sccService.GetSccGlyph(1, rgpszFullPaths, rgsiGlyphs, rgdwSccStatus);

						uint[] rguiAffectedNodes = new uint[1];
						rguiAffectedNodes[0] = vsItemSel.itemid;
						sccProject2.SccGlyphChanged(1, rguiAffectedNodes, rgsiGlyphs,
						                            rgdwSccStatus);
					}
				}
			}
		}


		/// <summary>
		/// Returns the filename of the solution
		/// </summary>
		public string GetSolutionFileName()
		{
			var sol = (IVsSolution) GetService(typeof (SVsSolution));
			string solutionDirectory;
			string solutionFile;
			string solutionUserOptions;
			
			if (sol.GetSolutionInfo(out solutionDirectory, out solutionFile,
				                    out solutionUserOptions) == VSConstants.S_OK)
			{
				return solutionFile;
			}
			
			return null;
		}

		/// <summary>
		/// Returns the filename of the specified controllable project 
		/// </summary>
		public string GetProjectFileName(IVsSccProject2 pscp2Project)
		{
			// Note: Solution folders return currently a name like "NewFolder1{1DBFFC2F-6E27-465A-A16A-1AECEA0B2F7E}.storage"
			// Your provider may consider returning the solution file as the project name for the solution, if it has to persist some properties in the "project file"
			// UNDONE: What to return for web projects? They return a folder name, not a filename! Consider returning a pseudo-project filename instead of folder.

			var hierProject = (IVsHierarchy) pscp2Project;
			var project = (IVsProject) pscp2Project;

			// Attempt to get first the filename controlled by the root node 
			IList<string> sccFiles = GetNodeFiles(pscp2Project,
			                                      VSConstants.VSITEMID_ROOT);
			if (sccFiles.Count > 0 && !string.IsNullOrEmpty(sccFiles[0]))
			{
				return sccFiles[0];
			}

			// If that failed, attempt to get a name from the IVsProject interface
			string bstrMKDocument;
			if (	project.GetMkDocument(VSConstants.VSITEMID_ROOT, out bstrMKDocument) == VSConstants.S_OK
				&&	!string.IsNullOrEmpty(bstrMKDocument))
			{
				return bstrMKDocument;
			}

			// If that failes, attempt to get the filename from the solution
			var sol = (IVsSolution) GetService(typeof (SVsSolution));
			string uniqueName;
			if (	sol.GetUniqueNameOfProject(hierProject, out uniqueName) == VSConstants.S_OK
				&&	!string.IsNullOrEmpty(uniqueName))
			{
				// uniqueName may be a full-path or may be relative to the solution's folder
				if (uniqueName.Length > 2 && uniqueName[1] == ':')
				{
					return uniqueName;
				}

				// try to get the solution's folder and relativize the project name to it
				string solutionDirectory, solutionFile, solutionUserOptions;
				if (
					sol.GetSolutionInfo(out solutionDirectory, out solutionFile,
					                    out solutionUserOptions) == VSConstants.S_OK)
				{
					uniqueName = solutionDirectory + "\\" + uniqueName;

					// UNDONE: eliminate possible "..\\.." from path
					return uniqueName;
				}
			}

			// If that failed, attempt to get the project name from 
			string bstrName;
			if (
				hierProject.GetCanonicalName(VSConstants.VSITEMID_ROOT, out bstrName) ==
				VSConstants.S_OK)
			{
				return bstrName;
			}

			// if everything we tried fail, return null string
			return null;
		}

		private void DebugWalkingNode(IVsHierarchy pHier, uint itemid)
		{
			object property = null;
			if (
				pHier.GetProperty(itemid, (int) __VSHPROPID.VSHPROPID_Name, out property) ==
				VSConstants.S_OK)
			{
				Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture,
				                              "Walking hierarchy node: {0}",
				                              (string) property));
			}
		}

		/// <summary>
		/// Gets the list of ItemIDs that are nodes in the specified project
		/// </summary>
		private IList<uint> GetProjectItems(IVsHierarchy pHier)
		{
			// Start with the project root and walk all expandable nodes in the project
			return GetProjectItems(pHier, VSConstants.VSITEMID_ROOT);
		}

		/// <summary>
		/// Gets the list of ItemIDs that are nodes in the specified project, starting with the specified item
		/// </summary>
		private IList<uint> GetProjectItems(IVsHierarchy pHier, uint startItemid)
		{
			var projectNodes = new List<uint>();

			// The method does a breadth-first traversal of the project's hierarchy tree
			var nodesToWalk = new Queue<uint>();
			nodesToWalk.Enqueue(startItemid);

			while (nodesToWalk.Count > 0)
			{
				uint node = nodesToWalk.Dequeue();
				projectNodes.Add(node);

				DebugWalkingNode(pHier, node);

				object property = null;
				if (
					pHier.GetProperty(node, (int) __VSHPROPID.VSHPROPID_FirstChild,
					                  out property) == VSConstants.S_OK)
				{
					uint childnode = (uint) (int) property;
					if (childnode == VSConstants.VSITEMID_NIL)
					{
						continue;
					}

					DebugWalkingNode(pHier, childnode);

					if (
						(pHier.GetProperty(childnode, (int) __VSHPROPID.VSHPROPID_Expandable,
						                   out property) == VSConstants.S_OK &&
						 (int) property != 0) ||
						(pHier.GetProperty(childnode, (int) __VSHPROPID2.VSHPROPID_Container,
						                   out property) == VSConstants.S_OK && (bool) property))
					{
						nodesToWalk.Enqueue(childnode);
					}
					else
					{
						projectNodes.Add(childnode);
					}

					while (
						pHier.GetProperty(childnode, (int) __VSHPROPID.VSHPROPID_NextSibling,
						                  out property) == VSConstants.S_OK)
					{
						childnode = (uint) (int) property;
						if (childnode == VSConstants.VSITEMID_NIL)
						{
							break;
						}

						DebugWalkingNode(pHier, childnode);

						if (
							(pHier.GetProperty(childnode, (int) __VSHPROPID.VSHPROPID_Expandable,
							                   out property) == VSConstants.S_OK &&
							 (int) property != 0) ||
							(pHier.GetProperty(childnode, (int) __VSHPROPID2.VSHPROPID_Container,
							                   out property) == VSConstants.S_OK && (bool) property))
						{
							nodesToWalk.Enqueue(childnode);
						}
						else
						{
							projectNodes.Add(childnode);
						}
					}
				}
			}

			return projectNodes;
		}

		/// <summary>
		/// Gets the list of source controllable files in the specified project
		/// </summary>
		public IList<string> GetProjectFiles(IVsSccProject2 pscp2Project)
		{
			return GetProjectFiles(pscp2Project, VSConstants.VSITEMID_ROOT);
		}

		/// <summary>
		/// Gets the list of source controllable files in the specified project
		/// </summary>
		public IList<string> GetProjectFiles(IVsSccProject2 pscp2Project,
		                                     uint startItemId)
		{
			var projectFiles = new List<string>();
			var hierProject = (IVsHierarchy) pscp2Project;
			var projectItems = GetProjectItems(hierProject, startItemId);

			foreach (uint itemid in projectItems)
			{
				IList<string> sccFiles = GetNodeFiles(pscp2Project, itemid);
				foreach (string file in sccFiles)
				{
					projectFiles.Add(file);
				}
			}

			return projectFiles;
		}

		/// <summary>
		/// Checks whether the provider is invoked in command line mode
		/// </summary>
		public bool InCommandLineMode()
		{
			var shell = (IVsShell) GetService(typeof (SVsShell));
			object pvar;
			if (
				shell.GetProperty((int) __VSSPROPID.VSSPROPID_IsInCommandLineMode,
				                  out pvar) == VSConstants.S_OK &&
				(bool) pvar)
			{
				return true;
			}

			return false;
		}

		/// <summary>
		/// Checks whether the specified project is a solution folder
		/// </summary>
		public bool IsSolutionFolderProject(IVsHierarchy pHier)
		{
			var pFileFormat = pHier as IPersistFileFormat;
			if (pFileFormat != null)
			{
				Guid guidClassID;
				if (pFileFormat.GetClassID(out guidClassID) == VSConstants.S_OK &&
				    guidClassID.CompareTo(guidSolutionFolderProject) == 0)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Returns a list of solution folders projects in the solution
		/// </summary>
		public HashSet<IVsHierarchy> GetSolutionFoldersEnum()
		{
			var mapHierarchies = new HashSet<IVsHierarchy>();

			var sol = (IVsSolution) GetService(typeof (SVsSolution));
			Guid rguidEnumOnlyThisType = guidSolutionFolderProject;
			IEnumHierarchies ppenum = null;
			ErrorHandler.ThrowOnFailure(
				sol.GetProjectEnum((uint) __VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
				                   ref rguidEnumOnlyThisType, out ppenum));

			IVsHierarchy[] rgelt = new IVsHierarchy[1];
			uint pceltFetched = 0;
			while (ppenum.Next(1, rgelt, out pceltFetched) == VSConstants.S_OK &&
			       pceltFetched == 1)
			{
				mapHierarchies.Add(rgelt[0]);
			}

			return mapHierarchies;
		}

		#endregion
	}
}