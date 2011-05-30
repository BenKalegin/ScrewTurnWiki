using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using MySql.Data.MySqlClient;

class WikiPage
{
	static readonly Regex CategoryLinkRegex = new Regex (@"(\[\[Category:.+?\]\])|(\[Category:.+?\])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	static readonly Regex LinkRegex = new Regex (@"(\[\[.+?\]\])|(\[.+?\])", RegexOptions.Compiled);
	static readonly Regex UrlRegex = new Regex (@"\w.+://", RegexOptions.Compiled);
	static readonly Regex DashesRegex = new Regex (@"[-]{2}", RegexOptions.Compiled);
	static readonly Regex TableRegex = new Regex (@"\{\|(\ [^\n]*)?\n.+?\|\}", RegexOptions.Compiled | RegexOptions.Singleline);
	static readonly Regex HRegex = new Regex (@"^={1,4}.+?={1,4}\n?", RegexOptions.Compiled | RegexOptions.Multiline);
	static readonly Regex MagicWordRegex = new Regex (@"\{\{.+?\}\}", RegexOptions.Compiled | RegexOptions.Singleline);

	static readonly SortedList <string, string> codeHighlightBlocks = new SortedList <string, string> () {
		{"<csharp>", "\n@@ csharp\n"},
		{"</csharp>", "\n@@\n"},
		{"<bash>", "\n@@ bash\n"},
		{"</bash>", "\n@@\n"},
		{"<xml>", "\n@@ xml\n"},
		{"</xml>", "\n@@\n"}
	};

	static readonly SortedList <string, string> otherSimpleReplacements = new SortedList <string, string> () {
		{"[Mono:About", "[About_Mono"},
		{"[Mono:Runtime:Documentation:ThreadSafety", "[Project_Mono_Runtime_Documentation_ThreadSafety"},
		{"[Mono:Runtime:Documentation:MemoryManagement", "[Project_Mono_Runtime_Documentation_MemoryMangement"},
		{"[Mono:Runtime:Documentation:GenericSharing", "[Project_Mono_Runtime_Documentation_GenericSharing"},
		{"[Mono:Runtime:Documentation:Generics", "[Project_Mono_Runtime_Documentation_Generics"},
		{"[Mono:Runtime:Documentation:RegisterAllocation", "[Project_Mono_Runtime_Documentation_RegisterAllocation"},
		{"[Mono:Runtime:Documentation:SoftDebugger", "[Project_Mono_Runtime_Documentation_SoftDebugger"},
		{"[Mono:Runtime:Documentation:mono-llvm.diff", "[Project_Mono_Runtime_Documentation_mono-llvm.diff"},
		{"[Mono:Runtime:Documentation:LLVM", "[Project_Mono_Runtime_Documentation_LLVM"},
		{"[Mono:Runtime:Documentation:XDEBUG", "[Project_Mono_Runtime_Documentation_XDEBUG"},
		{"[Mono:Runtime:Documentation:MiniPorting", "[Project_Mono_Runtime_Documentation_MiniPorting"},
		{"[Mono:Runtime:Documentation:AOT", "[Project_Mono_Runtime_Documentation_AOT"},
		{"[Mono:Runtime:Documentation:Trampolines", "[Project_Mono_Runtime_Documentation_Trampolines"},
		{"[Mono:Runtime:Documentation", "[Project_Mono_Runtime_Documentation"},
		{"[Mono:", "[Project_Mono_"}, // For pages in namespace Project (4)
		{"[Talk:", "[Talk_"},
		{"[User:", "[User_"},
		{"[Help:", "[Help_"},
		{"[Mono_Runtime", "[Runtime"},
		{"#REDIRECT", ">>>"}
	};
	
	public uint Id { get; set; }
	public byte NameSpace { get; set; }
	public DekiNamespace DekiNameSpace { get; set; }
	public string Title { get; set; }
	public string Text { get; internal set; }
	public string Comment { get; set; }
	public uint User { get; set; }
	public DateTime LastModified { get; set; }
	public DateTime Created { get; set; }
	public List <string> Categories { get; set; }
	public List <string> MagicWords { get; set; }
	public bool Ignore {
		get {
			return DekiNameSpace == DekiNamespace.Mediawiki ||
				DekiNameSpace == DekiNamespace.Mediawiki_talk ||
				DekiNameSpace == DekiNamespace.Category ||
				DekiNameSpace == DekiNamespace.Category_talk;
		}
	}
	
	public WikiPage (MySqlDataReader reader, Dictionary <string, bool> templates)
	{
		Id = (uint)reader ["cur_id"];
		NameSpace = (byte)reader ["cur_namespace"];
		if (NameSpace >= (sbyte)DekiNamespace.FIRST && NameSpace <= (sbyte)DekiNamespace.LAST)
			DekiNameSpace = ((DekiNamespace) NameSpace);
		else
			DekiNameSpace = DekiNamespace.Invalid;

		string titlePrefix = String.Empty;
		switch (DekiNameSpace) {
			case DekiNamespace.Talk:
			case DekiNamespace.User:
			case DekiNamespace.User_talk:
			case DekiNamespace.Help:
				titlePrefix = DekiNameSpace.ToString () + "_";
				break;
				
			case DekiNamespace.Project:
				titlePrefix = "Project_Mono_";
				break;
				
		}
		Title = titlePrefix + reader ["cur_title"] as string;
		Text = FixupContent (reader ["cur_text"] as string, templates);
		Comment = (reader ["cur_comment"] as byte[]).TinyBlobToString ();
		User = (uint)reader ["cur_user"];
		LastModified = (reader ["cur_touched"] as string).ParseDekiTime ();
		Created = (reader ["cur_timestamp"] as string).ParseDekiTime ();
	}

	public void ResolveCategories ()
	{
		using (var mconn = new MySqlConnection (DekiMigration.ConnectionString)) {
			mconn.Open ();
			var cmd = mconn.GetCommand ("SELECT DISTINCT cl_to FROM categorylinks WHERE cl_from = ?Id", new Tuple <string, object> ("Id", Id));
			using (var reader = cmd.ExecuteReader ()) {
				if (Categories == null)
					Categories = new List <string> ();

				while (reader.Read ())
					Categories.Add (reader ["cl_to"] as string);
				Categories.Sort ();
			}
		}
	}

	string FixupContent (string content, Dictionary <string, bool> templates)
	{
		if (String.IsNullOrEmpty (content))
			return String.Empty;

		string ret = CategoryLinkRegex.Replace (content, String.Empty, -1);
		ret = DashesRegex.Replace (ret, "<nowiki>--</nowiki>", -1);
		var sb = new StringBuilder (ret);

		if (DekiNameSpace != DekiNamespace.Template) {
			if (ret.IndexOf ("__NOTOC__", StringComparison.OrdinalIgnoreCase) == -1) {
				if (HRegex.IsMatch (ret)) {
					if (ret.IndexOf ("__TOC__") != -1)
						sb.Replace ("__TOC__", "{TOC}");
					else
						sb.Insert (0, "{TOC}\n");
				}
			} else {
				sb.Replace ("__NOTOC__", String.Empty);
			}
		}
		
		foreach (var kvp in otherSimpleReplacements)
			sb.Replace (kvp.Key, kvp.Value);
		
		Match match = LinkRegex.Match (sb.ToString ());
		string value, tmp;
		string[] fields;
		int newStart = 0;
		while (match.Success) {
			value = match.Value;
			if (value.Equals ("[]", StringComparison.Ordinal) || value.Equals ("[[]]", StringComparison.Ordinal) || value.Equals ("[[]", StringComparison.Ordinal)) {
				match = LinkRegex.Match (sb.ToString (), match.Index + match.Length);
				continue;
			}
			
			if (value.StartsWith ("[[", StringComparison.Ordinal))
				value = value.Substring (2, match.Length - 4).Trim ();
			else
				value = value.Substring (1, match.Length - 2).Trim ();
			if (value.IndexOf ('|') != -1) {
				fields = value.Split ('|');
				fields [0] = fields [0].Trim ().Replace (":", "_").Replace ("&", "_").Replace (" ", "_");
				sb.Remove (match.Index, match.Length);
				tmp = String.Format ("[{0}]", String.Join ("|", fields));
				newStart = match.Index + tmp.Length;
				sb.Insert (match.Index, tmp);
			} else if (value.IndexOf (' ') != -1) {
				// Might be a special case of [url://link text] or [SomeOther text]
				// We replace the first ' ' with '|' and remove invalid characters
				// before it unless it's an url
				sb.Remove (match.Index, match.Length);
				fields = value.Split (' ');
				if (!UrlRegex.IsMatch (fields [0]))
					fields [0] = fields [0].Trim ().Replace (":", "_").Replace ("&", "_").Replace (" ", "_");
				tmp = String.Format ("[{0}|{1}]", fields [0], String.Join (" ", fields, 1, fields.Length - 1));
				newStart = match.Index + tmp.Length;
				sb.Insert (match.Index, tmp);
			} else {
				if (value.IndexOf (':') != -1 || value.IndexOf ('&') != -1) {
					tmp = String.Format ("[{0}]", value.Trim ().Replace (":", "_").Replace ("&", "_").Replace (" ", "_"));
					sb.Remove (match.Index, match.Length);
					sb.Insert (match.Index, tmp);
					newStart = match.Index + tmp.Length;
				} else 
					newStart = match.Index + match.Length;
			}
			
			match = LinkRegex.Match (sb.ToString (), newStart);
		}

		match = TableRegex.Match (sb.ToString ());
		newStart = 0;
		while (match.Success) {
			sb.Remove (match.Index, match.Length);
			tmp = TranslateTable (match.Value);
			sb.Insert (match.Index, tmp);
			newStart = match.Index + tmp.Length;
			match = TableRegex.Match (sb.ToString (), newStart);
		}

		match = HRegex.Match (sb.ToString ());
		newStart = 0;
		int equalcount;
		while (match.Success) {
			value = match.Value.Trim ();
			equalcount = 0;
			for (int i = 0; i < value.Length; i++) {
				if (value [i] != '=')
					break;
				equalcount++;
			}
			
			if (equalcount >= 4)
				tmp = "\n";
			else
				tmp = "\n=";

			tmp += value;
			equalcount = 0;
			for (int i = value.Length - 1; i >= 0; i--) {
				if (value [i] != '=')
					break;
				equalcount++;
			}
			if (equalcount < 4)
				tmp += "=";
			
			sb.Remove (match.Index, match.Length);
			sb.Insert (match.Index, tmp);
			newStart = match.Index + tmp.Length;

			match = HRegex.Match (sb.ToString (), newStart);
		}
		
		foreach (var kvp in codeHighlightBlocks)
			sb.Replace (kvp.Key, kvp.Value);

		MagicWords = new List <string> ();
		match = MagicWordRegex.Match (sb.ToString ());
		while (match.Success) {
			value = match.Value.TrimStart ('{').TrimEnd ('}').Replace (" ", "_");
			if (!templates.ContainsKey (value)) {
				MagicWords.Add (value);
				newStart = match.Index + match.Length;
			} else {
				if (value.StartsWith ("Template:", StringComparison.OrdinalIgnoreCase))
					value = value.Substring (9);
				tmp = "{s:" + value + "}";
				sb.Remove (match.Index, match.Length);
				sb.Insert (match.Index, tmp);
				newStart = match.Index + tmp.Length;
			}
			
			match = MagicWordRegex.Match (sb.ToString (), newStart);
		}
		
		return sb.ToString ();
	}
	
	string TranslateTable (string table)
	{
		string[] lines = table.Split (new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length < 3)
			return table;

		var ret = new StringBuilder ();

		int start = 1;
		ret.AppendLine (lines [0]);
		if (lines [1].Trim ().StartsWith ("|+")) {
			// Caption
			start++;
			ret.AppendLine (lines [1]);
		}
		
		// * In STW only the 2nd or 3rd lines can start with " !" to mark heading cells
		// * If a trailing || is found on the line, it must be followed by some text '&nbsp;'
		//   does the trick
		var sb = new StringBuilder ();
		string line, trimmed;
		for (int i = start; i < lines.Length - 1; i++) {
			sb.Length = 0;
			line = lines [i];
			trimmed = line.Trim ();
			if (trimmed.StartsWith ("!")) {
				if (i == start)
					sb.Append (line);
				else {
					int idx = line.IndexOf ('!');
					if (idx == 0)
						sb.Append ("|" + line.Substring (1));
					else {
						sb.Append (line.Substring (0, idx));
						sb.Append ("|");
						if (idx < line.Length)
							sb.Append (line.Substring (idx + 1));
					}
				}
			} else
				sb.Append (line);
			
			if (trimmed.EndsWith ("||"))
				sb.Append (" &nbsp;");
			ret.AppendLine (sb.ToString ());
		}
		ret.AppendLine (lines [lines.Length - 1]);
		
		return ret.ToString ();
	}
}