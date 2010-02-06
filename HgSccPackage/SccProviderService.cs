//=========================================================================
// Copyright 2009 Sergey Antonov <sergant_@mail.ru>
// 
// This software may be used and distributed according to the terms of the
// GNU General Public License version 2 as published by the Free Software
// Foundation.
// 
// See the file COPYING.TXT for the full text of the license, or see
// http://www.gnu.org/licenses/gpl-2.0.txt
// 
//=========================================================================

using System;
using System.IO;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using C5;
using HgSccHelper;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using System.Windows.Forms;

namespace HgSccPackage
{
	[Guid("A7F26CA1-1000-4729-896E-0BBE9E380635")]
	public class SccProviderService : 
		IVsSccProvider,             // Required for provider registration with source control manager
		IVsSccManager2,             // Base source control functionality interface
		IVsSccManagerTooltip,       // Provide tooltips for source control items
		IVsSolutionEvents,          // We'll register for solution events, these are usefull for source control
		IVsSolutionEvents2,
		IVsQueryEditQuerySave2,     // Required to allow editing of controlled files 
		IVsTrackProjectDocumentsEvents2,  // Usefull to track project changes (add, renames, deletes, etc)
		IVsTrackProjectDocumentsEvents3,
		IVsRunningDocTableEvents,
		IDisposable 
	{
		// Whether the provider is active or not
		private bool _active = false;
		// The service and source control provider
		private SccProvider _sccProvider = null;
		// The cookie for solution events 
		private uint _vsSolutionEventsCookie;
		// The cookie for project document events
		private uint _tpdTrackProjectDocumentsCookie;
		// The list of controlled projects hierarchies
		private readonly C5.HashSet<IVsHierarchy> _controlledProjects = new C5.HashSet<IVsHierarchy>();
		private SccProviderStorage storage = new SccProviderStorage();
		// The list of controlled and offline projects hierarchies
		private readonly C5.HashSet<IVsHierarchy> _offlineProjects = new C5.HashSet<IVsHierarchy>();
		// Variable tracking whether the currently loading solution is controlled (during solution load or merge)
		private string _loadingControlledSolutionLocation = "";
		// The location of the currently controlled solution
		private string _solutionLocation;
		// The list of files approved for in-memory edit
		private readonly C5.HashSet<string> _approvedForInMemoryEdit = new C5.HashSet<string>();
		uint pdwCookie;

		private IVsRunningDocumentTable rdt;

//		private SccFileChangesManager file_changes;

		#region SccProvider Service initialization/unitialization

		public SccProviderService(SccProvider sccProvider)
		{
			Debug.Assert(null != sccProvider);
			_sccProvider = sccProvider;

			// Subscribe to solution events
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.AdviseSolutionEvents(this, out _vsSolutionEventsCookie);
			Debug.Assert(VSConstants.VSCOOKIE_NIL != _vsSolutionEventsCookie);

			// Subscribe to project documents
			var tpdService = (IVsTrackProjectDocuments2)_sccProvider.GetService(typeof(SVsTrackProjectDocuments));
			tpdService.AdviseTrackProjectDocumentsEvents(this, out _tpdTrackProjectDocumentsCookie);
			Debug.Assert(VSConstants.VSCOOKIE_NIL != _tpdTrackProjectDocumentsCookie);

			// Get RDT service 

			rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(SVsRunningDocumentTable));
			// Subscribe to RDT events               
			rdt.AdviseRunningDocTableEvents(this, out pdwCookie);

			storage.UpdateEvent += UpdateEvent_Handler;

//			file_changes = new SccFileChangesManager(_sccProvider);
		}

