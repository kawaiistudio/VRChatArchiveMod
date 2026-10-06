using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using UnityEngine;
using UnityEngine.EventSystems;
using RuntimeUnityEditor.Core;
using RuntimeUnityEditor.Core.Utils.Abstractions;

namespace VRChatArchiveMod.Modules
{
	// EVERYTHING THAT TOUCHES A RuntimeUnityEditor TYPE LIVES HERE, and nowhere else.
	//
	// A .NET type is loaded when a method that mentions it is first JIT-compiled, so keeping the RUE
	// references confined to this class is what lets RuntimeEditorModule exist -- and the whole mod
	// load -- on a machine where the RUE assembly cannot be produced at all. Every entry point below
	// is called from inside a try/catch after InstallResolver() has run.
	internal static class RuntimeEditorHost
	{
		internal static string Failure;
		internal static string Version = "?";

		private const string CoreName = "RuntimeUnityEditor.Core.IL2CPP";
		private static bool _resolverInstalled;
		private static readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

		// ---------------------------------------------------------------- loading from memory

		// LoadFromStream on the DEFAULT context, not Assembly.Load(byte[]).
		//
		// On .NET 6 Assembly.Load(byte[]) drops the assembly into an anonymous load context, and a type
		// from an anonymous context is NOT the same type as the one this mod was compiled against even
		// though the names match -- the InitSettings subclass below would fail to bind with a
		// MissingMethodException that names the right method. Loading into Default unifies them.
		internal static void InstallResolver()
		{
			if (_resolverInstalled) return;
			_resolverInstalled = true;

			AssemblyLoadContext.Default.Resolving += (ctx, name) =>
			{
				try { return FromResource(name.Name); } catch { return null; }
			};
			// BepInEx does not put plugins in the default context on every host, so the classic hook is
			// kept as well: whichever one fires, the same cached assembly comes back.
			AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
			{
				try { return FromResource(new AssemblyName(e.Name).Name); } catch { return null; }
			};
		}

		// The two assemblies this mod carries. "mcs" is Mono's C# compiler and only the REPL window
		// needs it, so a failure to produce it must not stop the rest of the editor from starting.
		private static Assembly FromResource(string simpleName)
		{
			if (string.IsNullOrEmpty(simpleName)) return null;
			lock (_loaded)
			{
				if (_loaded.TryGetValue(simpleName, out Assembly cached)) return cached;

				string res = null;
				if (simpleName == CoreName) res = "rue.core.gz";
				else if (simpleName == "mcs") res = "rue.mcs.gz";
				if (res == null) return null;

				byte[] raw = Gunzip(res);
				if (raw == null) return null;
				using (var ms = new MemoryStream(raw))
				{
					Assembly a = AssemblyLoadContext.Default.LoadFromStream(ms);
					_loaded[simpleName] = a;
					VRChatArchiveModPlugin.Logger.LogInfo("[RuntimeEditor] " + simpleName + " charge depuis la ressource embarquee ("
						+ (raw.Length / 1024) + " Ko).");
					return a;
				}
			}
		}

