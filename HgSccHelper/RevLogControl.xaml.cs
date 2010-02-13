﻿//=========================================================================
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

using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Threading;
using System;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;

namespace HgSccHelper
{
	/// <summary>
	/// Interaction logic for RevLogControl.xaml
	/// </summary>
	public partial class RevLogControl : UserControl
	{
		List<RevLogChangeDesc> revs;
		ObservableCollection<RevLogLinesPair> rev_lines;

		RevLogChangeDescParser rev_log_parser;
		RevLogIteratorParser rev_log_iterator;
		RevLogLinesPairParser rev_log_lines_parser;

		DispatcherTimer timer;

		const int BatchSize = 500;

		//-----------------------------------------------------------------------------
		public static RoutedUICommand DiffPreviousCommand = new RoutedUICommand("Diff Previous",
			"DiffPrevious", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand FileHistoryCommand = new RoutedUICommand("File History",
			"FileHistory", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand UpdateCommand = new RoutedUICommand("Update to Revision",
			"Update", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand TagsCommand = new RoutedUICommand("Add/Remove Tag",
			"Tags", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand MergeCommand = new RoutedUICommand("Merge",
			"Merge", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand ReadNextCommand = new RoutedUICommand("Read Next",
			"ReadNext", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand ReadAllCommand = new RoutedUICommand("Read All",
			"ReadAll", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public static RoutedUICommand StopCommand = new RoutedUICommand("Stop",
			"Stop", typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		public string WorkingDir { get; set; }

		//------------------------------------------------------------------
		public UpdateContext UpdateContext { get; private set; }

		//------------------------------------------------------------------
		HgThread worker;

		//------------------------------------------------------------------
		Cursor prev_cursor;

		//------------------------------------------------------------------
		RevLogStyleFile revlog_style;

		//------------------------------------------------------------------
		IdentifyInfo CurrentRevision { get; set; }

		//-----------------------------------------------------------------------------
		public static readonly DependencyProperty SelectedChangesetProperty =
			DependencyProperty.Register("SelectedChangeset", typeof(RevLogLinesPair),
			typeof(RevLogControl));

		//-----------------------------------------------------------------------------
		private RevLogLinesPair SelectedChangeset
		{
			get { return (RevLogLinesPair)this.GetValue(SelectedChangesetProperty); }
			set { this.SetValue(SelectedChangesetProperty, value); }
		}

		//------------------------------------------------------------------
		SelectedParentFile SelectedParentFile { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// SHA1 -> BranchInfo map
		/// </summary>
		C5.HashDictionary<string, BranchInfo> Branches { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// Tag Name -> TagInfo map
		/// </summary>
		C5.HashDictionary<string, TagInfo> Tags { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// SHA1 -> RevLogLinesPair map
		/// </summary>
		C5.HashDictionary<string, RevLogLinesPair> rev_log_hash_map;

		//------------------------------------------------------------------
		public RevLogControl()
		{
			InitializeComponent();

			VirtualizingStackPanel.SetIsVirtualizing(graphView, true);
			VirtualizingStackPanel.SetVirtualizationMode(graphView, VirtualizationMode.Recycling);

			worker = new HgThread();
			revlog_style = new RevLogStyleFile();

			rev_log_iterator = new RevLogIteratorParser();
			rev_log_lines_parser = new RevLogLinesPairParser();

			revs = new List<RevLogChangeDesc>();
			rev_lines = new ObservableCollection<RevLogLinesPair>();
			graphView.ItemsSource = rev_lines;

			UpdateContext = new UpdateContext();

			rev_log_hash_map = new C5.HashDictionary<string, RevLogLinesPair>();
		}

		//------------------------------------------------------------------
		private void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
		{
			var hg = new Hg();
			CurrentRevision = hg.Identify(WorkingDir);
			if (CurrentRevision == null)
				return;

			Branches = new C5.HashDictionary<string, BranchInfo>();
			foreach (var branch in hg.Branches(WorkingDir))
			{
				Branches[branch.SHA1] = branch;
			}

			Tags = new C5.HashDictionary<string, TagInfo>();
			foreach (var tag in hg.Tags(WorkingDir))
			{
				Tags[tag.Name] = tag;
			}

			timer = new DispatcherTimer();
			timer.Interval = TimeSpan.FromMilliseconds(50);
			timer.Tick += OnTimerTick;

			if (WorkingDir != null)
			{
				RunRevLogThread(WorkingDir, "", BatchSize);
			}

			graphView.Focus();
		}

		//------------------------------------------------------------------
		private void OnTimerTick(object o, EventArgs e)
		{
			timer.Stop();

			if (graphView.SelectedItems.Count == 1)
			{
				var rev_pair = (RevLogLinesPair)graphView.SelectedItem;
				SelectedChangeset = rev_pair;

				var parents_diff = new List<ParentFilesDiff>();

				var hg = new Hg();
				foreach (var parent in SelectedChangeset.Current.ChangeDesc.Parents)
				{
					var options = HgStatusOptions.Added | HgStatusOptions.Deleted
						| HgStatusOptions.Modified
						| HgStatusOptions.Copies | HgStatusOptions.Removed;

					var files = hg.Status(WorkingDir, options, "", parent,
						SelectedChangeset.Current.ChangeDesc.SHA1);

					var desc = hg.GetRevisionDesc(WorkingDir, parent);
					if (desc != null)
					{
						var parent_diff = new ParentFilesDiff();
						parent_diff.Desc = desc;
						parent_diff.Files = files;

						parents_diff.Add(parent_diff);
					}
				}

				tabParentsDiff.ItemsSource = parents_diff;
				if (tabParentsDiff.Items.Count > 0)
				{
					tabParentsDiff.SelectedIndex = 0;
					var list_view = tabParentsDiff.ItemContainerGenerator.ContainerFromIndex(0) as ListView;
					if (list_view != null)
					{
						if (list_view.Items.Count > 0)
							list_view.SelectedIndex = 0;
					}
				}

				return;
			}
		}

		//------------------------------------------------------------------
		private void graphView_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			SelectedChangeset = null;
			tabParentsDiff.ItemsSource = null;
			timer.Stop();

			if (graphView.SelectedItems.Count == 1)
			{
				timer.Start();
			}
		}

		//------------------------------------------------------------------
		private void UserControl_Unloaded(object sender, System.Windows.RoutedEventArgs e)
		{
			timer.Tick -= OnTimerTick;

			if (StopCommand.CanExecute(sender, e.Source as IInputElement))
				StopCommand.Execute(sender, e.Source as IInputElement);

			worker.Dispose();
			revlog_style.Dispose();
		}

		//------------------------------------------------------------------
		// Enumerate all the descendants of the visual object.
		static IEnumerable<Visual> EnumVisual(Visual myVisual)
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(myVisual); i++)
			{
				// Retrieve child visual at specified index value.
				Visual childVisual = (Visual)VisualTreeHelper.GetChild(myVisual, i);
				yield return childVisual;

				foreach (var v in EnumVisual(childVisual))
					yield return v;
			}
		}

		//------------------------------------------------------------------
		private childItem FindVisualChild<childItem>(DependencyObject obj)
			where childItem : DependencyObject
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(obj, i);
				if (child != null && child is childItem)
					return (childItem)child;
				else
				{
					childItem childOfChild = FindVisualChild<childItem>(child);
					if (childOfChild != null)
						return childOfChild;
				}
			}
			return null;
		}

		//------------------------------------------------------------------
		private void DiffPrevious_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;
			if (SelectedParentFile != null)
			{
				if (SelectedParentFile.FileInfo.Status == HgFileStatus.Modified)
					e.CanExecute = true;
			}
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void DiffPrevious_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
		{
			e.Handled = true;

			var parent_diff = (ParentFilesDiff)tabParentsDiff.SelectedItem;
			var file_info = SelectedParentFile.FileInfo;

			try
			{
				var hg = new Hg();
				hg.Diff(WorkingDir, file_info.File, parent_diff.Desc.SHA1, file_info.File,
				    SelectedChangeset.Current.ChangeDesc.SHA1);
			}
			catch (HgDiffException)
			{
				Util.HandleHgDiffException();
			}
		}

		//------------------------------------------------------------------
		private void ListViewFiles_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			if (DiffPreviousCommand != null)
			{
				if (DiffPreviousCommand.CanExecute(sender, e.Source as IInputElement))
					DiffPreviousCommand.Execute(sender, e.Source as IInputElement);
			}
		}

		//------------------------------------------------------------------
		private void ReadNext_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;
			if (WorkingDir != null && revs != null && revs.Count > 0)
			{
				if (worker != null && !worker.IsBusy)
					if (revs[revs.Count - 1].Rev != 0)
					{
						e.CanExecute = true;
					}
			}
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void ReadNext_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var start_rev = revs[revs.Count - 1].Rev - 1;
			var stop_rev = Math.Max(0, start_rev - BatchSize);
			var rev = string.Format("{0}:{1}", start_rev, stop_rev);

			RunRevLogThread(WorkingDir, rev, 0);
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void RunRevLogThread(string work_dir, string rev, int max_count)
		{
			var p = new HgThreadParams();
			p.CompleteHandler = Worker_Completed;
			p.OutputHandler = Output_Handler;
			p.WorkingDir = WorkingDir;

			var args = new StringBuilder();
			args.Append("log");
			args.Append(" --debug");
			args.Append(" -v");
	
			if (max_count != 0)
				args.Append(" -l " + max_count);

			if (rev.Length > 0)
				args.Append(" -r " + rev);

			args.Append(" --style " + revlog_style.FileName.Quote());

			p.Args = args.ToString();

			rev_log_parser = new RevLogChangeDescParser();
			
			prev_cursor = Cursor;
			Cursor = Cursors.Wait;
			
			worker.Run(p);
		}

		//------------------------------------------------------------------
		private void ReadAll_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			ReadNext_CanExecute(sender, e);
		}

		//------------------------------------------------------------------
		private void ReadAll_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var start_rev = revs[revs.Count - 1].Rev - 1;
			var rev = string.Format("{0}:{1}", start_rev, 0);

			RunRevLogThread(WorkingDir, rev, 0);
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void FileHistory_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (SelectedParentFile != null)
					e.CanExecute = true;

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void FileHistory_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var parent_diff = (ParentFilesDiff)tabParentsDiff.SelectedItem;
			var file_info = SelectedParentFile.FileInfo;

			FileHistoryWindow wnd = new FileHistoryWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.Rev = SelectedChangeset.Current.ChangeDesc.SHA1;
			wnd.FileName = file_info.File;
			wnd.Owner = Window.GetWindow(this);

			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			if (wnd.UpdateContext.IsTagsChanged)
				HandleTagsChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void Stop_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = (worker != null && worker.IsBusy && !worker.CancellationPending);
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Stop_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			worker.Cancel();
			e.Handled = true;
		}

		//------------------------------------------------------------------
		void Worker_Completed(HgThreadResult completed)
		{
			Cursor = prev_cursor;

			// Updating commands state (CanExecute)
			CommandManager.InvalidateRequerySuggested();
		}

		//------------------------------------------------------------------
		void Output_Handler(string msg)
		{
			var change_desc = rev_log_parser.ParseLine(msg);
			if (change_desc != null)
			{
				Dispatcher.Invoke(DispatcherPriority.Normal,
					new Action<RevLogChangeDesc>(Worker_NewRevLogChangeDesc), change_desc);
			}
		}

		//------------------------------------------------------------------
		void Worker_NewRevLogChangeDesc(RevLogChangeDesc change_desc)
		{
			revs.Add(change_desc);
			var new_lines_pair = rev_log_lines_parser.ParseLogLines(
				rev_log_iterator.ParseChangeDesc(change_desc));

			var sha1 = new_lines_pair.Current.ChangeDesc.SHA1;

			foreach (var parent in CurrentRevision.Parents)
			{
				if (parent.SHA1 == sha1)
				{
					new_lines_pair.IsCurrent = true;
					break;
				}
			}

			BranchInfo branch_info;
			if (Branches.Find(sha1, out branch_info))
				new_lines_pair.BranchInfo = branch_info;

			rev_lines.Add(new_lines_pair);
			rev_log_hash_map[new_lines_pair.Current.ChangeDesc.SHA1] = new_lines_pair;

			if (graphView.SelectedIndex == -1 && graphView.Items.Count > 0)
				graphView.SelectedIndex = 0;
		}

		//------------------------------------------------------------------
		private void Update_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (SelectedChangeset != null)
				e.CanExecute = true;

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Update_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			UpdateWindow wnd = new UpdateWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.TargetRevision = SelectedChangeset.Current.ChangeDesc.SHA1;

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void HandleParentChange()
		{
			var hg = new Hg();
			var new_current = hg.Identify(WorkingDir);

			foreach (var parent in CurrentRevision.Parents)
			{
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(parent.SHA1, out lines_pair))
					lines_pair.IsCurrent = false;
			}

			CurrentRevision = new_current;
			foreach (var parent in CurrentRevision.Parents)
			{
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(parent.SHA1, out lines_pair))
					lines_pair.IsCurrent = true;
			}
		}

		//------------------------------------------------------------------
		private void HandleBranchChanges()
		{
			var hg = new Hg();
			var new_branches = new C5.HashDictionary<string, BranchInfo>();
			var branch_list = hg.Branches(WorkingDir);

			foreach (var branch_info in branch_list)
			{
				new_branches[branch_info.SHA1] = branch_info;
				Branches.Remove(branch_info.SHA1);
			}

			foreach (var branch_info in Branches.Values)
			{
				// removing old branch info
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(branch_info.SHA1, out lines_pair))
					lines_pair.BranchInfo = null;
			}

			Branches = new_branches;

			foreach (var branch_info in Branches.Values)
			{
				// adding or updating branch info
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(branch_info.SHA1, out lines_pair))
					lines_pair.BranchInfo = branch_info;
			}
		}