		public void Dispose()
		{
			// Unregister from receiving solution events
			if (VSConstants.VSCOOKIE_NIL != _vsSolutionEventsCookie)
			{
				var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
				sol.UnadviseSolutionEvents(_vsSolutionEventsCookie);
				_vsSolutionEventsCookie = VSConstants.VSCOOKIE_NIL;
			}

			// Unregister from receiving project documents
			if (VSConstants.VSCOOKIE_NIL != _tpdTrackProjectDocumentsCookie)
			{
				var tpdService = (IVsTrackProjectDocuments2)_sccProvider.GetService(typeof(SVsTrackProjectDocuments));
				tpdService.UnadviseTrackProjectDocumentsEvents(_tpdTrackProjectDocumentsCookie);
				_tpdTrackProjectDocumentsCookie = VSConstants.VSCOOKIE_NIL;
			}

			if (pdwCookie != VSConstants.VSCOOKIE_NIL)
			{
//				IVsRunningDocumentTable rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(SVsRunningDocumentTable));
				rdt.UnadviseRunningDocTableEvents(pdwCookie);
			}

//			file_changes.Dispose();

			storage.UpdateEvent -= UpdateEvent_Handler;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsSccProvider specific functions
		//--------------------------------------------------------------------------------
		#region IVsSccProvider interface functions

		// Called by the scc manager when the provider is activated. 
		// Make visible and enable if necessary scc related menu commands
		public int SetActive()
		{
			Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Provider set active"));

			_active = true;
			_sccProvider.OnActiveStateChange();

			return VSConstants.S_OK;
		}

		// Called by the scc manager when the provider is deactivated. 
		// Hides and disable scc related menu commands
		public int SetInactive()
		{
			Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Provider set inactive"));

			_active = false;
			_sccProvider.OnActiveStateChange();

			return VSConstants.S_OK;
		}

		public int AnyItemsUnderSourceControl(out int pfResult)
		{
			if (!_active)
			{
				pfResult = 0;
			}
			else
			{
				// Although the parameter is an int, it's in reality a BOOL value, so let's return 0/1 values
				pfResult = (_controlledProjects.Count != 0) ? 1 : 0;
			}
	
			return VSConstants.S_OK;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsSccManager2 specific functions
		//--------------------------------------------------------------------------------
		#region IVsSccManager2 interface functions

		public int BrowseForProject(out string pbstrDirectory, out int pfOK)
		{
			// Obsolete method
			pbstrDirectory = null;
			pfOK = 0;
			return VSConstants.E_NOTIMPL;
		}

		public int CancelAfterBrowseForProject() 
		{
			// Obsolete method
			return VSConstants.E_NOTIMPL;
		}

		/// <summary>
		/// Returns whether the source control provider is fully installed
		/// </summary>
		public int IsInstalled(out int pbInstalled)
		{
			// All source control packages should always return S_OK and set pbInstalled to nonzero
			pbInstalled = 1;
			return VSConstants.S_OK;
		}

		/// <summary>
		/// Provide source control icons for the specified files and returns scc status of files
		/// </summary>
		/// <returns>The method returns S_OK if at least one of the files is controlled, S_FALSE if none of them are</returns>
		public int GetSccGlyph( [InAttribute] int cFiles, [InAttribute] string[] rgpszFullPaths, [OutAttribute] VsStateIcon[] rgsiGlyphs, [OutAttribute] uint[] rgdwSccStatus )
		{
//			Debug.Assert(cFiles == 1, "Only getting one file icon at a time is supported");
			SourceControlStatus[] statuses = new SourceControlStatus[rgpszFullPaths.Length];
			GetStatusForFiles(rgpszFullPaths, statuses);

			//Iterate through all the files
			for (int iFile = 0; iFile < cFiles; iFile++)
			{
				// Return the icons and the status. While the status is a combination a flags, we'll return just values 
				// with one bit set, to make life easier for GetSccGlyphsFromStatus
				SourceControlStatus status = statuses[iFile];
				switch (status)
				{
					case SourceControlStatus.scsCheckedIn:
						rgsiGlyphs[iFile] = VsStateIcon.STATEICON_CHECKEDIN;
						if (rgdwSccStatus != null)
						{
							rgdwSccStatus[iFile] = (uint)__SccStatus.SCC_STATUS_CONTROLLED;
						}
						break;
					case SourceControlStatus.scsCheckedOut:
						rgsiGlyphs[iFile] = VsStateIcon.STATEICON_CHECKEDOUT;
						if (rgdwSccStatus != null)
						{
							rgdwSccStatus[iFile] = (uint)__SccStatus.SCC_STATUS_CHECKEDOUT;
						}
						break;
					default:
						//System.Collections.Generic.IList<VSITEMSELECTION> nodes = GetControlledProjectsContainingFile(rgpszFullPaths[i]);
						/*
											if (nodes.Count > 0)
											{
												// If the file is not controlled, but is member of a controlled project, report the item as checked out (same as source control in VS2003 did)
												// If the provider wants to have special icons for "pending add" files, the IVsSccGlyphs interface needs to be supported
												rgsiGlyphs[0] = VsStateIcon.STATEICON_CHECKEDOUT;
												if (rgdwSccStatus != null)
												{
													rgdwSccStatus[0] = (uint) __SccStatus.SCC_STATUS_CHECKEDOUT;
												}
											}
											else
						*/
						{
							// This is an uncontrolled file, return a blank scc glyph for it
							rgsiGlyphs[iFile] = VsStateIcon.STATEICON_BLANK;
							if (rgdwSccStatus != null)
							{
								rgdwSccStatus[iFile] = (uint)__SccStatus.SCC_STATUS_NOTCONTROLLED;
							}
						}
						break;
				}
			}

			return VSConstants.S_OK;
		}

		/// <summary>
		/// Determines the corresponding scc status glyph to display, given a combination of scc status flags
		/// </summary>
		public int GetSccGlyphFromStatus([InAttribute] uint dwSccStatus, [OutAttribute] VsStateIcon[] psiGlyph)
		{
			switch (dwSccStatus)
			{
				case (uint) __SccStatus.SCC_STATUS_CHECKEDOUT:
					psiGlyph[0] = VsStateIcon.STATEICON_CHECKEDOUT;
					break;
				case (uint) __SccStatus.SCC_STATUS_CONTROLLED:
					psiGlyph[0] = VsStateIcon.STATEICON_CHECKEDIN;
					break;
				default:
					psiGlyph[0] = VsStateIcon.STATEICON_BLANK;
					break;
			}
			return VSConstants.S_OK;
		}

		public uint SourceStatusToSccStatus(SourceControlStatus status)
		{
			switch (status)
			{
				case SourceControlStatus.scsCheckedIn:
					return (uint) __SccStatus.SCC_STATUS_CONTROLLED;
				case SourceControlStatus.scsCheckedOut:
					return (uint) __SccStatus.SCC_STATUS_CHECKEDOUT;
				case SourceControlStatus.scsUncontrolled:
					return (uint) __SccStatus.SCC_STATUS_NOTCONTROLLED;
			}

			return (uint)__SccStatus.SCC_STATUS_NOTCONTROLLED;
		}

		/// <summary>
		/// One of the most important methods in a source control provider, is called by projects that are under source control when they are first opened to register project settings
		/// </summary>
		public int RegisterSccProject([InAttribute] IVsSccProject2 pscp2Project, [InAttribute] string pszSccProjectName, [InAttribute] string pszSccAuxPath, [InAttribute] string pszSccLocalPath, [InAttribute] string pszProvider)
		{
			if (pszProvider.CompareTo(_sccProvider.ProviderName)!=0)
			{
				// If the provider name controlling this project is not our provider, the user may be adding to a 
				// solution controlled by this provider an existing project controlled by some other provider.
				// We'll deny the registration with scc in such case.
				return VSConstants.E_FAIL;
			}

			Logger.WriteLine("RegisterSccProject: sln = '{0}'", _sccProvider.GetSolutionFileName());

			if (pscp2Project == null)
			{
//				Logger.WriteLine("RegisterSccProject: adding solution");
				// Manual registration with source control of the solution, from OnAfterOpenSolution
				Logger.WriteLine("Solution {0} is registering with source control - {1}, {2}, {3}, {4}", _sccProvider.GetSolutionFileName(), pszSccProjectName, pszSccAuxPath, pszSccLocalPath, pszProvider);

				var solHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
				string solutionFile = _sccProvider.GetSolutionFileName();
				var work_dir = Path.GetDirectoryName(solutionFile);

				// FIXME: ��������� ��� ���� � �������� ���� �� �������� ��� � �������
				// � ����� ��� � ������� ��� �����������
				if (!storage.IsValid)
				{
					var err = storage.Init(work_dir, SccOpenProjectFlags.CreateIfNew);
					if (err != SccErrors.Ok)
						return VSConstants.E_FAIL;
				}
/*
				var storage = new SccProviderStorage(solutionFile);
				_controlledProjects[solHier] = storage;
*/
				_controlledProjects.Add(solHier);
			}
			else
			{
				Logger.WriteLine("Project {0} is registering with source control - {1}, {2}, {3}, {4}", _sccProvider.GetProjectFileName(pscp2Project), pszSccProjectName, pszSccAuxPath, pszSccLocalPath, pszProvider);

				// Add the project to the list of controlled projects
				var hierProject = (IVsHierarchy)pscp2Project;
				var project_path = _sccProvider.GetProjectFileName(pscp2Project);
				string solutionFile = _sccProvider.GetSolutionFileName();
				var work_dir = Path.GetDirectoryName(solutionFile);
//				Logger.WriteLine("RegisterSccProject: adding project = '{0}'", project_path);

				if (!storage.IsValid)
				{
					var err = storage.Init(work_dir, SccOpenProjectFlags.CreateIfNew);
					if (err != SccErrors.Ok)
						return VSConstants.E_FAIL;
				}
				_controlledProjects.Add(hierProject);
			}

			return VSConstants.S_OK;
		}

		/// <summary>
		/// Called by projects registered with the source control portion of the environment before they are closed. 
		/// </summary>
		public int UnregisterSccProject([InAttribute] IVsSccProject2 pscp2Project)
		{
			// Get the project's hierarchy
			IVsHierarchy hierProject = null;
			if (pscp2Project == null)
			{
				// If the project's pointer is null, it must be the solution calling to unregister, from OnBeforeCloseSolution
				Logger.WriteLine("Solution {0} is unregistering with source control.", _sccProvider.GetSolutionFileName());
				hierProject = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
			}
			else
			{
				Logger.WriteLine("Project {0} is unregistering with source control.", _sccProvider.GetProjectFileName(pscp2Project));
				hierProject = (IVsHierarchy)pscp2Project;
			}

			// Remove the project from the list of controlled projects
			if (_controlledProjects.Contains(hierProject))
			{
				_controlledProjects.Remove(hierProject);
				return VSConstants.S_OK;
			}
			
			return VSConstants.S_FALSE;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsSccManagerTooltip specific functions
		//--------------------------------------------------------------------------------
		#region IVsSccManagerTooltip interface functions

		/// <summary>
		/// Called by solution explorer to provide tooltips for items. Returns a text describing the source control status of the item.
		/// </summary>
		public int GetGlyphTipText([InAttribute] IVsHierarchy phierHierarchy, [InAttribute] uint itemidNode, out string pbstrTooltipText)
		{
			// Initialize output parameters
			pbstrTooltipText = "";

			System.Collections.Generic.IList<string> files = _sccProvider.GetNodeFiles(phierHierarchy, itemidNode);
			if (files.Count == 0)
			{
				return VSConstants.S_OK;
			}

			// Return the glyph text based on the first file of node (the master file)
			SourceControlStatus status = GetFileStatus(files[0]);
			switch (status)
			{
				case SourceControlStatus.scsCheckedIn:
					pbstrTooltipText = Resources.ResourceManager.GetString("Status_CheckedIn"); 
					break;
				case SourceControlStatus.scsCheckedOut:
					pbstrTooltipText = Resources.ResourceManager.GetString("Status_CheckedOut");
					break;
				default:
					// If the file is not controlled, but is member of a controlled project, report the item as checked out (same as source control in VS2003 did)
					// If the provider wants to have special icons for "pending add" files, the IVsSccGlyphs interface needs to be supported
					System.Collections.Generic.IList<VSITEMSELECTION> nodes = GetControlledProjectsContainingFile(files[0]);
					if (nodes.Count > 0)
					{
//						pbstrTooltipText = Resources.ResourceManager.GetString("Status_PendingAdd");
						pbstrTooltipText = Resources.ResourceManager.GetString("Status_Uncontrolled");
					}
					break;
			}

			return VSConstants.S_OK;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsSolutionEvents and IVsSolutionEvents2 specific functions
		//--------------------------------------------------------------------------------
		#region IVsSolutionEvents interface functions

		public int OnAfterCloseSolution([InAttribute] Object pUnkReserved)
		{
			// Reset all source-control-related data now that solution is closed
			_controlledProjects.Clear();
			_offlineProjects.Clear();
			_sccProvider.SolutionHasDirtyProps = false;
			_loadingControlledSolutionLocation = "";
			_solutionLocation = "";
			_approvedForInMemoryEdit.Clear();
			storage.Close();

			return VSConstants.S_OK;
		}

		public int OnAfterLoadProject([InAttribute] IVsHierarchy pStubHierarchy, [InAttribute] IVsHierarchy pRealHierarchy)
		{
			Logger.WriteLine("OnAfterLoadProject: {0}", pRealHierarchy);
			return VSConstants.S_OK;
		}

		public int OnAfterOpenProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fAdded)
		{
			Logger.WriteLine("OnAfterOpenProject: {0}, added = {1}", pHierarchy, fAdded);

			// If a solution folder is added to the solution after the solution is added to scc, we need to controll that folder
			if (_sccProvider.IsSolutionFolderProject(pHierarchy) && (fAdded == 1))
			{
				var solHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
				if (IsProjectControlled(solHier))
				{
					// Register this solution folder using the same location as the solution
					var pSccProject = (IVsSccProject2)pHierarchy;
					RegisterSccProject(pSccProject, _solutionLocation, "", "", _sccProvider.ProviderName);

					// We'll also need to refresh the solution folders glyphs to reflect the controlled state
					System.Collections.Generic.IList<VSITEMSELECTION> nodes = new List<VSITEMSELECTION>();

					VSITEMSELECTION vsItem;
					vsItem.itemid = VSConstants.VSITEMID_ROOT;
					vsItem.pHier = pHierarchy;
					nodes.Add(vsItem);

					_sccProvider.RefreshNodesGlyphs(nodes);
				}
			}

			return VSConstants.S_OK;
		}

		public int OnAfterOpenSolution([InAttribute] Object pUnkReserved, [InAttribute] int fNewSolution)
		{
			// This event is fired last by the shell when opening a solution.
			// By this time, we have already loaded the solution persistence data from the PreLoad section
			// the controlled projects should be opened and registered with source control
			if (_loadingControlledSolutionLocation.Length > 0)
			{
				// We'll also need to refresh the solution glyphs to reflect the controlled state
				System.Collections.Generic.IList<VSITEMSELECTION> nodes = new List<VSITEMSELECTION>();

				// If the solution was controlled, now it is time to register the solution hierarchy with souce control, too.
				// Note that solution is not calling RegisterSccProject(), the scc package will do this call as it knows the source control location
				RegisterSccProject(null, _loadingControlledSolutionLocation, "", "", _sccProvider.ProviderName);

				VSITEMSELECTION vsItem;
				vsItem.itemid = VSConstants.VSITEMID_ROOT;
				vsItem.pHier = null;
				nodes.Add(vsItem);

				// Also, solution folders won't call RegisterSccProject, so we have to enumerate them and register them with scc once the solution is controlled
				var enumSolFolders = _sccProvider.GetSolutionFoldersEnum();
				foreach (IVsHierarchy pHier in enumSolFolders)
				{
					// Register this solution folder using the same location as the solution
					var pSccProject = (IVsSccProject2)pHier;
					RegisterSccProject(pSccProject, _loadingControlledSolutionLocation, "", "", _sccProvider.ProviderName);

					vsItem.itemid = VSConstants.VSITEMID_ROOT;
					vsItem.pHier = pHier;
					nodes.Add(vsItem);
				}

				// Refresh the glyphs now for solution and solution folders
				_sccProvider.RefreshNodesGlyphs(nodes);
			}

			_solutionLocation = _loadingControlledSolutionLocation;

			// reset the flag now that solution open completed
			_loadingControlledSolutionLocation = "";

			return VSConstants.S_OK;
		}

		public int OnBeforeCloseProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fRemoved)
		{
			Logger.WriteLine("OnBeforeCloseProject: {0}", pHierarchy);
			return VSConstants.S_OK;
		}

		public int OnBeforeCloseSolution([InAttribute] Object pUnkReserved)
		{
			// Since we registered the solution with source control from OnAfterOpenSolution, it would be nice to unregister it, too, when it gets closed.
			// Also, unregister the solution folders
			var enumSolFolders = _sccProvider.GetSolutionFoldersEnum();
			foreach (IVsHierarchy pHier in enumSolFolders)
			{
				var pSccProject = (IVsSccProject2)pHier;
				UnregisterSccProject(pSccProject);
			}

			UnregisterSccProject(null);

			return VSConstants.S_OK;
		}

		public int OnBeforeUnloadProject([InAttribute] IVsHierarchy pRealHierarchy, [InAttribute] IVsHierarchy pStubHierarchy)
		{
			Logger.WriteLine("OnBeforeUnloadProject: {0}", pRealHierarchy);
			return VSConstants.S_OK;
		}

		public int OnQueryCloseProject([InAttribute] IVsHierarchy pHierarchy, [InAttribute] int fRemoving, [InAttribute] ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnQueryCloseSolution([InAttribute] Object pUnkReserved, [InAttribute] ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnQueryUnloadProject([InAttribute] IVsHierarchy pRealHierarchy, [InAttribute] ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnAfterMergeSolution ([InAttribute] Object pUnkReserved )
		{
			// reset the flag now that solutions were merged and the merged solution completed opening
			_loadingControlledSolutionLocation = "";

			return VSConstants.S_OK;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsQueryEditQuerySave2 specific functions
		//--------------------------------------------------------------------------------
		#region IVsQueryEditQuerySave2 interface functions

		public int BeginQuerySaveBatch ()
		{
			Logger.WriteLine("BeginQuerySaveBatch");
			return VSConstants.S_OK;
		}

		public int EndQuerySaveBatch ()
		{
			Logger.WriteLine("EndQuerySaveBatch");
			return VSConstants.S_OK;
		}

		public int DeclareReloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
		{
			return VSConstants.S_OK;
		}

		public int DeclareUnreloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
		{
			return VSConstants.S_OK;
		}

		public int IsReloadable ([InAttribute] string pszMkDocument, out int pbResult )
		{
			// Since we're not tracking which files are reloadable and which not, consider everything reloadable
			pbResult = 1;
			return VSConstants.S_OK;
		}

		public int OnAfterSaveUnreloadableFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo)
		{
			Logger.WriteLine("OnAfterSaveUnreloadableFile: {0}", pszMkDocument);
			return VSConstants.S_OK;
		}

		/// <summary>
		/// Called by projects and editors before modifying a file
		/// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
		/// to make the file writable in order to allow the edit to continue
		///
		/// There are a lot of cases to deal with during QueryEdit/QuerySave. 
		/// - called in commmand line mode, when UI cannot be displayed
		/// - called during builds, when save shoudn't probably be allowed
		/// - called during projects migration, when projects are not open and not registered yet with source control
		/// - checking out files may bring new versions from vss database which may be reloaded and the user may lose in-memory changes; some other files may not be reloadable
		/// - not all editors call QueryEdit when they modify the file the first time (buggy editors!), and the files may be already dirty in memory when QueryEdit is called
		/// - files on disk may be modified outside IDE and may have attributes incorrect for their scc status
		/// - checkouts may fail
		/// The sample provider won't deal with all these situations, but a real source control provider should!
		/// </summary>
		public int QueryEditFiles([InAttribute] uint rgfQueryEdit, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgrgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] rgFileInfo, out uint pfEditVerdict, out uint prgfMoreInfo)
		{
			// Initialize output variables
			pfEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
			prgfMoreInfo = 0;

			// In non-UI mode just allow the edit, because the user cannot be asked what to do with the file
			if (_sccProvider.InCommandLineMode())
			{
				return VSConstants.S_OK;
			}

			try 
			{
				SourceControlStatus[] statuses = new SourceControlStatus[rgpszMkDocuments.Length];
				GetStatusForFiles(rgpszMkDocuments, statuses);

				//Iterate through all the files
				for (int iFile = 0; iFile < cFiles; iFile++)
				{
					Logger.WriteLine("QueryEditFiles: [{0}]: {1}", iFile, rgpszMkDocuments[iFile]);
					 
					uint fEditVerdict = (uint)tagVSQueryEditResult.QER_EditNotOK;
					uint fMoreInfo = 0;

					// Because of the way we calculate the status, it is not possible to have a 
					// checked in file that is writtable on disk, or a checked out file that is read-only on disk
					// A source control provider would need to deal with those situations, too
//					SourceControlStatus status = GetFileStatus(rgpszMkDocuments[iFile]);
					SourceControlStatus status = statuses[iFile];
					bool fileExists = File.Exists(rgpszMkDocuments[iFile]);
					bool isFileReadOnly = false;
					if (fileExists)
					{
						isFileReadOnly = (( File.GetAttributes(rgpszMkDocuments[iFile]) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
					}

					// Allow the edits if the file does not exist or is writable
					if (!fileExists || !isFileReadOnly)
					{
						fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
					}
					else
					{
						// If the IDE asks about a file that was already approved for in-memory edit, allow the edit without asking the user again
						if (_approvedForInMemoryEdit.Contains(rgpszMkDocuments[iFile].ToLower()))
						{
							fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
							fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_InMemoryEdit);
						}
						else
						{
							switch (status)
							{
								case SourceControlStatus.scsCheckedIn:
									if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_ReportOnly) != 0)
									{
										fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | tagVSQueryEditResultFlags.QER_ReadOnlyUnderScc);
									}
									else
									{
/*
										DlgQueryEditCheckedInFile dlgAskCheckout = new DlgQueryEditCheckedInFile(rgpszMkDocuments[iFile]);
										if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_SilentMode) != 0)
										{
											// When called in silent mode, attempt the checkout
											// (The alternative is to deny the edit and return QER_NoisyPromptRequired and expect for a non-silent call)
											dlgAskCheckout.Answer = DlgQueryEditCheckedInFile.qecifCheckout;
										}
										else
										{
											dlgAskCheckout.ShowDialog();
										}

										if (dlgAskCheckout.Answer == DlgQueryEditCheckedInFile.qecifCheckout)
										{
											// Checkout the file, and since it cannot fail, allow the edit
											CheckoutFileAndRefreshProjectGlyphs(rgpszMkDocuments[iFile]);
											fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
											fMoreInfo = (uint)tagVSQueryEditResultFlags.QER_MaybeCheckedout;
											// Do not forget to set QER_Changed if the content of the file on disk changes during the query edit
											// Do not forget to set QER_Reloaded if the source control reloads the file from disk after such changing checkout.
										}
										else if (dlgAskCheckout.Answer == DlgQueryEditCheckedInFile.qecifEditInMemory)
										{
											// Allow edit in memory
											fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
											fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_InMemoryEdit);
											// Add the file to the list of files approved for edit, so if the IDE asks again about this file, we'll allow the edit without asking the user again
											// UNDONE: Currently, a file gets removed from _approvedForInMemoryEdit list only when the solution is closed. Consider intercepting the 
											// IVsRunningDocTableEvents.OnAfterSave/OnAfterSaveAll interface and removing the file from the approved list after it gets saved once.
											_approvedForInMemoryEdit.Add(rgpszMkDocuments[iFile].ToLower());
										}
										else
										{
											fEditVerdict = (uint)tagVSQueryEditResult.QER_NoEdit_UserCanceled;
											fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_ReadOnlyUnderScc | tagVSQueryEditResultFlags.QER_CheckoutCanceledOrFailed);
										}
*/
										fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
										fMoreInfo = (uint)tagVSQueryEditResultFlags.QER_MaybeCheckedout;
									}
									break;
								case SourceControlStatus.scsCheckedOut: // fall through
								case SourceControlStatus.scsUncontrolled:
									if (fileExists && isFileReadOnly)
									{
										if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_ReportOnly) != 0)
										{
											fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | tagVSQueryEditResultFlags.QER_ReadOnlyNotUnderScc);
										}
										else
										{
											bool fChangeAttribute = false;
											if ((rgfQueryEdit & (uint)tagVSQueryEditFlags.QEF_SilentMode) != 0)
											{
												// When called in silent mode, deny the edit and return QER_NoisyPromptRequired and expect for a non-silent call)
												// (The alternative is to silently make the file writable and accept the edit)
												fMoreInfo = (uint)(tagVSQueryEditResultFlags.QER_EditNotPossible | tagVSQueryEditResultFlags.QER_ReadOnlyNotUnderScc | tagVSQueryEditResultFlags.QER_NoisyPromptRequired );
											}
											else
											{
												// This is a controlled file, warn the user
												IVsUIShell uiShell = (IVsUIShell)_sccProvider.GetService(typeof(SVsUIShell));
												Guid clsid = Guid.Empty;
												int result = VSConstants.S_OK;
												string messageText = Resources.ResourceManager.GetString("QEQS_EditUncontrolledReadOnly");
												string messageCaption = Resources.ResourceManager.GetString("ProviderName");
												if (uiShell.ShowMessageBox(0, ref clsid,
																	messageCaption,
																	String.Format(CultureInfo.CurrentUICulture, messageText, rgpszMkDocuments[iFile]),
																	string.Empty,
																	0,
																	OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
																	OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
																	OLEMSGICON.OLEMSGICON_QUERY,
																	0,        // false = application modal; true would make it system modal
																	out result) == VSConstants.S_OK
													&& result == (int)DialogResult.Yes)
												{
													fChangeAttribute = true;
												}
											}

											if (fChangeAttribute)
											{
												// Make the file writable and allow the edit
												File.SetAttributes(rgpszMkDocuments[iFile], FileAttributes.Normal);
												fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
											}
										}
									}
									else
									{
										fEditVerdict = (uint)tagVSQueryEditResult.QER_EditOK;
									}
									break;
							}
						}
					}

					// It's a bit unfortunate that we have to return only one set of flags for all the files involved in the operation
					// The edit can continue if all the files were approved for edit
					prgfMoreInfo |= fMoreInfo;
					pfEditVerdict |= fEditVerdict;
				}
			}
			catch(Exception)
			{
				// If an exception was caught, do not allow the edit
				pfEditVerdict = (uint)tagVSQueryEditResult.QER_EditNotOK;
				prgfMoreInfo = (uint)tagVSQueryEditResultFlags.QER_EditNotPossible;
			}

