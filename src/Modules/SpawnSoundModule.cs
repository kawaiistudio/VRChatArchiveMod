using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// SPAWN STINGER — plays "The Spawn Dark Squad" once each time you finish loading into an
	// instance. Strictly a local, one-shot 2D AudioSource — nothing is sent anywhere and no game
	// object is touched. Toggle + volume live in Settings.
	//
	// The spawn moment is the RISING edge of the local player existing (VRC.Player.prop_Player_0
	// goes null → non-null on every instance join). It deliberately does NOT ride on
	// EraLoadingModule.IsLoading any more: that module returns early when the 2017 loading screen is
	// switched off, so IsLoading stayed false forever and the falling edge this used to wait for
	// never arrived — the stinger silently stopped working while its own toggle still read ON.
	// One feature's toggle must not decide whether another feature runs.
	public class SpawnSoundModule : IModule
	{
		public override string Name => "SpawnSound";

		private static AudioSource _src;
		private static AudioClip _clip;
		private static bool _clipRequested;

		// Spawn state. AwaySeconds exists because the local player reference can blink null for a
		// frame or two mid-session (avatar swap, respawn); without it every blink fired the stinger.
		private bool _inWorld;
		private float _goneSince = -1f;
		private const float AwaySeconds = 2f;

		public override void OnInitialize()
		{
			RequestClip();
			VRChatArchiveModPlugin.Logger.LogInfo("[SpawnSound] armed.");
		}

		public override void OnUpdate()
		{
			// Spawn sound disabled
			return;
		}

		// A world change tears the local player down; clearing the timestamp here means the next
		// appearance counts as a spawn even if the gap was short.
		public override void OnSceneLoaded(int buildIndex)
		{
			_inWorld = false;
			_goneSince = 0f;
		}

		private static void Play()
		{
			// Spawn sound disabled
			return;
		}

		// Preview from the Settings "Test" button, without having to rejoin an instance.
		public static void PlayNow() => StopNow();

		// Cut it off immediately when the user flips the toggle off from the menu.
		public static void StopNow() { try { if (_src != null) _src.Stop(); } catch { } }

		public static bool IsPlaying { get { try { return _src != null && _src.isPlaying; } catch { return false; } } }

		private static void RequestClip()
		{
			if (_clip != null || _clipRequested) return;
			_clipRequested = true;
			try
			{
				using (var res = typeof(SpawnSoundModule).Assembly.GetManifestResourceStream("spawn_darksquad.wav"))
				{
					if (res == null) { VRChatArchiveModPlugin.Logger.LogWarning("[SpawnSound] embedded wav missing."); return; }
					var wav = new byte[res.Length];
					int off = 0, n;
					while (off < wav.Length && (n = res.Read(wav, off, wav.Length - off)) > 0) off += n;
					_clip = WavAudio.Decode(wav, "ArchiveSpawnSound");
					VRChatArchiveModPlugin.Logger.LogInfo(_clip != null ? "[SpawnSound] clip decoded." : "[SpawnSound] clip decode failed.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[SpawnSound] clip load failed: {e.Message}"); }
		}
	}
}