		private static byte[] Gunzip(string resourceName)
		{
			try
			{
				using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
				{
					if (s == null) return null;
					using (var gz = new GZipStream(s, CompressionMode.Decompress))
					using (var outp = new MemoryStream(1 << 20))
					{
						gz.CopyTo(outp);
						return outp.ToArray();
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[RuntimeEditor] ressource " + resourceName + " illisible : " + e.Message);
				return null;
			}
		}

		// ---------------------------------------------------------------- boot

		// A BOOT THAT KILLS THE PROCESS MUST NOT BE TRIED TWICE.
		//
		// Constructing the editor instantiates every RUE feature, and a feature is free to touch il2cpp
		// in ways this build does not survive -- an access violation there takes VRChat down before
		// anything can catch it, and the mod would be blamed for a tool nobody asked to start. Same
		// contract as Il2CppDelegates: a marker file is written immediately before the constructor and
		// deleted the moment it returns. If it is still there next launch the boot died inside it, and
		// the editor stays off until VRChat updates (the marker is keyed to GameAssembly.dll's size).
		private static string CookiePath
		{
			get
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod");
				Directory.CreateDirectory(dir);
				long key = 0;
				try { key = new FileInfo(Path.Combine(BepInEx.Paths.GameRootPath, "GameAssembly.dll")).Length; }
				catch { }
				return Path.Combine(dir, "runtimeeditor-crash-" + key + ".marker");
			}
		}

		internal static bool Boot(out Action update, out Action lateUpdate, out Action gui)
		{
			update = lateUpdate = gui = null;
			Failure = null;

			string cookie = null;
			try { cookie = CookiePath; } catch { }
			if (cookie != null && File.Exists(cookie))
			{
				Failure = "le demarrage a tue le process au lancement precedent sur ce build — editeur desarme";
				VRChatArchiveModPlugin.Logger.LogWarning("[RuntimeEditor] " + Failure
					+ ". Il sera re-teste automatiquement a la prochaine mise a jour de VRChat.");
				return false;
			}

			MonoBehaviour host = AnyLiveBehaviour();
			if (host == null) { Failure = "aucun MonoBehaviour vivant a emprunter"; return false; }

			var settings = new VaInitSettings(host);

			// The constructor is internal, and RUE means it: the supported way in is its own loader
			// plugin. We are standing in for that plugin, so the same call is made by reflection
			// rather than reimplemented.
			object core;
			try
			{
				try { if (cookie != null) File.WriteAllText(cookie, "boot"); } catch { }
				core = Activator.CreateInstance(typeof(RuntimeUnityEditorCore),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
					null, new object[] { settings }, CultureInfo.InvariantCulture);
			}
			catch (Exception e)
			{
				Failure = "constructeur refuse : " + Core.Unwrap.Describe(e);
				return false;
			}
			finally { try { if (cookie != null && File.Exists(cookie)) File.Delete(cookie); } catch { } }
			if (core == null) { Failure = "constructeur a rendu null"; return false; }

			try { Version = RuntimeUnityEditorCore.Version; } catch { }

			// Bound once as delegates rather than invoked reflectively each frame: these run three
			// times per frame for the life of the process.
			update = Bind(core, "Update");
			lateUpdate = Bind(core, "LateUpdate");
			gui = Bind(core, "OnGUI");
			if (update == null || gui == null)
			{
				Failure = "callbacks introuvables sur RuntimeUnityEditorCore";
				return false;
			}
			return true;
		}

		private static Action Bind(object core, string method)
		{
			try
			{
				MethodInfo mi = core.GetType().GetMethod(method,
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
				return mi == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), core, mi);
			}
			catch { return null; }
		}

		// RUE wants a MonoBehaviour to hang coroutines off and to identify its plugin object. It does
		// NOT want a specific one -- so rather than injecting a type for it, one of VRChat's own is
		// borrowed. The EventSystem is the natural pick: it exists for as long as the UI does, it is
		// already what FramePump rides on for Update, and nothing about it is touched beyond being
		// handed back as a reference.
		private static MonoBehaviour AnyLiveBehaviour()
		{
			try
			{
				EventSystem es = EventSystem.current;
				if (es != null && NativeGuardAlive(es)) return es;
			}
			catch { }
			try
			{
				Transform root = Core.QuickMenu.Main() ?? Core.QuickMenu.Root();
				if (root != null)
				{
					var mb = root.GetComponentInChildren<MonoBehaviour>(true);
					if (mb != null && NativeGuardAlive(mb)) return mb;
				}
			}
			catch { }
			try
			{
				Camera c = Camera.main;
				if (c != null)
				{
					var mb = c.GetComponent<MonoBehaviour>();
					if (mb != null && NativeGuardAlive(mb)) return mb;
				}
			}
			catch { }
			return null;
		}

		private static bool NativeGuardAlive(UnityEngine.Object o)
		{
			try { return Core.NativeGuard.Alive(o); } catch { return o != null; }
		}

