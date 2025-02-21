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

using System;
using System.Text;

//=============================================================================
namespace HgSccHelper
{
	//=============================================================================
	public class HgArgsBuilder
	{
		private readonly StringBuilder args;

		//-----------------------------------------------------------------------------
		public HgArgsBuilder()
		{
			args = new StringBuilder(Hg.MaxCmdLength);
		}

		//-----------------------------------------------------------------------------
		public int Length
		{
			get { return args.Length; }
		}

		//-----------------------------------------------------------------------------
		public void Clear()
		{
			if (args.Length > 0)
				args.Remove(0, args.Length);
		}

		//-----------------------------------------------------------------------------
		public void Append(string arg)
		{
			// FIXME: trim arg ?
			if (args.Length == 0)
				args.Append(arg);
			else
				args.Append(" " + arg);
		}

		//-----------------------------------------------------------------------------
		public void AppendVerbose()
		{
			Append("--verbose");
		}

		//-----------------------------------------------------------------------------
		public void AppendDebug()
		{
			Append("--debug");
		}

		//-----------------------------------------------------------------------------
		public void AppendInteractive(bool is_interactive)
		{
			Append(String.Format("--config ui.interactive={0}", is_interactive ? "True" : "False"));
		}

		//-----------------------------------------------------------------------------
		public void AppendStyle(string style_filename)
		{
			Append("--style");
			Append(style_filename.Quote());
		}

		//-----------------------------------------------------------------------------
		public void AppendRevision(string revision)
		{
			Append("--rev");
			Append(revision.Quote());
		}

		//-----------------------------------------------------------------------------
		public void AppendRevision(int revision)
		{
			AppendRevision(revision.ToString());
		}

		//-----------------------------------------------------------------------------
		public void AppendPath(string path)
		{
			Append(path.Quote());
		}

		//-----------------------------------------------------------------------------
		public void AppendDisableExtension(HgExtension extension)
		{
			AppendDisableExtension(HgExtensionNames.GetExtensionName(extension));
		}

		//-----------------------------------------------------------------------------
		public void AppendDisableExtension(string extension)
		{
			Append("--config");
			Append(String.Format("extensions.{0}=!", extension));
		}

		//-----------------------------------------------------------------------------
		public bool AppendListFile(string list_file)
		{
			var str = String.Format("listfile:{0}", list_file.Quote());
			if ((Length + str.Length) > Hg.MaxCmdLength)
				return false;

			Append(str);
			return true;
		}

		//-----------------------------------------------------------------------------
		public override string ToString()
		{
			return args.ToString();
		}
	}

	//=============================================================================
	public class HgArgsBuilderZero
	{
		private readonly StringBuilder args;

		//-----------------------------------------------------------------------------
		public HgArgsBuilderZero()
		{
			args = new StringBuilder(Hg.MaxCmdLength);
		}

		//-----------------------------------------------------------------------------
		public int Length
		{
			get { return args.Length; }
		}

		//-----------------------------------------------------------------------------
		public void Clear()
		{
			if (args.Length > 0)
				args.Remove(0, args.Length);
		}

		//-----------------------------------------------------------------------------
		public void Append(string arg)
		{
			// FIXME: trim arg ?
			if (args.Length == 0)
				args.Append(arg);
			else
				args.Append("\0" + arg);
		}

		//-----------------------------------------------------------------------------
		public void AppendVerbose()
		{
			Append("--verbose");
		}

		//-----------------------------------------------------------------------------
		public void AppendDebug()
		{
			Append("--debug");
		}

		//-----------------------------------------------------------------------------
		public void AppendStyle(string style_filename)
		{
			Append("--style");
			Append(style_filename);
		}

		//-----------------------------------------------------------------------------
		public void AppendRevision(string revision)
		{
			Append("--rev");
			Append(revision);
		}

		//-----------------------------------------------------------------------------
		public void AppendRevision(int revision)
		{
			AppendRevision(revision.ToString());
		}

		//-----------------------------------------------------------------------------
		public void AppendPath(string path)
		{
			Append(path);
		}

		//-----------------------------------------------------------------------------
		public void AppendDisableExtension(HgExtension extension)
		{
			AppendDisableExtension(HgExtensionNames.GetExtensionName(extension));
		}

		//-----------------------------------------------------------------------------
		public void AppendDisableExtension(string extension)
		{
			Append("--config");
			Append(String.Format("extensions.{0}=!", extension));
		}

		//-----------------------------------------------------------------------------
		public bool AppendListFile(string list_file)
		{
			var str = String.Format("listfile:{0}", list_file);
			if ((Length + str.Length) > Hg.MaxCmdLength)
				return false;

			Append(str);
			return true;
		}

		//-----------------------------------------------------------------------------
		public override string ToString()
		{
			return args.ToString();
		}
	}
}
