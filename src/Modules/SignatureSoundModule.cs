using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// SIGNATURE SOUNDS — a clip that belongs to a PERSON, played the moment they arrive in your
	// instance. One entry per user id, embedded in this DLL like every other sound the mod owns.
	//
	// NOT NETWORKED, AND IT MUST NOT BE. The soundboard broadcasts a key through the Archive server
	// because a button press happens on ONE client and has to reach the others. An arrival does not:
	// every client in the instance sees the same person walk in, so every client can play the sound
	// for itself off its own roster. Three things fall out of that, and all three matter:
	//   * nothing is sent, so nobody can make your client play a sound by faking a trigger;
	//   * it works with no account, no login and no server;
	//   * THE PERSON HEARS THEIR OWN. That is the whole reason this is not on the soundboard route —
	//     over there the sender is the one client that never hears the thing it just fired, which is
	//     backwards for a sound whose entire point is "I have arrived".
	//
	// Local, 2D, one shot. Nothing is attached to a player and no game object is touched.
	public partial class SignatureSoundModule : IModule
	{
		public override string Name => "SignatureSound";

		public sealed class Signature
		{
			public readonly string Uid, Resource, Label;
			public AudioClip Clip;
			public bool Tried;
			public Signature(string uid, string resource, string label) { Uid = uid; Resource = resource; Label = label; }
		}

		// THE TABLE LIVES OUTSIDE THIS FILE, in src/Modules/SignatureSoundTable.local.cs, which is
		// gitignored — for the same reason ressources/ is. An entry is a REAL VRChat user id tied to
		// a person's name, and the publication rule for this repo is that no real user id ships in it:
		// the audit before the first push blanked the ones that had crept into ModConfig defaults.
		// The public tree therefore compiles with an EMPTY table — a partial method with no body is
		// removed by the compiler — exactly as it already compiles without the media it embeds.
		//
		// To add somebody: one line in that file, the WAV in ressources\, one <EmbeddedResource> in
		// the csproj. The WAV must be 16-bit PCM: WavAudio is a plain PCM decoder because this build's
		// IL2CPP has no DownloadHandlerAudioClip, so an .ogg decodes to null and the arrival is simply
		// silent. House format, matching spawn_darksquad.wav: 22050 Hz mono s16le.
		static partial void AddSignatures(List<Signature> list);

		private static readonly Signature[] Table = BuildTable();

		private static Signature[] BuildTable()
		{
			var list = new List<Signature>();
			AddSignatures(list);
			return list.ToArray();
		}

		/// <summary>True if this account has a signature sound — used by SpawnSoundModule so your own
		/// arrival does not play the generic stinger and a signature on top of each other.</summary>
		public static bool HasSignature(string uid) => Find(uid) != null;

		private static Signature Find(string uid)
		{
			if (string.IsNullOrEmpty(uid)) return null;
			for (int i = 0; i < Table.Length; i++)
				if (string.Equals(Table[i].Uid, uid, StringComparison.OrdinalIgnoreCase)) return Table[i];
			return null;
		}

		public static string LastPlayed = "";

		private static AudioSource _src;

		// Who was already here. A uid leaves this set when they leave, so the same person coming back
		// later plays again.
		private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// THE SETTLE PASS. Walking into a world full of people is not twenty arrivals, and announcing
		// them as one is the exact bug WatchlistModule documents at the top of its own file. The first
		// pass after a scene load therefore SEEDS the set silently...
		private bool _settled;
		// ...with one deliberate exception: YOURSELF. Your own arrival IS that first pass — the local
		// player appearing in the roster is literally the moment you finished loading in — so the seed
		// swallows everyone except you.
		private float _nextScan;
		private const float ScanSeconds = 1f;

		public override void OnInitialize()
		{
			VRChatArchiveModPlugin.Logger.LogInfo("[SignatureSound] armed — " + Table.Length + " signature(s) registered.");
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_seen.Clear();
			_settled = false;
			_nextScan = 0f;
		}

		public override void OnUpdate()
		{
			// Signature sound disabled
			return;
		}

		private static void Play(Signature sig, string who)
		{
			// Signature sound disabled
			return;
		}

		/// <summary>Preview from the menu, without waiting for that person to walk in.</summary>
		public static void PreviewFirst()
		{
			StopNow();
		}

		public static void StopNow() { try { if (_src != null) _src.Stop(); } catch { } }
	}
}