		// ---------------------------------------------------------------- settings

		// RUE asks the host to own persistence. Its settings are a handful of hotkeys and window
		// sizes, so they go to one flat file next to the mod's other state rather than into the mod's
		// own config, which the desktop client reads and would then show a dozen entries nobody set.
		private sealed class VaInitSettings : InitSettings
		{
			private readonly MonoBehaviour _host;
			private readonly ILoggerWrapper _log = new VaLogger();
			private static readonly Dictionary<string, string> _store = new Dictionary<string, string>(StringComparer.Ordinal);
			private static string _file;
			private static bool _read;

			internal VaInitSettings(MonoBehaviour host) { _host = host; }

			public override MonoBehaviour PluginMonoBehaviour => _host;
			public override ILoggerWrapper LoggerWrapper => _log;

			public override string ConfigPath
			{
				get
				{
					try
					{
						string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "runtimeeditor");
						Directory.CreateDirectory(d);
						return d;
					}
					catch { return BepInEx.Paths.BepInExRootPath; }
				}
			}

			protected override Action<T> RegisterSetting<T>(string category, string name, T defaultValue,
				string description, Action<T> onValueUpdated)
			{
				string key = category + "/" + name;
				T start = defaultValue;

				// The mod's own key binding wins over RUE's F12 default, so the hotkey is configured in
				// one place and the desktop client can show it like every other binding.
				if (typeof(T) == typeof(KeyCode) && name.IndexOf("Open/close", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					try
					{
						var k = (KeyCode)Enum.Parse(typeof(KeyCode), Core.ModConfig.RuntimeEditorKey.Value, true);
						start = (T)(object)k;
					}
					catch { }
				}
				else
				{
					Load();
					if (_store.TryGetValue(key, out string stored))
					{
						try { start = Parse<T>(stored); } catch { }
					}
				}

				// The contract: fire once now, with either the stored value or the default.
				try { onValueUpdated(start); } catch { }

				return v =>
				{
					try
					{
						lock (_store)
						{
							_store[key] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
							Save();
						}
					}
					catch { }
				};
			}

			private static T Parse<T>(string raw)
			{
				Type t = typeof(T);
				if (t.IsEnum) return (T)Enum.Parse(t, raw, true);
				return (T)Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
			}

			private void Load()
			{
				if (_read) return;
				_read = true;
				try
				{
					_file = Path.Combine(ConfigPath, "settings.txt");
					if (!File.Exists(_file)) return;
					foreach (string line in File.ReadAllLines(_file))
					{
						int eq = line.IndexOf('=');
						if (eq <= 0) continue;
						_store[line.Substring(0, eq)] = line.Substring(eq + 1);
					}
				}
				catch { }
			}

			private void Save()
			{
				try
				{
					if (_file == null) _file = Path.Combine(ConfigPath, "settings.txt");
					var lines = new List<string>(_store.Count);
					foreach (var kv in _store) lines.Add(kv.Key + "=" + kv.Value);
					File.WriteAllLines(_file, lines);
				}
				catch { }
			}
		}

		// RUE's levels are BepInEx5's, and BepInEx6 spells them differently -- mapped rather than cast
		// so a future renumbering on either side cannot silently turn errors into debug spam.
		private sealed class VaLogger : ILoggerWrapper
		{
			public void Log(LogLevel logLevel, object content)
			{
				string s;
				try { s = "[RUE] " + content; } catch { return; }
				try
				{
					if ((logLevel & (LogLevel.Fatal | LogLevel.Error)) != 0) VRChatArchiveModPlugin.Logger.LogError(s);
					else if ((logLevel & LogLevel.Warning) != 0) VRChatArchiveModPlugin.Logger.LogWarning(s);
					else if ((logLevel & (LogLevel.Message | LogLevel.Info)) != 0) VRChatArchiveModPlugin.Logger.LogInfo(s);
					else VRChatArchiveModPlugin.Logger.LogDebug(s);
				}
				catch { }
			}
		}
	}
}
