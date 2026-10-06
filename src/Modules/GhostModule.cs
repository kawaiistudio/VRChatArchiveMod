using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// GHOST — a TOGGLE that stops YOUR OWN player from being serialized to the network.
	//
	// It does exactly what the owner did by hand in UnityExplorer: on the local player object
	// ("VRCPlayer[Local] …") the component VRC.Networking.FlatBufferNetworkSerializer is the thing that
	// packs your position / rotation / IK into the outbound stream. With it disabled nothing about your
	// body leaves the client: everyone else sees you standing exactly where you were when you switched
	// it on, while you keep moving normally on your side. Turning it off re-enables the component and
	// the next serialization catches everybody up.
	//
	// Scope: your own object only. No other client, no world state, no extra traffic (less, in fact).
	// A world change rebuilds the player object, so the toggle drops to OFF there instead of chasing a
	// component that no longer exists.
	public class GhostModule : IModule
	{
		public override string Name => "Ghost";

		/// <summary>True while the local player's network serializer is held disabled (read by the sync for the client's button).</summary>
		public static bool Active { get; private set; }
		public static string Status = "";

		private const string SerializerName = "FlatBufferNetworkSerializer";
		private static Behaviour _serializer;     // the component we hold off; re-resolved when the player object changes
		private static float _nextCheck;

		private static GameObject _ghostClone;

		public override void OnUpdate()
		{
			try
			{
				if (!Active) return;
				// Once a second, make sure it is still ours to hold: VRChat may re-enable the component
				// (avatar switch, respawn) and a re-created player object needs a fresh reference.
				float now = VaClock.Now;
				if (now < _nextCheck) return;
				_nextCheck = now + 1f;
				var s = Resolve();
				if (s == null)
				{
					Active = false;
					DespawnGhostAvatar();
					Status = "ghost off — your player object changed";
					return;
				}
				if (s.enabled) s.enabled = false;
			}
			catch (Exception e) { Status = "ghost: " + e.Message; }
		}

		private static Vector3 _ghostStartPos;
		private static Quaternion _ghostStartRot;
		private static bool _hasGhostStart;

		public static void Toggle()
		{
			try
			{
				var s = Resolve();
				if (s == null)
				{
					Active = false;
					DespawnGhostAvatar();
					Status = "ghost: your player's network serializer was not found (not in a world yet?)";
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] " + SerializerName + " not found on the local player.");
					return;
				}
				if (Active)
				{
					Active = false;
					s.enabled = true;
					DespawnGhostAvatar();

					if (ModConfig.GhostSavePosition != null && ModConfig.GhostSavePosition.Value && _hasGhostStart)
					{
						var api = PlayerRef.LocalApi();
						if (api != null)
						{
							try { api.TeleportTo(_ghostStartPos, _ghostStartRot); } catch { }
						}
						Status = "ghost OFF — returned to saved position";
						Toast.Show("Ghost OFF (Returned to saved pos)");
					}
					else
					{
						Status = "ghost OFF — others see you move again";
						Toast.Show("Ghost: OFF");
					}
					_hasGhostStart = false;
				}
				else
				{
					var api = PlayerRef.LocalApi();
					if (api != null)
					{
						_ghostStartPos = api.GetPosition();
						_ghostStartRot = api.GetRotation();
						_hasGhostStart = true;
					}
					s.enabled = false;
					Active = true;
					_nextCheck = VaClock.Now + 1f;
					SpawnGhostAvatar();
					Status = "ghost ON — you are frozen for everyone else, you still move for yourself";
					Toast.Show("Ghost: ON");
				}
				VaTagsModule.LastStatus = Status;
				VRChatArchiveModPlugin.Logger.LogInfo("[Ghost] " + Status);
			}
			catch (Exception e) { Status = "ghost: " + e.Message; VaTagsModule.LastStatus = Status; }
		}

		#region Ghost Avatar Cloning

		private static Transform GetLocalAvatarTransform()
		{
			try
			{
				Transform localTrans = PlayerRef.LocalTransform();
				if (localTrans != null && Core.NativeGuard.Alive(localTrans))
				{
					var av = localTrans.Find("ForwardDirection/Avatar");
					if (av != null && Core.NativeGuard.Alive(av) && av.gameObject.activeInHierarchy)
						return av;

					var fwd = localTrans.Find("ForwardDirection");
					if (fwd != null && Core.NativeGuard.Alive(fwd))
					{
						var av2 = fwd.Find("Avatar");
						if (av2 != null && Core.NativeGuard.Alive(av2))
							return av2;
					}
				}

				// Fallback search across transforms if direct hierarchy lookup misses
				var all = UnityEngine.Object.FindObjectsOfType<Transform>();
				if (all != null)
				{
					foreach (var t in all)
					{
						if (t == null || !Core.NativeGuard.Alive(t)) continue;
						if (t.name == "Avatar" && t.parent != null && t.parent.name == "ForwardDirection")
						{
							var root = t.root;
							if (root != null && root.name.StartsWith("VRCPlayer[Local]"))
							{
								return t;
							}
						}
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] GetLocalAvatarTransform: " + e.Message);
			}
			return null;
		}

		private static void SpawnGhostAvatar()
		{
			try
			{
				DespawnGhostAvatar();

				Transform avatarTrans = GetLocalAvatarTransform();
				if (avatarTrans == null || !Core.NativeGuard.Alive(avatarTrans))
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] Local avatar transform not found to clone.");
					return;
				}

				GameObject sourceGo = avatarTrans.gameObject;
				if (sourceGo == null || !Core.NativeGuard.Alive(sourceGo)) return;

				Vector3 worldPos = avatarTrans.position;
				Quaternion worldRot = avatarTrans.rotation;
				Vector3 worldScale = avatarTrans.lossyScale;

				// Clone the avatar hierarchy
				_ghostClone = UnityEngine.Object.Instantiate(sourceGo, worldPos, worldRot);
				if (_ghostClone == null || !Core.NativeGuard.Alive(_ghostClone))
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] Failed to instantiate avatar clone.");
					return;
				}

				_ghostClone.name = "Avatarghostmode";
				// Unparent into world root so it stays frozen regardless of player movement
				_ghostClone.transform.SetParent(null, true);
				_ghostClone.transform.position = worldPos;
				_ghostClone.transform.rotation = worldRot;
				_ghostClone.transform.localScale = worldScale;

				CleanGhostAvatar(_ghostClone);

				VRChatArchiveModPlugin.Logger.LogInfo("[Ghost] Spawned frozen avatar clone 'Avatarghostmode' at " + worldPos);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError("[Ghost] SpawnGhostAvatar failed: " + e);
			}
		}

		private static void CleanGhostAvatar(GameObject clone)
		{
			if (clone == null || !Core.NativeGuard.Alive(clone)) return;

			try
			{
				// 1. Disable and destroy all Animators to freeze bones in their exact pose
				var animators = clone.GetComponentsInChildren<Animator>(true);
				if (animators != null)
				{
					foreach (var anim in animators)
					{
						if (anim == null) continue;
						try { anim.enabled = false; } catch { }
						try { UnityEngine.Object.Destroy(anim); } catch { }
					}
				}

				// 2. Disable & destroy all non-renderer Behaviours (IK, look targets, constraints, VRC scripts)
				var behaviours = clone.GetComponentsInChildren<Behaviour>(true);
				if (behaviours != null)
				{
					foreach (var b in behaviours)
					{
						if (b == null) continue;
						if (b is Renderer) continue;
						try { b.enabled = false; } catch { }
						try { UnityEngine.Object.Destroy(b); } catch { }
					}
				}

				// 3. Disable & destroy Colliders
				var colliders = clone.GetComponentsInChildren<Collider>(true);
				if (colliders != null)
				{
					foreach (var col in colliders)
					{
						if (col == null) continue;
						try { col.enabled = false; } catch { }
						try { UnityEngine.Object.Destroy(col); } catch { }
					}
				}

				// 4. Destroy Rigidbodies
				var bodies = clone.GetComponentsInChildren<Rigidbody>(true);
				if (bodies != null)
				{
					foreach (var rb in bodies)
					{
						if (rb == null) continue;
						try { UnityEngine.Object.Destroy(rb); } catch { }
					}
				}

				// 5. Disable & destroy AudioSources
				var audios = clone.GetComponentsInChildren<AudioSource>(true);
				if (audios != null)
				{
					foreach (var a in audios)
					{
						if (a == null) continue;
						try { a.Stop(); a.enabled = false; } catch { }
						try { UnityEngine.Object.Destroy(a); } catch { }
					}
				}

				// 6. Pause any active particle systems
				var particles = clone.GetComponentsInChildren<ParticleSystem>(true);
				if (particles != null)
				{
					foreach (var ps in particles)
					{
						if (ps == null) continue;
						try { ps.Pause(); } catch { }
					}
				}

				// 7. Ensure SkinnedMeshRenderers remain visible from any viewing angle & reset head-chop blendshapes
				var smrs = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
				if (smrs != null)
				{
					foreach (var smr in smrs)
					{
						if (smr == null) continue;
						try { smr.updateWhenOffscreen = true; } catch { }

						// Reset any blendshapes that might be hiding the head or facial features
						try
						{
							if (smr.sharedMesh != null)
							{
								int bsCount = smr.sharedMesh.blendShapeCount;
								for (int b = 0; b < bsCount; b++)
								{
									string bsName = smr.sharedMesh.GetBlendShapeName(b);
									if (bsName.IndexOf("headchop", StringComparison.OrdinalIgnoreCase) >= 0
										|| bsName.IndexOf("head_chop", StringComparison.OrdinalIgnoreCase) >= 0
										|| bsName.IndexOf("hidehead", StringComparison.OrdinalIgnoreCase) >= 0
										|| bsName.IndexOf("hide_head", StringComparison.OrdinalIgnoreCase) >= 0
										|| bsName.IndexOf("nohead", StringComparison.OrdinalIgnoreCase) >= 0)
									{
										smr.SetBlendShapeWeight(b, 0f);
									}
								}
							}
						}
						catch { }
					}
				}

				// 8. Restore head bone scale and all chopped/shrunk bones for local first person
				Transform mirrorClone = null;
				try
				{
					Transform localTrans = PlayerRef.LocalTransform();
					if (localTrans != null)
					{
						mirrorClone = localTrans.Find("ForwardDirection/_AvatarMirrorClone") ?? localTrans.Find("ForwardDirection/_AvatarShadowClone");
					}
				}
				catch { }

				var transforms = clone.GetComponentsInChildren<Transform>(true);
				if (transforms != null)
				{
					foreach (var t in transforms)
					{
						if (t == null) continue;

						// Check if this bone is Head or under Head, or was shrunken/chopped (< 0.1 scale)
						bool isHeadOrChild = t.name.Equals("Head", StringComparison.OrdinalIgnoreCase)
							|| (t.parent != null && t.parent.name.Equals("Head", StringComparison.OrdinalIgnoreCase))
							|| t.name.IndexOf("headchop", StringComparison.OrdinalIgnoreCase) >= 0;

						bool isShrunk = t.localScale.sqrMagnitude < 0.1f
							|| Mathf.Abs(t.localScale.x) < 0.1f
							|| Mathf.Abs(t.localScale.y) < 0.1f
							|| Mathf.Abs(t.localScale.z) < 0.1f;

						if (isHeadOrChild || isShrunk)
						{
							Vector3 restoredScale = Vector3.one;
							if (mirrorClone != null)
							{
								var mirrorBone = FindDeepChild(mirrorClone, t.name);
								if (mirrorBone != null && mirrorBone.localScale.sqrMagnitude > 0.1f)
								{
									restoredScale = mirrorBone.localScale;
								}
							}

							t.localScale = restoredScale;
						}
					}
				}

				// 9. Put on layer 0 (Default) so mirrors, camera, and player can clearly see it
				SetLayerRecursively(clone, 0);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] CleanGhostAvatar: " + e.Message);
			}
		}

		private static void SetLayerRecursively(GameObject obj, int layer)
		{
			if (obj == null) return;
			try { obj.layer = layer; } catch { }
			var t = obj.transform;
			if (t == null) return;
			int childCount = t.childCount;
			for (int i = 0; i < childCount; i++)
			{
				var child = t.GetChild(i);
				if (child != null && child.gameObject != null)
				{
					SetLayerRecursively(child.gameObject, layer);
				}
			}
		}

		private static Transform FindDeepChild(Transform parent, string name)
		{
			if (parent == null) return null;
			if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return parent;
			for (int i = 0; i < parent.childCount; i++)
			{
				var found = FindDeepChild(parent.GetChild(i), name);
				if (found != null) return found;
			}
			return null;
		}

		private static void DespawnGhostAvatar()
		{
			try
			{
				if (_ghostClone != null && Core.NativeGuard.Alive(_ghostClone))
				{
					UnityEngine.Object.Destroy(_ghostClone);
				}

				var stray = GameObject.Find("Avatarghostmode");
				if (stray != null && Core.NativeGuard.Alive(stray))
				{
					UnityEngine.Object.Destroy(stray);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] DespawnGhostAvatar: " + e.Message);
			}
			finally
			{
				_ghostClone = null;
			}
		}

		#endregion

		// The serializer on the local player's root object ("VRCPlayer[Local] …"), by il2cpp type name —
		// the class is not referenced by the mod, so the name keeps this build-independent. Three
		// name sources are tried per component (native class name, interop type, managed type), the
		// root's own components first, then the whole player hierarchy; when nothing matches, the
		// names actually found are logged once so the next report says what the build calls it.
		private static float _nextDiag;
		private static Behaviour Resolve()
		{
			try
			{
				if (_serializer != null && Core.NativeGuard.Alive(_serializer)) return _serializer;
				_serializer = null;
				var root = PlayerRef.LocalPlayer();
				GameObject go = null;
				try { go = root != null && Core.NativeGuard.Alive(root) ? root.gameObject : null; } catch { go = null; }
				if (go == null)
				{
					var t = PlayerRef.LocalTransform();
					try { go = t != null && Core.NativeGuard.Alive(t) ? t.gameObject : null; } catch { go = null; }
				}
				if (go == null || !Core.NativeGuard.Alive(go)) return null;

				// FIRST BY TYPE. On this build the il2cpp class names on the player are obfuscated (the
				// diagnostic below printed garbage for 16 of 19 components), so a name comparison finds
				// nothing. The interop assemblies carry the de-obfuscated type — resolve it once and ask
				// Unity for that component directly.
				Behaviour found = null;
				var il2 = SerializerType();
				if (il2 != null)
				{
					Component comp = null;
					try { comp = go.GetComponentSafe(il2); } catch { comp = null; }
					if (comp == null) { try { comp = go.GetComponentInChildrenSafe(il2, true); } catch { comp = null; } }
					if (comp != null && Core.NativeGuard.Alive(comp))
					{
						found = comp.TryCast<Behaviour>();
						if (found == null) { try { found = new Behaviour(comp.Pointer); } catch { found = null; } }
					}
				}
				if (found == null) found = Find(go.GetComponents<Component>());
				if (found == null) found = Find(go.GetComponentsInChildren<Component>(true));
				if (found == null)
				{
					float now = VaClock.Now;
					if (now >= _nextDiag)
					{
						_nextDiag = now + 10f;
						var names = new System.Text.StringBuilder();
						try
						{
							var comps = go.GetComponents<Component>();
							int k = 0;
							foreach (var c in comps) { if (c == null) continue; if (k++ > 0) names.Append(", "); names.Append(NameOf(c)); if (k > 40) break; }
						}
						catch { }
						VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] no component named *" + SerializerName + "* on '" + go.name + "'. Components there: " + names);
					}
					return null;
				}
				_serializer = found;
				return found;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] resolve: " + e.Message); }
			return null;
		}

		// The de-obfuscated managed type from the interop assemblies (VRC.Networking.FlatBufferNetworkSerializer),
		// turned into the il2cpp Type GetComponent understands. Looked up once per session; a miss is logged
		// with the reason so the next report names the assembly it lives in.
		private static Il2CppSystem.Type _il2Type;
		private static bool _typeTried;
		private static Il2CppSystem.Type SerializerType()
		{
			if (_typeTried) return _il2Type;
			_typeTried = true;
			try
			{
				System.Type t = null;
				var asms = AppDomain.CurrentDomain.GetAssemblies();
				// cheap first: the fully qualified name in every loaded assembly
				foreach (var asm in asms)
				{
					try { t = asm.GetType("VRC.Networking." + SerializerName, false); } catch { t = null; }
					if (t != null) break;
				}
				if (t == null)
				{
					try { t = System.Reflection.Assembly.Load("Assembly-CSharp").GetType("VRC.Networking." + SerializerName, false); } catch { t = null; }
				}
				if (t == null)
				{
					// slow path, once: any type of that simple name
					foreach (var asm in asms)
					{
						System.Type[] types;
						try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; } catch { continue; }
						if (types == null) continue;
						foreach (var ty in types) if (ty != null && ty.Name == SerializerName) { t = ty; break; }
						if (t != null) break;
					}
				}
				if (t == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] no managed type named " + SerializerName + " in the loaded interop assemblies.");
					return null;
				}
				_il2Type = Il2CppInterop.Runtime.Il2CppType.From(t);
				VRChatArchiveModPlugin.Logger.LogInfo("[Ghost] serializer type resolved: " + t.FullName + " (" + t.Assembly.GetName().Name + ")");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] type lookup: " + e.Message); }
			return _il2Type;
		}

		private static Behaviour Find(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Component> comps)
		{
			if (comps == null) return null;
			foreach (var c in comps)
			{
				try
				{
					if (c == null || !Core.NativeGuard.Alive(c)) continue;
					if (NameOf(c).IndexOf(SerializerName, StringComparison.OrdinalIgnoreCase) < 0) continue;
					var b = c.TryCast<Behaviour>();
					// A MonoBehaviour the interop cannot cast (class not in the generated assemblies) still IS
					// a Behaviour natively: wrap the same pointer.
					if (b == null) { try { b = new Behaviour(c.Pointer); } catch { b = null; } }
					if (b != null) return b;
				}
				catch { }
			}
			return null;
		}

		private static string NameOf(Component c)
		{
			string n = "";
			try { n = Core.MenuCard.Il2CppNameOf(c) ?? ""; } catch { }
			if (n.Length == 0 || n == "?")
			{
				try { n = c.GetIl2CppType().FullName ?? ""; } catch { }
			}
			if (n.Length == 0) { try { n = c.GetType().Name; } catch { } }
			return n ?? "";
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The player object is rebuilt with the world: the old component is gone, and a new world
			// starts un-ghosted so nobody is surprised by a frozen you on arrival.
			_serializer = null;
			_hasGhostStart = false;
			DespawnGhostAvatar();
			if (Active) { Active = false; Status = "ghost off (world changed)"; }
		}

		public override void OnShutdown()
		{
			try { var s = Resolve(); if (s != null) s.enabled = true; } catch { }
			DespawnGhostAvatar();
			Active = false;
		}
	}
}
