using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// Finding VRChat's menus, ONCE.
	//
	// Four modules each had their own copy of this, and each one called
	// Resources.FindObjectsOfTypeAll<Transform>() to get there. That call walks every Transform
	// loaded in the game — tens of thousands in a populated instance — to find a single object.
	// MenuTheme ran it roughly three times a second, and the profiler put the module at 283 ms/s:
	// more than a quarter of every second, spent looking up something that never moves.
	//
	// Two menus, not one: the wrist QuickMenu and the big Main Menu are separate canvases, and
	// anything that themes "the menu" has to know about both — theming only the QuickMenu is why
	// the main menu kept VRChat's own text colours.
	//
	// Each root is cached and only re-found when it has actually been destroyed. A miss is also
	// rate-limited, so a menu that does not exist yet cannot turn this back into a per-frame scan.
	public static class QuickMenu
	{
		private const string QuickName = "Canvas_QuickMenu(Clone)";
		private const string MainName = "Canvas_MainMenu(Clone)";

		private sealed class Cached
		{
			public string Name;
			public string Probe;        // a child that proves this is the real canvas, not a stray
			public Transform T;
			public float NextScan;
		}

		private static readonly Cached Quick = new Cached { Name = QuickName, Probe = "CanvasGroup/Container/Window" };
		private static readonly Cached MainM = new Cached { Name = MainName, Probe = "Container" };

		public static Transform Root() => Resolve(Quick);
		public static Transform Main() => Resolve(MainM);

		private static Transform Resolve(Cached c)
		{
			// Unity's == means a destroyed object compares equal to null, so this also catches the
			// menu being torn down and rebuilt.
			if (c.T != null) return c.T;

			float now = VaClock.Now;
			if (now < c.NextScan) return null;
			c.NextScan = now + 2f;

			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != c.Name) continue;
					try
					{
						if (!t.gameObject.scene.IsValid()) continue;
						if (c.Probe != null && t.Find(c.Probe) == null) continue;
						c.T = t;
						return c.T;
					}
					catch { }
				}
			}
			catch { }
			return null;
		}

		// True when the menu is actually on screen. Everything that only matters while the user is
		// looking at the menu should check this first: a closed menu needs no theming, no reskin and
		// no card repair, and skipping the work entirely beats making the work cheaper.
		public static bool Visible => Shown(Quick);
		public static bool MainVisible => Shown(MainM);
		public static bool AnyVisible => Visible || MainVisible;

		private static bool Shown(Cached c)
		{
			var r = Resolve(c);
			if (r == null) return false;
			try { return r.gameObject.activeInHierarchy; }
			catch { return false; }
		}

		// A world change can destroy the canvas; drop the reference so the next call re-finds it.
		public static void Forget()
		{
			Quick.T = null; Quick.NextScan = 0f;
			MainM.T = null; MainM.NextScan = 0f;
		}
	}
}