		//------------------------------------------------------------------
		private void HandleTagsChanges()
		{
			var hg = new Hg();
			var new_tags = new C5.HashDictionary<string, TagInfo>();
			var tags_list = hg.Tags(WorkingDir);

			foreach (var tag in tags_list)
			{
				new_tags[tag.Name] = tag;
			}

			foreach (var tag in Tags.Values)
			{
				// removing old tags
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(tag.SHA1, out lines_pair))
				{
					var change_desc = lines_pair.Current.ChangeDesc;
					change_desc.Tags.Remove(tag.Name);
				}
			}

			Tags = new_tags;

			foreach (var tag in Tags.Values)
			{
				// adding or updating tags
				RevLogLinesPair lines_pair;
				if (rev_log_hash_map.Find(tag.SHA1, out lines_pair))
				{
					var change_desc = lines_pair.Current.ChangeDesc;
					if (!change_desc.Tags.Contains(tag.Name))
						change_desc.Tags.Add(tag.Name);
				}
			}
		}

		//------------------------------------------------------------------
		private void Tags_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (SelectedChangeset != null)
				e.CanExecute = true;

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Tags_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			TagsWindow wnd = new TagsWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.TargetRevision = SelectedChangeset.Current.ChangeDesc.SHA1;

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			if (wnd.UpdateContext.IsTagsChanged)
				HandleTagsChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void Merge_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (SelectedChangeset != null)
			{
				if (CurrentRevision.Parents.Count == 1)
				{
					if (SelectedChangeset.Current.ChangeDesc.SHA1 != CurrentRevision.SHA1)
						e.CanExecute = true;
				}
			}

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Merge_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var wnd = new MergeWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.TargetRevision = SelectedChangeset.Current.ChangeDesc.SHA1;

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void listFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			SelectedParentFile = null;

			var tab_item = tabParentsDiff.ItemContainerGenerator.ContainerFromIndex(
				tabParentsDiff.SelectedIndex) as TabItem;
			var list_view = e.OriginalSource as ListView;

			if (tab_item != null && list_view != null)
			{
				if (list_view.SelectedItems.Count == 1)
				{
					SelectedParentFile = new SelectedParentFile
					{
						FileInfo = list_view.SelectedItem as HgFileInfo,
						ParentFilesDiff = tab_item.Content as ParentFilesDiff
					};
				}
			}
		}
	}

	//==================================================================
	class ParentFilesDiff
	{
		public RevLogChangeDesc Desc { get; set; }
		public List<HgFileInfo> Files { get; set; }
		public string HeaderString
		{
			get
			{
				return String.Format("Diff with Parent {0} ({1})",
					Desc.Rev, Desc.SHA1.ShortSHA1());
			}
		}
	}

	//==================================================================
	class SelectedParentFile
	{
		public ParentFilesDiff ParentFilesDiff { get; set; }
		public HgFileInfo FileInfo { get; set; }
	}
}
