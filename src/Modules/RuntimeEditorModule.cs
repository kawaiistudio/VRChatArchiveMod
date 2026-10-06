using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// RUNTIME UNITY EDITOR, HOSTED BY THE MOD INSTEAD OF BY ITS OWN PLUGIN.
	//
	// RuntimeUnityEditor ships a BepInEx6-IL2CPP loader plugin, and that plugin cannot run on VRChat
	// 1903. Its whole job is one line:
	//
	//     _helper = instance.AddComponent<RuntimeUnityEditorHelper>();
	//
	// which goes through ClassInjector to build an Il2CppClass by hand for a managed MonoBehaviour.
	// That is the exact call FramePump exists to avoid: on this build the class layout is shuffled
	// beyond what the struct handlers repair, and the process ends inside AddComponent. Nothing else
	// in RUE needs it -- the source contains no delegate conversion at all, and this is its ONLY
	// ClassInjector use.
	//
	// So the loader plugin is not shipped. RUE documents this path itself ("it's recommended to not
	// reference this assembly and instead reference RuntimeUnityEditorCore directly"): the core takes
	// an InitSettings and three per-frame callbacks, which is precisely what IModule already gives us
	// through FramePump's Harmony anchors. No type is injected anywhere.
	//
	// The core assembly rides INSIDE this mod as a gzipped embedded resource and is loaded from
	// memory, so there is nothing extra to install and nothing to copy into the game's plugins
	// folder -- it reaches the game through DATA\MODS like every other part of the mod.
	public sealed class RuntimeEditorModule : IModule
	{
		public override string Name => "RuntimeEditor";

		public static string Status = "off";

		// Deliberately plain delegates and `object`: this type must be loadable even when the RUE
		// assembly is missing or refuses to load, and a field of a RUE type would drag it in at type
		// load time, before the resolver below has had a chance to run.
		private Action _update, _lateUpdate, _gui;
		private bool _tried;

		public override void OnInitialize()
		{
			if (ModConfig.RuntimeEditorEnabled == null || !ModConfig.RuntimeEditorEnabled.Value)
			{
				Status = "off (RuntimeEditor.Enabled = false)";
				return;
			}
			// The resolver has to exist before any RUE type is touched, and Boot() is the first thing
			// that touches one. JIT happens per method at the call, so installing it here is enough.
			RuntimeEditorHost.InstallResolver();
		}

		// Started at UI-ready rather than at load: RUE builds windows and reads Screen/GUI state, and
		// the first scene has to be up for any of that to mean anything.
		public override void OnUiReady()
		{
			if (_tried) return;
			if (ModConfig.RuntimeEditorEnabled == null || !ModConfig.RuntimeEditorEnabled.Value) return;
			_tried = true;
			try
			{
				if (!RuntimeEditorHost.Boot(out _update, out _lateUpdate, out _gui))
				{
					// SAID OUT LOUD. This returned quietly the first time and the log showed the core
					// assembly loading and then nothing at all -- the exact silence this session has
					// been spent removing from other modules.
					Status = RuntimeEditorHost.Failure ?? "could not start";
					VRChatArchiveModPlugin.Logger.LogWarning("[RuntimeEditor] pas demarre : " + Status);
					return;
				}
				Status = "on — " + ModConfig.RuntimeEditorKey.Value + " opens it";
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[RuntimeEditor] RuntimeUnityEditor " + RuntimeEditorHost.Version + " demarre SANS type injecte — "
					+ ModConfig.RuntimeEditorKey.Value + " l'ouvre. Inspecteur de hierarchie, de composants et REPL.");
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[RuntimeEditor] demarrage refuse : " + Unwrap.Describe(e));
			}
		}

		// RUE's own helper MonoBehaviour declared exactly these three. They are driven from the mod's
		// frame pump instead, which rides on methods Unity already calls every frame.
		public override void OnUpdate() { var f = _update; if (f != null) f(); }
		public override void OnLateUpdate() { var f = _lateUpdate; if (f != null) f(); }
		public override void OnGui() { var f = _gui; if (f != null) f(); }

		public override void OnShutdown()
		{
			_update = null; _lateUpdate = null; _gui = null;
			Status = "off";
		}
	}
}
