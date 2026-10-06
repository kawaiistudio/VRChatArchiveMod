using System;
using System.Collections.Generic;

namespace VRChatArchiveMod.Core
{
	// WHAT THE ARCHIVE ITSELF IS DOING, SHOWN IN GAME.
	//
	// The two things this mod exists to serve — the AUTO ARCHIVER uploading what you load, and the
	// CACHE VIEWER recording the avatars that pass through — both run in the DESKTOP CLIENT. Until now
	// their output was only visible there, on a second screen or behind alt-tab, which is precisely
	// where nobody is looking while they are in VRChat.
	//
	// So the client pushes each line here over the existing ModControl channel and the Launch Pad
	// console draws it. This class is only a ring buffer with a clock: no parsing, no formatting, no
	// opinion about what a line means. The client already decided that when it wrote the line, and one
	// formatting decision in one place is what keeps the two from ever disagreeing.
	public static class ArchiveFeed
	{
		public enum Kind { Archiver, Cache }

		public struct Entry
		{
			public string Clock;   // HH:mm:ss, stamped on arrival
			public Kind Kind;
			public string Text;
		}

		// Deliberately small. This is a live feed on a 280-unit panel that shows eight lines at a
		// time, not an archive of its own — the client keeps the real history.
		private const int Capacity = 200;

		private static readonly List<Entry> _archiver = new List<Entry>();
		private static readonly List<Entry> _cache = new List<Entry>();

		public static IReadOnlyList<Entry> Archiver => _archiver;
		public static IReadOnlyList<Entry> Cache => _cache;

		/// <summary>Which feed the in-game console is showing. Toggled from the client.</summary>
		public static Kind Showing = Kind.Archiver;

		/// <summary>Bumped on every append, whichever feed. The console redraws on a change rather
		/// than polling two list lengths — a line arriving in the feed you are NOT looking at must not
		/// cost a rebuild, and switching feeds must redraw even though no line arrived.</summary>
		public static int Version { get; private set; }

		// HOW MANY LINES HAVE EVER ARRIVED, per feed — which is not the same as how many are kept.
		//
		// The console header used to print the LIST LENGTH. That list is a 200-entry ring: once it
		// is full every new line pushes an old one out, so the number reaches 200 and then never
		// moves again no matter how much is happening. A counter that stops counting reads as a
		// frozen panel, which is exactly how it was reported.
		private static int _archiverTotal, _cacheTotal;

		/// <summary>Lines received since launch on the feed currently being shown. Keeps rising.</summary>
		public static int CurrentTotal => Showing == Kind.Cache ? _cacheTotal : _archiverTotal;

		public static void Add(Kind kind, string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return;
			try
			{
				var list = kind == Kind.Cache ? _cache : _archiver;
				lock (list)
				{
					list.Add(new Entry
					{
						// Stamped HERE, not by the sender: the client's line may have travelled or been
						// queued, and a console showing when it ARRIVED is honest about what it knows.
						Clock = DateTime.Now.ToString("HH:mm:ss"),
						Kind = kind,
						Text = text.Length > 200 ? text.Substring(0, 199) + "…" : text,
					});
					if (list.Count > Capacity) list.RemoveAt(0);
					if (kind == Kind.Cache) _cacheTotal++; else _archiverTotal++;
				}
				Version++;
			}
			catch { }
		}

		/// <summary>Switches the console between the two feeds, and reports what it switched to.</summary>
		public static string Toggle()
		{
			Showing = Showing == Kind.Archiver ? Kind.Cache : Kind.Archiver;
			Version++;   // no line arrived, but the console must redraw
			return Showing == Kind.Cache ? "CACHE LOGS" : "ARCHIVER LOGS";
		}

		public static IReadOnlyList<Entry> Current => Showing == Kind.Cache ? _cache : _archiver;
		public static string CurrentTitle => Showing == Kind.Cache ? "CACHE LOGS" : "ARCHIVER LOGS";
	}
}