			return VSConstants.S_OK;
		}

		/// <summary>
		/// Called by editors and projects before saving the files
		/// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
		/// to make the file writable in order to allow the file saving to continue
		/// </summary>
		public int QuerySaveFile([InAttribute] string pszMkDocument, [InAttribute] uint rgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] pFileInfo, out uint pdwQSResult)
		{
			// Delegate to the other QuerySave function
			string[] rgszDocuements = new string[1];
			uint[] rgrgf = new uint[1];
			rgszDocuements[0] = pszMkDocument;
			rgrgf[0] = rgf;
			return QuerySaveFiles(rgf, 1, rgszDocuements, rgrgf, pFileInfo, out pdwQSResult);
		}

		/// <summary>
		/// Called by editors and projects before saving the files
		/// The function allows the source control systems to take the necessary actions (checkout, flip attributes)
		/// to make the file writable in order to allow the file saving to continue
		/// </summary>
		public int QuerySaveFiles([InAttribute] uint rgfQuerySave, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgrgf, [InAttribute] VSQEQS_FILE_ATTRIBUTE_DATA[] rgFileInfo, out uint pdwQSResult)
		{
//			IVsRunningDocumentTable rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(IVsRunningDocumentTable));

			var checkout_files = new List<string>();

			// Initialize output variables
			// It's a bit unfortunate that we have to return only one set of flags for all the files involved in the operation
			// The last file will win setting this flag
			pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;

			// In non-UI mode attempt to silently flip the attributes of files or check them out 
			// and allow the save, because the user cannot be asked what to do with the file
			if (_sccProvider.InCommandLineMode())
			{
				rgfQuerySave = rgfQuerySave | (uint)tagVSQuerySaveFlags.QSF_SilentMode;
			}

			try 
			{
				SourceControlStatus[] statuses = new SourceControlStatus[rgpszMkDocuments.Length];
				GetStatusForFiles(rgpszMkDocuments, statuses);

				for (int iFile = 0; iFile < cFiles; iFile++)
				{
					Logger.WriteLine("QuerySaveFiles: [{0}]: {1}", iFile, rgpszMkDocuments[iFile]);
//					SourceControlStatus status = GetFileStatus(rgpszMkDocuments[iFile]);
					SourceControlStatus status = statuses[iFile];
					bool fileExists = File.Exists(rgpszMkDocuments[iFile]);
					bool isFileReadOnly = false;
					if (fileExists)
					{
						isFileReadOnly = ((File.GetAttributes(rgpszMkDocuments[iFile]) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);
					}

					switch (status)
					{
/*
						case SourceControlStatus.scsCheckedIn:
							DlgQuerySaveCheckedInFile dlgAskCheckout = new DlgQuerySaveCheckedInFile(rgpszMkDocuments[iFile]);
							if ((rgfQuerySave & (uint)tagVSQuerySaveFlags.QSF_SilentMode) != 0)
							{
								// When called in silent mode, attempt the checkout
								// (The alternative is to deny the save, return QSR_NoSave_NoisyPromptRequired and expect for a non-silent call)
								dlgAskCheckout.Answer = DlgQuerySaveCheckedInFile.qscifCheckout;
							}
							else
							{
								dlgAskCheckout.ShowDialog();
							}

							switch (dlgAskCheckout.Answer)
							{
								case DlgQueryEditCheckedInFile.qecifCheckout:
									// Checkout the file, and since it cannot fail, allow the save to continue
									CheckoutFileAndRefreshProjectGlyphs(rgpszMkDocuments[iFile]);
									pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
									break;
								case DlgQuerySaveCheckedInFile.qscifForceSaveAs:
									pdwQSResult = (uint)tagVSQuerySaveResult.QSR_ForceSaveAs;
									break;
								case DlgQuerySaveCheckedInFile.qscifSkipSave:
									pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Continue;
									break;
								default:
									pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Cancel;
									break;
							}

							break;
*/
						case SourceControlStatus.scsCheckedIn:
							{
								if (fileExists && isFileReadOnly)
								{
									// Make the file writable and allow the save
									File.SetAttributes(rgpszMkDocuments[iFile], FileAttributes.Normal);
								}
								// TODO: Add dirty file to checkout_files
//								rdt.GetDocumentInfo()
								checkout_files.Add(rgpszMkDocuments[iFile]);
								// Allow the save now 
								pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
								break;
							}
						case SourceControlStatus.scsCheckedOut:	 // fall through
						case SourceControlStatus.scsUncontrolled:
							if (fileExists && isFileReadOnly)
							{
								// Make the file writable and allow the save
								File.SetAttributes(rgpszMkDocuments[iFile], FileAttributes.Normal);
							}
							// Allow the save now 
							pdwQSResult = (uint)tagVSQuerySaveResult.QSR_SaveOK;
							break;
					}
				}
			}
			catch (Exception)
			{
				// If an exception was caught, do not allow the save
				pdwQSResult = (uint)tagVSQuerySaveResult.QSR_NoSave_Cancel;
			}

			// FIXME: ���������� �� ���������� ������ ������ ������
/*
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);
*/

/*
			IList<VSITEMSELECTION> lst = GetControlledProjectsContainingFiles(checkout_files);
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));

			foreach (var item in lst)
			{
				var proj = item.pHier as IVsProject2;
				if (proj == null)
				{
					// solution
					Logger.WriteLine("QuerySaveFile, solution");
					sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);
				}
				else
				{
					object obj_doc_cookie;

					var err = item.pHier.GetProperty(item.itemid, (int)__VSHPROPID.VSHPROPID_ItemDocCookie, out obj_doc_cookie);
					if (err != VSConstants.S_OK)
						continue;

					uint doc_cookie = (uint)(int)obj_doc_cookie;

					string doc_path;
					err = proj.GetMkDocument(item.itemid, out doc_path);
					if (err == VSConstants.S_OK)
					{
						sol.SaveSolutionElement((uint) __VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, doc_cookie);
					}
				}
			}
*/

/*
			foreach (var file in checkout_files)
			{
				storage.SetCacheStatus(file, SourceControlStatus.scsCheckedOut);
			}
*/

/*
			IList<VSITEMSELECTION> lst = GetControlledProjectsContainingFiles(checkout_files);
			if (lst.Count != 0)
			{
				_sccProvider.RefreshNodesGlyphs(lst);
			}
*/

			return VSConstants.S_OK;
		}

		#endregion

		//--------------------------------------------------------------------------------
		// IVsTrackProjectDocumentsEvents2 specific functions
		//--------------------------------------------------------------------------------

		public int OnQueryAddFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYADDFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYADDFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYADDFILERESULTS[] rgResults)
		{
			pSummaryResult[0] = VSQUERYADDFILERESULTS.VSQUERYADDFILERESULTS_AddOK;
			if (rgResults != null)
			{
				for (int iFile = 0; iFile < cFiles; iFile++)
				{
					Logger.WriteLine("OnQueryAddFiles: [{0}]: {1}", iFile, rgpszMkDocuments[iFile]);
					rgResults[iFile] = VSQUERYADDFILERESULTS.VSQUERYADDFILERESULTS_AddOK;
				}
			}

			try
			{
				var sccProject = pProject as IVsSccProject2;
				var pHier = pProject as IVsHierarchy;
				string projectName = null;
				if (sccProject == null)
				{
					// This is the solution calling
					pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
					projectName = _sccProvider.GetSolutionFileName();
				}
				else
				{
					// If the project doesn't support source control, it will be skipped
					if (sccProject != null)
					{
						projectName = _sccProvider.GetProjectFileName(sccProject);
					}
				}

				if (projectName != null)
				{
					for (int iFile = 0; iFile < cFiles; iFile++)
					{
						//                        var storage = _controlledProjects[pHier];
						if (storage != null)
						{
							SourceControlStatus status = storage.GetFileStatus(rgpszMkDocuments[iFile]);
							if (status != SourceControlStatus.scsUncontrolled)
							{
							}
						}
					}
				}
			}
			catch (Exception)
			{
				pSummaryResult[0] = VSQUERYADDFILERESULTS.VSQUERYADDFILERESULTS_AddNotOK;
				if (rgResults != null)
				{
					for (int iFile = 0; iFile < cFiles; iFile++)
					{
						rgResults[iFile] = VSQUERYADDFILERESULTS.VSQUERYADDFILERESULTS_AddNotOK;
					}
				}
			}

			return VSConstants.S_OK;
		}

		/// <summary>
		/// Implement this function to update the project scc glyphs when the items are added to the project.
		/// If a project doesn't call GetSccGlyphs as they should do (as solution folder do), this will update correctly the glyphs when the project is controled
		/// </summary>
		public int OnAfterAddFilesEx([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSADDFILEFLAGS[] rgFlags)
		{
			var files = new List<string>();
			var selected_items = new List<VSITEMSELECTION>();

			// Start by iterating through all projects calling this function
			for (int iProject = 0; iProject < cProjects; iProject++)
			{
				var sccProject = rgpProjects[iProject] as IVsSccProject2;
				var pHier = rgpProjects[iProject] as IVsHierarchy;

				// If the project is not controllable, or is not controlled, skip it
				if (sccProject == null || !IsProjectControlled(pHier))
				{
					continue;
				}

				int iProjectFilesStart = rgFirstIndices[iProject];
				int iNextProjecFilesStart = cFiles;
				if (iProject < cProjects - 1)
				{
					iNextProjecFilesStart = rgFirstIndices[iProject + 1];
				}

				// Now that we know which files belong to this project, iterate the project files
				for (int iFile = iProjectFilesStart; iFile < iNextProjecFilesStart; iFile++)
				{
					// Refresh the solution explorer glyphs for all projects containing this file
					System.Collections.Generic.IList<VSITEMSELECTION> nodes = GetControlledProjectsContainingFile(rgpszMkDocuments[iFile]);
					files.Add(rgpszMkDocuments[iFile]);
					selected_items.AddRange(nodes);
				}
			}

			storage.AddFilesToStorage(files);
			_sccProvider.RefreshNodesGlyphs(selected_items);

			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
				//				return;
			}

			return VSConstants.S_OK;
		}

		public int OnQueryAddDirectories ([InAttribute] IVsProject pProject, [InAttribute] int cDirectories, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYADDDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYADDDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYADDDIRECTORYRESULTS[] rgResults)
		{
			return VSConstants.E_NOTIMPL;
		}

		public int OnAfterAddDirectoriesEx ([InAttribute] int cProjects, [InAttribute] int cDirectories, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSADDDIRECTORYFLAGS[] rgFlags)
		{
			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
				//				return;
			}
			return VSConstants.S_OK;
		}

		/// <summary>
		/// Implement OnQueryRemoveFilesevent to warn the user when he's deleting controlled files.
		/// The user gets the chance to cancel the file removal.
		/// </summary>
		public int OnQueryRemoveFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYREMOVEFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYREMOVEFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYREMOVEFILERESULTS[] rgResults)
		{
			pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveOK;
			if (rgResults != null)
			{
				for (int iFile = 0; iFile < cFiles; iFile++)
				{
					Logger.WriteLine("OnQueryRemoveFiles: [{0}]: {1}", iFile, rgpszMkDocuments[iFile]);
					rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveOK;
				}
			}

			try
			{
				var sccProject = pProject as IVsSccProject2;
				var pHier = pProject as IVsHierarchy;
				string projectName = null;
				if (sccProject == null)
				{
					// This is the solution calling
					pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
					projectName = _sccProvider.GetSolutionFileName();
				}
				else
				{
					// If the project doesn't support source control, it will be skipped
					if (sccProject != null)
					{
						projectName = _sccProvider.GetProjectFileName(sccProject);
					}
				}
				
                if (projectName != null)
				{
					for (int iFile = 0; iFile < cFiles; iFile++)
					{
//                        var storage = _controlledProjects[pHier];
						if (storage != null)
						{
							SourceControlStatus status = storage.GetFileStatus(rgpszMkDocuments[iFile]);
							if (status != SourceControlStatus.scsUncontrolled)
							{
/*
								// This is a controlled file, warn the user
								IVsUIShell uiShell = (IVsUIShell)_sccProvider.GetService(typeof(SVsUIShell));
								Guid clsid = Guid.Empty;
								int result = VSConstants.S_OK;
								string messageText = Resources.ResourceManager.GetString("TPD_DeleteControlledFile"); 
								string messageCaption = Resources.ResourceManager.GetString("ProviderName");
								if (uiShell.ShowMessageBox(0, ref clsid,
													messageCaption,
													String.Format(CultureInfo.CurrentUICulture, messageText, rgpszMkDocuments[iFile]),
													string.Empty,
													0,
													OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
													OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
													OLEMSGICON.OLEMSGICON_QUERY,
													0,        // false = application modal; true would make it system modal
													out result) != VSConstants.S_OK
									|| result != (int)DialogResult.Yes)
								{
									pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
									if (rgResults != null)
									{
										rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
									}
									// Don't spend time iterating through the rest of the files once the rename has been cancelled
									break;
								}
*/
							}
						}
					}
				}
			}
			catch (Exception)
			{
				pSummaryResult[0] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
				if (rgResults != null)
				{
					for (int iFile = 0; iFile < cFiles; iFile++)
					{
						rgResults[iFile] = VSQUERYREMOVEFILERESULTS.VSQUERYREMOVEFILERESULTS_RemoveNotOK;
					}
				}
			}
			
			return VSConstants.S_OK;
		}

		public int OnAfterRemoveFiles([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSREMOVEFILEFLAGS[] rgFlags)
		{
			var files = new List<string>();

			// Start by iterating through all projects calling this function
			for (int iProject = 0; iProject < cProjects; iProject++)
			{
				var sccProject = rgpProjects[iProject] as IVsSccProject2;
				var pHier = rgpProjects[iProject] as IVsHierarchy;
				string projectName = null;

				if (sccProject == null)
				{
					// This is the solution calling
					pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
					projectName = _sccProvider.GetSolutionFileName();
				}
				else
				{
					if (sccProject == null)
					{
						// It is a project that doesn't support source control, in which case it should be ignored
						continue;
					}

					projectName = _sccProvider.GetProjectFileName(sccProject);
				}

				// Files in this project are in rgszMkOldNames, rgszMkNewNames arrays starting with iProjectFilesStart index and ending at iNextProjecFilesStart-1
				int iProjectFilesStart = rgFirstIndices[iProject];
				int iNextProjecFilesStart = cFiles;
				if (iProject < cProjects - 1)
				{
					iNextProjecFilesStart = rgFirstIndices[iProject + 1];
				}

				// Now that we know which files belong to this project, iterate the project files
				for (int iFile = iProjectFilesStart; iFile < iNextProjecFilesStart; iFile++)
				{
					files.Add(rgpszMkDocuments[iFile]);
				}
			}

			storage.RemoveFiles(files);

			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
				//				return;
			}
			return VSConstants.S_OK;
		}

		public int OnQueryRemoveDirectories([InAttribute] IVsProject pProject, [InAttribute] int cDirectories, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSQUERYREMOVEDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYREMOVEDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYREMOVEDIRECTORYRESULTS[] rgResults)
		{
			return VSConstants.E_NOTIMPL;
		}

		public int OnAfterRemoveDirectories([InAttribute] int cProjects, [InAttribute] int cDirectories, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] VSREMOVEDIRECTORYFLAGS[] rgFlags)
		{
			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
				//				return;
			}
			return VSConstants.S_OK;
		}

		public int OnQueryRenameFiles([InAttribute] IVsProject pProject, [InAttribute] int cFiles, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSQUERYRENAMEFILEFLAGS[] rgFlags, [OutAttribute] VSQUERYRENAMEFILERESULTS[] pSummaryResult, [OutAttribute] VSQUERYRENAMEFILERESULTS[] rgResults)
		{
			return VSConstants.E_NOTIMPL;
		}

		/// <summary>
		/// Implement OnAfterRenameFiles event to rename a file in the source control store when it gets renamed in the project
		/// Also, rename the store if the project itself is renamed
		/// </summary>
		public int OnAfterRenameFiles([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSRENAMEFILEFLAGS[] rgFlags)
		{
			// Start by iterating through all projects calling this function
			for (int iProject = 0; iProject < cProjects; iProject++)
			{
				var sccProject = rgpProjects[iProject] as IVsSccProject2;
				var pHier = rgpProjects[iProject] as IVsHierarchy;
				string projectName = null;

				if (sccProject == null)
				{
					// This is the solution calling
					pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
					projectName = _sccProvider.GetSolutionFileName();
				}
				else
				{
					if (sccProject == null)
					{
						// It is a project that doesn't support source control, in which case it should be ignored
						continue;
					}

					projectName = _sccProvider.GetProjectFileName(sccProject);
				}

				// Files in this project are in rgszMkOldNames, rgszMkNewNames arrays starting with iProjectFilesStart index and ending at iNextProjecFilesStart-1
				int iProjectFilesStart = rgFirstIndices[iProject];
				int iNextProjecFilesStart = cFiles;
				if (iProject < cProjects - 1)
				{
					iNextProjecFilesStart = rgFirstIndices[iProject+1];
				}

				// Now that we know which files belong to this project, iterate the project files
				for (int iFile = iProjectFilesStart; iFile < iNextProjecFilesStart; iFile++)
				{
					Logger.WriteLine("OnAfterRenameFiles: [{0}]: {1} -> {2}", iFile, rgszMkOldNames[iFile], rgszMkNewNames[iFile]);
					// var storage = _controlledProjects[pHier];
					if (storage != null)
					{
						storage.RenameFileInStorage(rgszMkOldNames[iFile], rgszMkNewNames[iFile]);

						// And refresh the solution explorer glyphs because we affected the source control status of this file
						// Note that by now, the project should already know about the new file name being part of its hierarchy
						System.Collections.Generic.IList<VSITEMSELECTION> nodes = GetControlledProjectsContainingFile(rgszMkNewNames[iFile]);
						_sccProvider.RefreshNodesGlyphs(nodes);
					}
				}
			}

			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
//				return;
			}

			return VSConstants.S_OK;
		}

		public int OnQueryRenameDirectories([InAttribute] IVsProject pProject, [InAttribute] int cDirs, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSQUERYRENAMEDIRECTORYFLAGS[] rgFlags, [OutAttribute] VSQUERYRENAMEDIRECTORYRESULTS[] pSummaryResult, [OutAttribute] VSQUERYRENAMEDIRECTORYRESULTS[] rgResults)
		{
			return VSConstants.E_NOTIMPL;
		}

		public int OnAfterRenameDirectories([InAttribute] int cProjects, [InAttribute] int cDirs, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgszMkOldNames, [InAttribute] string[] rgszMkNewNames, [InAttribute] VSRENAMEDIRECTORYFLAGS[] rgFlags)
		{
			return VSConstants.E_NOTIMPL;
/*
			// Before checking in files, make sure all in-memory edits have been commited to disk 
			// by forcing a save of the solution. Ideally, only the files to be checked in should be saved...
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			if (sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0) != VSConstants.S_OK)
			{
				// If saving the files failed, don't continue with the checkin
				//				return;
			}
			return VSConstants.S_OK;
*/
		}

		public int OnAfterSccStatusChanged([InAttribute] int cProjects, [InAttribute] int cFiles, [InAttribute] IVsProject[] rgpProjects, [InAttribute] int[] rgFirstIndices, [InAttribute] string[] rgpszMkDocuments, [InAttribute] uint[] rgdwSccStatus)
		{
			return VSConstants.E_NOTIMPL;
		}

		#region Files and Project Management Functions

		/// <summary>
		/// Returns whether this source control provider is the active scc provider.
		/// </summary>
		public bool Active
		{
			get { return _active; }
		}

		/// <summary>
		/// Variable containing the solution location in source control if the solution being loaded is controlled
		/// </summary>
		public string LoadingControlledSolutionLocation
		{
			set { _loadingControlledSolutionLocation = value; }
		}

		/// <summary>
		/// Checks whether the specified project or solution (pHier==null) is under source control
		/// </summary>
		/// <returns>True if project is controlled.</returns>
		public bool IsProjectControlled(IVsHierarchy pHier)
		{
			if (pHier == null)
			{
				// this is solution, get the solution hierarchy
				pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
			}

			return _controlledProjects.Contains(pHier);
		}

		/// <summary>
		/// Checks whether the specified project or solution (pHier==null) is offline
		/// </summary>
		/// <returns>True if project is offline.</returns>
		public bool IsProjectOffline(IVsHierarchy pHier)
		{
			if (pHier == null)
			{
				// this is solution, get the solution hierarchy
				pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
			}

			return _offlineProjects.Contains(pHier);
		}

		/// <summary>
		/// Toggle the offline status of the specified project or solution
		/// </summary>
		/// <returns>True if project is offline.</returns>
		public void ToggleOfflineStatus(IVsHierarchy pHier)
		{
			if (pHier == null)
			{
				// this is solution, get the solution hierarchy
				pHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
			}

			if (_offlineProjects.Contains(pHier))
			{
				_offlineProjects.Remove(pHier);
			}
			else
			{
				_offlineProjects.Add(pHier);
			}
		}

		/// <summary>
		/// Adds the specified projects and solution to source control
		/// </summary>
		public void AddProjectsToSourceControl(Hashtable hashUncontrolledProjects, bool addSolutionToSourceControl)
		{
			// A real source control provider will ask the user for a location where the projects will be controlled
			// From the user input it should create up to 4 strings that will pass them to the projects to persist, 
			// so next time the project is open from disk, it will callback source control package, and the package
			// could use the 4 binding strings to identify the correct database location of the project files.
			foreach (IVsHierarchy pHier in hashUncontrolledProjects.Keys)
			{
				var sccProject2 = (IVsSccProject2)pHier;
				sccProject2.SetSccLocation("<Project Location In Database>", "<Source Control Database>", "<Local Binding Root of Project>", _sccProvider.ProviderName);

				// Add the newly controlled projects now to the list of controlled projects in this solution
				_controlledProjects.Add(pHier);
			}

			// Also, if the solution was selected to be added to scc, write in the solution properties the controlled status
			if (addSolutionToSourceControl)
			{
				var solHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
				_controlledProjects.Add(solHier);
				_sccProvider.SolutionHasDirtyProps = true;
			}

			// Now save all the modified files
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			// Add now the solution and project files to the source control database
			// which in our case means creating a text file containing the list of controlled files
			foreach (IVsHierarchy pHier in hashUncontrolledProjects.Keys)
			{
				var sccProject2 = (IVsSccProject2)pHier;
				var files = _sccProvider.GetProjectFiles(sccProject2);
				var project_path = _sccProvider.GetProjectFileName(sccProject2);
				string solutionFile = _sccProvider.GetSolutionFileName();
				var work_dir = Path.GetDirectoryName(solutionFile);

				if (!storage.IsValid)
				{
					var err = storage.Init(work_dir, SccOpenProjectFlags.CreateIfNew);
//					if (err != SccErrors.Ok)
//						return;
				}

				storage.AddFilesToStorage(files);
			}

			// If adding solution to source control, create a storage for the solution, too
			if (addSolutionToSourceControl)
			{
				var solHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
				var files = new List<string>();
				string solutionFile = _sccProvider.GetSolutionFileName();
				files.Add(solutionFile);
				var work_dir = Path.GetDirectoryName(solutionFile);

				if (!storage.IsValid)
				{
					var err = storage.Init(work_dir, SccOpenProjectFlags.CreateIfNew);
//					if (err != SccErrors.Ok)
//						return;
				}

				storage.AddFilesToStorage(files);
			}

			// For all the projects added to source control, refresh their source control glyphs
			var nodes = new List<VSITEMSELECTION>();
			foreach (IVsHierarchy pHier in hashUncontrolledProjects.Keys)
			{
				VSITEMSELECTION vsItem;
				vsItem.itemid = VSConstants.VSITEMID_ROOT;
				vsItem.pHier = pHier;
				nodes.Add(vsItem);
			}

			// Also, add the solution if necessary to the list of glyphs to refresh
			if (addSolutionToSourceControl)
			{
				VSITEMSELECTION vsItem;
				vsItem.itemid = VSConstants.VSITEMID_ROOT;
				vsItem.pHier = null;
				nodes.Add(vsItem);
			}

			_sccProvider.RefreshNodesGlyphs(nodes);
		}

		// The following methods are not very efficient
		// A good source control provider should maintain maps to identify faster to which project does a file belong
		// and check only the status of the files in that project; or simply, query one common storage about the file status

		/// <summary>
		/// Returns the source control status of the specified file
		/// </summary>
		public SourceControlStatus GetFileStatus(string filename)
		{
			return storage.GetFileStatus(filename);
		}

		public void GetStatusForFiles(string[] files, SourceControlStatus[] statuses)
		{
			storage.GetStatusForFiles(files, statuses);
		}

		public void ViewHistory(string file)
		{
			if (storage != null)
			{
				SourceControlStatus status = storage.GetFileStatus(file);
				if (status != SourceControlStatus.scsUncontrolled)
				{
					storage.ViewHistory(file);
					return;
				}
			}
		}

		public void Compare(string file)
		{
			if (storage != null)
			{
				SourceControlStatus status = storage.GetFileStatus(file);
				if (status != SourceControlStatus.scsUncontrolled)
				{
					storage.Compare(file);
					return;
				}
			}
		}

		public void ViewChangeLog()
		{
			if (storage == null)
				return;

			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			storage.ViewChangeLog();
		}


		/// <summary>
		/// Returns a list of controlled projects containing the specified file
		/// </summary>
		public System.Collections.Generic.IList<VSITEMSELECTION> GetControlledProjectsContainingFile(string file)
		{
			// Accumulate all the controlled projects that contain this file
			System.Collections.Generic.IList<VSITEMSELECTION> nodes = new List<VSITEMSELECTION>();

			foreach (IVsHierarchy pHier in _controlledProjects)
			{
				var solHier = (IVsHierarchy)_sccProvider.GetService(typeof(SVsSolution));
				if (solHier == pHier)
				{
					// This is the solution
					if (file.ToLower().CompareTo(_sccProvider.GetSolutionFileName().ToLower()) == 0)
					{
						VSITEMSELECTION vsItem;
						vsItem.itemid = VSConstants.VSITEMID_ROOT;
						vsItem.pHier = null;
						nodes.Add(vsItem);
					}
				}
				else
				{
					var pProject = pHier as IVsProject2;
					// See if the file is member of this project
					// Caveat: the IsDocumentInProject function is expensive for certain project types, 
					// you may want to limit its usage by creating your own maps of file2project or folder2project
					int fFound;
					uint itemid;
					VSDOCUMENTPRIORITY[] prio = new VSDOCUMENTPRIORITY[1];
					if (pProject != null && pProject.IsDocumentInProject(file, out fFound, prio, out itemid) == VSConstants.S_OK && fFound != 0)
					{
						VSITEMSELECTION vsItem;
						vsItem.itemid = itemid;
						vsItem.pHier = pHier;
						nodes.Add(vsItem);
					}
				}
			}

			return nodes;
		}

		public System.Collections.Generic.IList<VSITEMSELECTION> GetControlledProjectsContainingFiles(IEnumerable<string> files)
		{
			var nodes = new List<VSITEMSELECTION>();
			foreach (var f in files)
			{
				var found = GetControlledProjectsContainingFile(f);
				nodes.AddRange(found);
			}

			return nodes;
		}

		public void CommitFiles(IEnumerable<string> files)
		{
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			IEnumerable<string> commited_files;
			storage.Commit(files, out commited_files);

			var list = GetControlledProjectsContainingFiles(commited_files);
			if (list.Count != 0)
			{
				// now refresh the selected nodes' glyphs
				_sccProvider.RefreshNodesGlyphs(list);
			}
		}

		public void RevertFiles(IEnumerable<string> files)
		{
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			var fileChange = (IVsFileChangeEx)_sccProvider.GetService(typeof(SVsFileChangeEx));
//			var rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(SVsRunningDocumentTable));

			var doc_list = new List<IVsPersistDocData>();

			foreach (var f in files)
			{
				IVsHierarchy h;
				uint cookie;
				uint itemid;
				IntPtr doc_data;
				var err = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, f, out h, out itemid, out doc_data, out cookie);

				if (err != VSConstants.S_OK || doc_data == IntPtr.Zero)
					continue;

				var doc = Marshal.GetObjectForIUnknown(doc_data) as IVsPersistDocData;
				if (doc != null)
				{
					doc_list.Add(doc);
					fileChange.IgnoreFile(0, f, 1);
				}
			}

			IEnumerable<string> reverted_files = null;
			try
			{
				storage.Revert(files, out reverted_files);

			}
			finally
			{
				foreach (var doc in doc_list)
				{
					doc.ReloadDocData((uint) _VSRELOADDOCDATA.RDD_IgnoreNextFileChange);
					Marshal.ReleaseComObject(doc);
				}

				foreach (var f in files)
				{
					fileChange.SyncFile(f);
					fileChange.IgnoreFile(0, f, 0);
				}

				if (reverted_files != null)
				{
					var list = GetControlledProjectsContainingFiles(reverted_files);
					if (list.Count != 0)
					{
						// now refresh the selected nodes' glyphs
						_sccProvider.RefreshNodesGlyphs(list);
					}
				}
			}
		}
		#endregion

		#region Implementation of IVsTrackProjectDocumentsEvents3

		/// <summary>
		/// Indicates that a project is about start a batch query process.
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		public int OnBeginQueryBatch()
		{
			Logger.WriteLine("OnBeginQueryBatch");
			return VSConstants.S_OK;
		}

		/// <summary>
		/// Determines whether it is okay to proceed with the actual batch operation after successful completion of a batch query process. 
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		/// <param name="pfActionOK">[out] Returns nonzero if it is okay to continue with the proposed batch process. Returns zero if the proposed batch process should not proceed.</param>
		public int OnEndQueryBatch(out int pfActionOK)
		{
			Logger.WriteLine("OnEndQueryBatch");
			pfActionOK = 1;
			return VSConstants.S_OK;
		}

		/// <summary>
		/// This method is called to indicate that a batch query process has been canceled.
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		public int OnCancelQueryBatch()
		{
			Logger.WriteLine("OnCancelQueryBatch");
			return VSConstants.S_OK;
		}

		/// <summary>
		/// Determines if it is okay to add a collection of files (possibly from source control) whose final destination may be different from a source location.
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		/// <param name="pProject">[in] Project making the request about adding files.</param>
		/// <param name="cFiles">[in] The number of files represented in the <paramref name="rgpszNewMkDocuments" />, <paramref name="rgpszSrcMkDocuments" />, <paramref name="rgFlags" />, and <paramref name="rgResults" /> arrays.</param>
		/// <param name="rgpszNewMkDocuments">[in] An array of file names that indicate the files' final destination.</param>
		/// <param name="rgpszSrcMkDocuments">[in] An array of file names specifying the source location of the files.</param>
		/// <param name="rgFlags">[in] An array of values, one element for each file, from the <see cref="T:Microsoft.VisualStudio.Shell.Interop.VSQUERYADDFILEFLAGS" /> enumeration.</param>
		/// <param name="pSummaryResult">[out] Returns an overall status for all files as a value from the <see cref="T:Microsoft.VisualStudio.Shell.Interop.VSQUERYADDFILERESULTS" /> enumeration.</param>
		/// <param name="rgResults">[out] An array that is to be filled in with the status of each file. Each status is a value from the <see cref="T:Microsoft.VisualStudio.Shell.Interop.VSQUERYADDFILERESULTS" /> enumeration.</param>
		public int OnQueryAddFilesEx(IVsProject pProject, int cFiles, string[] rgpszNewMkDocuments, string[] rgpszSrcMkDocuments, VSQUERYADDFILEFLAGS[] rgFlags, VSQUERYADDFILERESULTS[] pSummaryResult, VSQUERYADDFILERESULTS[] rgResults)
		{
			Logger.WriteLine("OnQueryAddFilesEx");
			return VSConstants.E_NOTIMPL;
		}

		/// <summary>
		/// Accesses a specified set of files and asks all implementers of this method to release any locks that may exist on those files.
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		/// <param name="grfRequiredAccess">[in] A value from the <see cref="T:Microsoft.VisualStudio.Shell.Interop.__HANDSOFFMODE" /> enumeration, indicating the type of access requested. This can be used to optimize the locks that actually need to be released.</param>
		/// <param name="cFiles">[in] The number of files in the <paramref name="rgpszMkDocuments" /> array.</param>
		/// <param name="rgpszMkDocuments">[in] If there are any locks on this array of file names, the caller wants them to be released.</param>
		public int HandsOffFiles(uint grfRequiredAccess, int cFiles, string[] rgpszMkDocuments)
		{
			Logger.WriteLine("HandsOffFiles");
			return VSConstants.E_NOTIMPL;
		}

		/// <summary>
		/// Called when a project has completed operations on a set of files.
		/// </summary>
		/// <returns>
		/// If the method succeeds, it returns <see cref="F:Microsoft.VisualStudio.VSConstants.S_OK" />. If it fails, it returns an error code.
		/// </returns>
		/// <param name="cFiles">[in] Number of file names given in the <paramref name="rgpszMkDocuments" /> array.</param>
		/// <param name="rgpszMkDocuments">[in] An array of file names.</param>
		public int HandsOnFiles(int cFiles, string[] rgpszMkDocuments)
		{
			Logger.WriteLine("HandsOnFiles");
			return VSConstants.E_NOTIMPL;
		}

		#endregion

		#region Implementation of IVsRunningDocTableEvents

		public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
		{
			return VSConstants.S_OK;
		}

		public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
		{
			return VSConstants.S_OK;
		}

		public int OnAfterSave(uint docCookie)
		{
			// Retrieve document info

			uint pgrfRDTFlags;
			uint pdwReadLocks;
			uint pdwEditLocks;
			string pbstrMkDocument;
			IVsHierarchy ppHier;
			uint pitemid;
			IntPtr ppunkDocData;

//			var rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(SVsRunningDocumentTable)); 
			var err = rdt.GetDocumentInfo(
				docCookie, out pgrfRDTFlags, out pdwReadLocks, out pdwEditLocks,
				out pbstrMkDocument, out ppHier, out pitemid, out ppunkDocData);

			Logger.WriteLine("OnAfterSave: {0}", pbstrMkDocument);

/*
			// Get automation-object Document and work with it

			ProjectItem prjItem = DTE.Solution.FindProjectItem(pbstrMkDocument);
			if (prjItem != null)
				OnDocumentSaved(prjItem.Document);
*/
			if (storage.IsValid)
			{
				var status = storage.GetFileStatus(pbstrMkDocument);
				if (status != SourceControlStatus.scsUncontrolled)
				{
					storage.UpdateFileCache(pbstrMkDocument);

					System.Collections.Generic.IList<VSITEMSELECTION> lst = GetControlledProjectsContainingFiles(new []{pbstrMkDocument});
					if (lst.Count != 0)
					{
						_sccProvider.RefreshNodesGlyphs(lst);
					}
				}
			}

			return VSConstants.S_OK;
		}

		public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
		{
			// Retrieve document info

			uint pgrfRDTFlags;
			uint pdwReadLocks;
			uint pdwEditLocks;
			string pbstrMkDocument;
			IVsHierarchy ppHier;
			uint pitemid;
			IntPtr ppunkDocData;

//			var rdt = (IVsRunningDocumentTable)_sccProvider.GetService(typeof(SVsRunningDocumentTable));
			var err = rdt.GetDocumentInfo(
				docCookie, out pgrfRDTFlags, out pdwReadLocks, out pdwEditLocks,
				out pbstrMkDocument, out ppHier, out pitemid, out ppunkDocData);

			Logger.WriteLine("OnAfterAttributeChange: {0}, {1:X}", pbstrMkDocument, grfAttribs);
			if ((grfAttribs & (uint)__VSRDTATTRIB.RDTA_DocDataReloaded) != (uint)__VSRDTATTRIB.RDTA_DocDataReloaded)
				return VSConstants.S_OK;

			/*
						// Get automation-object Document and work with it

						ProjectItem prjItem = DTE.Solution.FindProjectItem(pbstrMkDocument);
						if (prjItem != null)
							OnDocumentSaved(prjItem.Document);
			*/
			if (storage.IsValid)
			{
				var status = storage.GetFileStatus(pbstrMkDocument);
				if (status != SourceControlStatus.scsUncontrolled)
				{
					storage.UpdateFileCache(pbstrMkDocument);

					System.Collections.Generic.IList<VSITEMSELECTION> lst = GetControlledProjectsContainingFiles(new[] { pbstrMkDocument });
					if (lst.Count != 0)
					{
						_sccProvider.RefreshNodesGlyphs(lst);
					}
				}
			}

			return VSConstants.S_OK;
		}

		public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
		{
			return VSConstants.S_OK;
		}

		public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
		{
			return VSConstants.S_OK;
		}

		#endregion

		public void RefreshGlyphsForControlledProjects()
		{
			// For all the projects added to source control, refresh their source control glyphs
			var nodes = new List<VSITEMSELECTION>();
			foreach (IVsHierarchy pHier in _controlledProjects)
			{
				VSITEMSELECTION vsItem;
				vsItem.itemid = VSConstants.VSITEMID_ROOT;
				vsItem.pHier = pHier;
				nodes.Add(vsItem);
			}

			_sccProvider.RefreshNodesGlyphs(nodes);
		}

		//------------------------------------------------------------------
		public void Synchronize()
		{
			if (storage == null)
				return;

			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			storage.Synchronize();
		}

		//------------------------------------------------------------------
		public void Update()
		{
			if (storage == null)
				return;

			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_SaveIfDirty, null, 0);

			storage.Update();
		}

		//------------------------------------------------------------------
		public void Tags()
		{
			if (storage == null)
				return;

			storage.Tags();
		}

		//------------------------------------------------------------------
		private void UpdateEvent_Handler(object sender, EventArgs e)
		{
			storage.ReloadCache();

			// FIXME: Need a better way to handle updates, than reloading solution
			var sol = (IVsSolution)_sccProvider.GetService(typeof(SVsSolution));
			sol.OpenSolutionFile((uint)__VSSLNOPENOPTIONS.SLNOPENOPT_Silent,
				 _sccProvider.GetSolutionFileName());
		}
	}
}