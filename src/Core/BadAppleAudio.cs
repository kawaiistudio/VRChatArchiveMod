using System;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// THE SONG, AND THE CLOCK THE PICTURE FOLLOWS.
	//
	// The object renderer used to advance on VaClock.Now: press play, start counting.
	// That drifts from the music for a reason built into the show — the networked mode HOLDS frame 0
	// while it takes ownership of every object it is about to move (MarkModule.NetUpdate resets
	// _baStart while the queue drains, up to six seconds), and a loaded frame that arrives late slips
	// further still. Audio started at the button press was therefore ahead of the picture by however
	// long the objects took to gather — exactly the "ça bug un peu pour charger les objets" this is
	// answering.
	//
	// So the audio is the MASTER and the frame index is derived from it: idx = audio.time / interval.
	// An AudioSource runs on the audio hardware's own clock; it cannot stutter without you hearing
	// it, and every frame the renderer draws is the frame that belongs to the sample being played.
	// When the picture is held for ownership the song simply has not started yet.
	//
	// EMBEDDED, as a plain PCM WAV. This IL2CPP build has no DownloadHandlerAudioClip(string,
	// AudioType), so there is no MP3 path at all (see Core/WavAudio) — the track ships as mono
	// 22.05 kHz 16-bit, which is 9.7 MB of DLL for 3 min 39 of music and still sounds like music.
	public static class BadAppleAudio
	{
		private const string ResourceName = "badapple.wav";

		private static AudioClip _clip;
		private static string _clipName = "";     // which clip _clip currently holds
		private static AudioSource _src;
		private static GameObject _host;
		// Per clip, so one missing wav does not silence every other clip (and is not retried forever).
		private static readonly System.Collections.Generic.HashSet<string> _failed =
			new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Seconds into the song, or -1 when it is not playing. This is the clock.</summary>
		public static float Time
		{
			get
			{
				try { return _src != null && _src.isPlaying ? _src.time : -1f; }
				catch { return -1f; }
			}
		}

		public static bool Playing
		{
			get { try { return _src != null && _src.isPlaying; } catch { return false; } }
		}

		public static float Volume
		{
			get { try { return _src != null ? _src.volume : 0f; } catch { return 0f; } }
			set { try { if (_src != null) _src.volume = Mathf.Clamp01(value); } catch { } }
		}

		/// <summary>Starts the song from the beginning. Returns false when there is no audio to play —
		/// the renderer then falls back to its own wall clock and the show still runs, silently.</summary>
		public static bool Play(float volume) => Play(volume, "badapple");

		/// <summary>Starts the track that belongs to <paramref name="clip"/> from the beginning.
		/// "badapple" is the embedded song; any other name is <c>clips\&lt;name&gt;.wav</c> next to that
		/// clip's frames, so a clip brings its own audio without anything shipping in the DLL.</summary>
		public static bool Play(float volume, string clip)
		{
			try
			{
				if (!Ensure(clip)) return false;
				_src.volume = Mathf.Clamp01(volume);
				_src.time = 0f;
				_src.Play();
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[BadAppleAudio] playing ({_clip.length:0.0}s, {_clip.frequency} Hz, vol {_src.volume:0.00}).");
				return true;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[BadAppleAudio] play failed: " + e.Message);
				return false;
			}
		}

		public static void Stop()
		{
			try { if (_src != null) { _src.Stop(); _src.time = 0f; } }
			catch { }
		}

		private static bool Ensure(string clip)
		{
			string name = string.IsNullOrEmpty(clip) ? "badapple" : clip;
			if (_failed.Contains(name)) return false;
			try
			{
				if (_clip == null || !string.Equals(_clipName, name, StringComparison.OrdinalIgnoreCase))
				{
					bool builtIn = string.Equals(name, "badapple", StringComparison.OrdinalIgnoreCase);
					byte[] wav = null;
					string where;
					if (builtIn)
					{
						where = ResourceName + " (embedded)";
						using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
						if (s != null)
						{
							using var ms = new System.IO.MemoryStream();
							s.CopyTo(ms);
							wav = ms.ToArray();
						}
					}
					else
					{
						// A clip's own soundtrack, sitting beside its frames on THIS machine.
						string path = System.IO.Path.Combine(
							BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "clips", name + ".wav");
						where = path;
						try { if (System.IO.File.Exists(path)) wav = System.IO.File.ReadAllBytes(path); }
						catch { }
					}
					if (wav == null || wav.Length < 44)
					{
						_failed.Add(name);
						VRChatArchiveModPlugin.Logger.LogWarning($"[BadAppleAudio] no audio for '{name}' ({where}) — the show runs silently. PCM WAV only: this build has no MP3 decoder.");
						return false;
					}
					var decoded = WavAudio.Decode(wav, name);
					if (decoded == null)
					{
						_failed.Add(name);
						VRChatArchiveModPlugin.Logger.LogWarning($"[BadAppleAudio] '{name}' did not decode ({where}) — must be plain PCM WAV; the show runs silently.");
						return false;
					}
					_clip = decoded; _clipName = name;
					if (_src != null) _src.clip = _clip;
				}

				if (_src == null)
				{
					// Its own object, kept across scenes: a world change while the show runs must not
					// take the music with it, and nothing in the world should be able to mute it.
					_host = new GameObject("VA_BadAppleAudio");
					UnityEngine.Object.DontDestroyOnLoad(_host);
					_host.hideFlags = HideFlags.HideAndDontSave;
					_src = _host.AddComponent<AudioSource>();
					_src.clip = _clip;
					_src.loop = false;
					_src.playOnAwake = false;
					_src.spatialBlend = 0f;     // 2D: it plays in YOUR head, not at the picture
					_src.bypassEffects = true;
					_src.bypassListenerEffects = true;
					_src.bypassReverbZones = true;
					_src.ignoreListenerPause = true;
					_src.priority = 0;
				}
				return true;
			}
			catch (Exception e)
			{
				_failed.Add(string.IsNullOrEmpty(clip) ? "badapple" : clip);
				VRChatArchiveModPlugin.Logger.LogWarning("[BadAppleAudio] setup failed: " + e.Message);
				return false;
			}
		}
	}
}
