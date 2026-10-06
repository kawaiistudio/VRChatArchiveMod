using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod
{
	// THE SAME HAZARD AS THE SCENE SEARCHES, ON A MUCH BUSIER PATH.
	//
	// GetComponent and its relatives have a non-generic form taking an Il2CppSystem.Type, and the mod
	// uses it deliberately: the generic form returns nothing for a VRChat class on these builds. But
	// it carries the same danger as FindObjectsOfType -- Il2CppType.Of<T>() for a class this build
	// renamed hands back an object whose pointer is not live memory, and the native call reads a
	// scripting class straight off it. EspCameraGuard called GetComponent once per camera per frame
	// and killed the game thirty seconds in.
	//
	// These wrappers vet the type with Live.UsableType first and return nothing when it is not real,
	// which is the answer every caller already handles ("the component is not here"). They live in
	// the mod's root namespace so every file sees them with no extra using, and they are named
	// *Safe so a plain GetComponent left anywhere stands out as unchecked.
	// A COMPONENT RECEIVER IS ROUTED THROUGH ITS GameObject, AND THAT IS NOT COSMETIC.
	//
	// On this build every GetComponent* on the live UnityEngine.Component class is missing -- all
	// nine of them, confirmed by dumping the class's own member list. That is not an accident of
	// obfuscation: in C# those methods are one-line forwarders to gameObject.GetComponent(...), so
	// il2cpp inlines them away and leaves nothing to bind, exactly as it does to Mathf.Max. The
	// GameObject versions survive because they are native externs, which cannot be inlined.
	// Il2CppInterop still generates the Component overloads, their pointers stay missing-member
	// stubs, and invoking one is an access violation -- EspCameraGuard hit it every frame.
	// Forwarding here does in managed code what the engine's own forwarder used to do.
	internal static class LiveComponents
	{
		private static readonly Il2CppReferenceArray<Component> NoComponents =
			new Il2CppReferenceArray<Component>(0);

		// PICK AN OVERLOAD THIS BUILD ACTUALLY HAS.
		//
		// Unity 6 deleted GetComponent(Type) from both GameObject and Component -- their live classes
		// carry only GetComponent/0 (the generic), GetComponentFastPath/2 and TryGetComponent/2. The
		// 1903 interop still exposes the (Type) overload, bound to a missing-member stub, and invoking
		// it is an access violation. TryGetComponent(Type, out Component) is present on both builds and
		// answers the same question, so it is preferred and the old overload is used only where it is
		// genuinely bound. Decided once per process, not per call.
		private static readonly MethodBase GoGetComponents = Live.Overload(typeof(GameObject), "GetComponents", typeof(Il2CppSystem.Type));
		private static readonly MethodBase GoTryGet = Live.Overload(typeof(GameObject), "TryGetComponent", typeof(Il2CppSystem.Type), typeof(Component).MakeByRefType());
		private static readonly MethodBase GoGet = Live.Overload(typeof(GameObject), "GetComponent", typeof(Il2CppSystem.Type));
		private static int _goMode = -1;   // 0 = TryGetComponent, 1 = GetComponent, 2 = neither

		private static int GoMode()
		{
			if (_goMode >= 0) return _goMode;
			if (GoTryGet != null && Live.SlotAlive(GoTryGet)) _goMode = 0;
			else if (GoGet != null && Live.SlotAlive(GoGet)) _goMode = 1;
			else _goMode = 2;
			VRChatArchiveModPlugin.Logger.LogInfo("[LiveComponents] recherche de composant par type : "
				+ (_goMode == 0 ? "TryGetComponent(Type, out)" : _goMode == 1 ? "GetComponent(Type)" : "AUCUNE surcharge liee sur ce build"));
			return _goMode;
		}

		internal static Component GetComponentSafe(this GameObject go, Il2CppSystem.Type t)
		{
			if (go == null || !Live.UsableType(t)) return null;
			try
			{
				switch (GoMode())
				{
					case 0:
						Component found;
						return go.TryGetComponent(t, out found) ? found : null;
					case 1:
						return go.GetComponent(t);
					default:
						return null;
				}
			}
			catch { return null; }
		}

		internal static Component GetComponentSafe(this Component c, Il2CppSystem.Type t)
			=> c == null ? null : c.gameObject.GetComponentSafe(t);

		// Arity 1 is gone on Unity 6 (live class has GetComponentInParent/2); the two-argument form
		// exists on both builds and includeInactive:false matches the old default.
		internal static Component GetComponentInParentSafe(this GameObject go, Il2CppSystem.Type t)
		{
			if (go == null || !Live.UsableType(t)) return null;
			try { return go.GetComponentInParent(t, false); } catch { return null; }
		}

		internal static Component GetComponentInParentSafe(this Component c, Il2CppSystem.Type t)
			=> c == null ? null : c.gameObject.GetComponentInParentSafe(t);

		internal static Component GetComponentInChildrenSafe(this GameObject go, Il2CppSystem.Type t, bool includeInactive = false)
		{
			if (go == null || !Live.UsableType(t)) return null;
			try { return go.GetComponentInChildren(t, includeInactive); } catch { return null; }
		}

		internal static Component GetComponentInChildrenSafe(this Component c, Il2CppSystem.Type t, bool includeInactive = false)
			=> c == null ? null : c.gameObject.GetComponentInChildrenSafe(t, includeInactive);

		internal static Il2CppReferenceArray<Component> GetComponentsSafe(this GameObject go, Il2CppSystem.Type t)
		{
			// GetComponents(Type) is gone on Unity 6 and has no one-call replacement that the interop
			// exposes, so it degrades to nothing rather than crashing.
			if (go == null || !Live.UsableType(t) || !Live.SlotAlive(GoGetComponents)) return NoComponents;
			try { return go.GetComponents(t) ?? NoComponents; } catch { return NoComponents; }
		}

		internal static Il2CppReferenceArray<Component> GetComponentsSafe(this Component c, Il2CppSystem.Type t)
			=> c == null ? NoComponents : c.gameObject.GetComponentsSafe(t);

		internal static Il2CppReferenceArray<Component> GetComponentsInChildrenSafe(this GameObject go, Il2CppSystem.Type t, bool includeInactive = false)
		{
			if (go == null || !Live.UsableType(t)) return NoComponents;
			try { return go.GetComponentsInChildren(t, includeInactive) ?? NoComponents; } catch { return NoComponents; }
		}

		internal static Il2CppReferenceArray<Component> GetComponentsInChildrenSafe(this Component c, Il2CppSystem.Type t, bool includeInactive = false)
			=> c == null ? NoComponents : c.gameObject.GetComponentsInChildrenSafe(t, includeInactive);

		internal static Component AddComponentSafe(this GameObject go, Il2CppSystem.Type t)
		{
			if (go == null || !Live.UsableType(t)) return null;
			try { return go.AddComponent(t); } catch { return null; }
		}
	}
}
