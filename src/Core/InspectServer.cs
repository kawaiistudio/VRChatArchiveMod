using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// ASK THE RUNNING GAME A QUESTION, AND GET AN ANSWER BACK.
	//
	// Every menu bug fixed on VRChat 1903 cost a full build-deploy-relaunch-read-the-log cycle, and
	// most of them were one fact away from being trivial: what is this row actually called, is it
	// active, how tall is it, where does its label live. The 2 MB DUMP MENU TREE action answers all of
	// that at once, which is why it finally unblocked the avatar sidebar -- but it needs a human to
	// press a button, it writes a file, and it says everything except the one thing being asked.
	//
	// So this opens a tiny HTTP server on loopback that answers targeted questions:
	//
	//     /q?op=find&name=Avatars%20Container      where is it, and is it alive
	//     /q?op=tree&path=<path>&depth=2           children, with size / active / components
	//     /q?op=comps&path=<path>                  component names on one object
	//     /q?op=texts&path=<path>                  every TMP label beneath it, with its text
	//
	// TWO RULES MAKE IT SAFE, and they are the whole reason a naive version of this crashes the game:
	//
	//   1. UNITY IS READ ON THE MAIN THREAD, NEVER ON THE SOCKET THREAD. A request is queued and the
	//      frame pump runs it; the socket thread only waits for the answer. Touching a Transform from
	//      a background thread is an access violation no try/catch can catch.
	//   2. LOOPBACK ONLY, AND OFF BY DEFAULT. It is bound to 127.0.0.1 so nothing off the machine can
	//      reach it, and it stays shut unless [Diagnostics] InspectPort is switched on -- an open port
	//      that can read the scene has no business being on in a build shipped to users.
	//
	// TcpListener rather than HttpListener: HttpListener needs a URL ACL reservation on Windows and
	// fails with "access denied" for a normal user, which is exactly the sort of thing that would make
	// this look broken when it is merely unprivileged.
	internal static class InspectServer
	{
		private sealed class Job
		{
			public string Query;
			public string Answer;
			public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
		}

		private static readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();
		private static TcpListener _listener;
		private static Thread _thread;
		private static bool _running;

		internal static int Port { get; private set; }
		internal static string Status = "off";

		internal static void Start(int port)
		{
			if (_running) return;
			try
			{
				_listener = new TcpListener(IPAddress.Loopback, port);
				_listener.Start();
				Port = port;
				_running = true;
				_thread = new Thread(Serve) { IsBackground = true, Name = "VA-Inspect" };
				_thread.Start();
				Status = "on 127.0.0.1:" + port;
				VRChatArchiveModPlugin.Logger.LogInfo("[Inspect] serveur d'inspection sur http://127.0.0.1:" + port
					+ "/q?op=find&name=... — la hierarchie est lue sur le thread principal, jamais sur le socket.");
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[Inspect] impossible d'ouvrir le port " + port + " : " + e.Message);
			}
		}

		internal static void Stop()
		{
			_running = false;
			try { _listener?.Stop(); } catch { }
			_listener = null;
			Status = "off";
		}

		// Called from the frame pump: this is the ONLY place Unity objects are touched.
		internal static void Pump()
		{
			int budget = 4;   // a burst of queries must not stall the frame
			while (budget-- > 0 && _jobs.TryDequeue(out Job j))
			{
				try { j.Answer = Answer(j.Query); }
				catch (Exception e) { j.Answer = "{\"error\":" + Str(e.Message) + "}"; }
				finally { j.Done.Set(); }
			}
		}

		// ---------------------------------------------------------------- socket

		private static void Serve()
		{
			while (_running)
			{
				TcpClient c = null;
				try { c = _listener.AcceptTcpClient(); }
				catch { if (!_running) return; continue; }

				try
				{
					using (c)
					using (var ns = c.GetStream())
					{
						c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
						c.NoDelay = true;
						// A graceful close, so the last bytes actually reach the client.
						c.LingerState = new LingerOption(true, 2);

						string req = ReadLine(ns);

						// DRAIN THE REST OF THE REQUEST BEFORE ANSWERING.
						//
						// Only the request line is needed, but curl also sends Host/User-Agent/Accept and
						// a blank line. Closing a socket with unread bytes still in the receive buffer
						// makes Windows send an RST instead of a FIN, so the client sees "connection
						// forcibly closed" and no response at all -- even though the answer was written.
						// That is exactly what this looked like: the port listened, TCP connected, and
						// every query came back as a reset.
						try
						{
							while (true)
							{
								string h = ReadLine(ns);
								if (h.Length == 0) break;      // blank line: end of headers
							}
						}
						catch { }
						// "GET /q?op=find&name=X HTTP/1.1"
						string query = "";
						int a = req.IndexOf(' '), b = req.LastIndexOf(' ');
						if (a > 0 && b > a) query = req.Substring(a + 1, b - a - 1);

						var job = new Job { Query = query };
						_jobs.Enqueue(job);
						// The main thread may be busy loading a world; three seconds is generous and
						// still bounded, so a wedged frame cannot hold the socket thread for ever.
						// 3 s was not enough and the answer came back empty. A scene-wide search runs ON the
						// frame, and the frame itself was 125 ms while the menu was open, so the walk simply
						// outlived the deadline -- reporting "the main thread did not answer" about a main
						// thread that was answering, slowly. This is a diagnostic socket on loopback, off by
						// default: waiting is free, a wrong answer is not.
						string body = job.Done.Wait(20000) ? (job.Answer ?? "{}") : "{\"error\":\"timeout: le thread principal n'a pas repondu en 20 s\"}";

						byte[] payload = Encoding.UTF8.GetBytes(body);
						byte[] head = Encoding.ASCII.GetBytes(
							"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
							+ "Content-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
						ns.Write(head, 0, head.Length);
						ns.Write(payload, 0, payload.Length);
						ns.Flush();
					}
				}
				catch { }
			}
		}

		private static string ReadLine(NetworkStream ns)
		{
			var sb = new StringBuilder(256);
			int ch;
			while (sb.Length < 4096 && (ch = ns.ReadByte()) >= 0)
			{
				if (ch == '\n') break;
				if (ch != '\r') sb.Append((char)ch);
			}
			return sb.ToString();
		}

		// ---------------------------------------------------------------- answers (main thread)

		private static string Answer(string query)
		{
			var q = Parse(query);
			string op = Get(q, "op");
			switch (op)
			{
				case "find": return Find(Get(q, "name"), Int(q, "limit", 25));
				case "tree": return Tree(Get(q, "path"), Int(q, "depth", 2));
				case "comps": return Comps(Get(q, "path"));
				case "texts": return Texts(Get(q, "path"), Int(q, "limit", 60));
				case "rect":  return Rect(Get(q, "path"));
				case "fields": return Fields(Get(q, "path"));
				case "raw": return Raw(Get(q, "path"), Int(q, "comp", -1));
				case "click": return Click(Get(q, "path"), null);
				case "clicktext": return Click(null, Get(q, "text"));
				case "screenpos": return ScreenPos(Get(q, "path"), Get(q, "text"));
				default:
					return "{\"ops\":[\"find&name=\",\"tree&path=&depth=\",\"comps&path=\",\"texts&path=\",\"rect&path=\",\"fields&path=\",\"raw&path=&comp=\",\"click&path=\",\"clicktext&text=\",\"screenpos&path=|text=\"]}";
			}
		}

		// Every object whose name CONTAINS this, anywhere in the loaded scene, with its full path,
		// whether it is active, and its size. Inactive objects are included on purpose: the whole
		// avatar-sidebar hunt turned on telling a live row from an inactive prefab template.
		private static string Find(string name, int limit)
		{
			if (string.IsNullOrEmpty(name)) return "{\"error\":\"name manquant\"}";
			var sb = new StringBuilder("{\"hits\":[");
			int n = 0;
			try
			{
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (n >= limit) break;
					if (t == null || !NativeGuard.Alive(t)) continue;
					string tn; try { tn = t.name; } catch { continue; }
					if (tn == null || tn.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					if (n++ > 0) sb.Append(',');
					Emit(sb, t);
				}
			}
			catch { }
			return sb.Append("],\"count\":").Append(n).Append('}').ToString();
		}

		private static string Tree(string path, int depth)
		{
			Transform t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder("{\"node\":");
			Emit(sb, t);
			sb.Append(",\"children\":[");
			Walk(sb, t, Math.Max(0, Math.Min(depth, 4)), 0);
			return sb.Append("]}").ToString();
		}

		private static void Walk(StringBuilder sb, Transform t, int depth, int written)
		{
			if (depth <= 0) return;
			int n = 0;
			try { n = t.childCount; } catch { return; }
			for (int i = 0; i < n && i < 60; i++)
			{
				Transform c = null;
				try { c = t.GetChild(i); } catch { continue; }
				if (c == null) continue;
				if (sb[sb.Length - 1] != '[') sb.Append(',');
				Emit(sb, c);
				if (depth > 1)
				{
					sb.Append(",{\"under\":\"").Append(Esc(SafeName(c))).Append("\",\"kids\":[");
					Walk(sb, c, depth - 1, 0);
					sb.Append("]}");
				}
			}
		}

		private static string Comps(string path)
		{
			Transform t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder("{\"path\":\"").Append(Esc(PathOf(t))).Append("\",\"components\":[");
			int n = 0;
			try
			{
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null || !NativeGuard.Alive(c)) continue;
					if (n++ > 0) sb.Append(',');
					sb.Append(Str(MenuCard.Il2CppNameOf(c)));
				}
			}
			catch { }
			return sb.Append("]}").ToString();
		}

		// Every TMP label under this object and what it currently reads -- the fastest way to find a
		// row by what the user actually sees, which is the only identifier VRChat never renames.
		private static string Texts(string path, int limit)
		{
			Transform t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder("{\"texts\":[");
			int n = 0;
			try
			{
				foreach (var tx in t.GetComponentsInChildren<TMPro.TMP_Text>(true))
				{
					if (n >= limit) break;
					if (tx == null || !NativeGuard.Alive(tx)) continue;
					string v; try { v = tx.text; } catch { continue; }
					if (string.IsNullOrEmpty(v)) continue;
					if (n++ > 0) sb.Append(',');
					sb.Append("{\"text\":").Append(Str(v))
					  .Append(",\"path\":").Append(Str(PathOf(tx.transform)))
					  .Append(",\"active\":").Append(Live(tx.gameObject) ? "true" : "false").Append('}');
				}
			}
			catch { }
			return sb.Append("],\"count\":").Append(n).Append('}').ToString();
		}

		// ---------------------------------------------------------------- helpers

		private static void Emit(StringBuilder sb, Transform t)
		{
			float w = 0f, h = 0f;
			// TryCast, NEVER `as`. A C# `as` is a MANAGED cast and returns null for every il2cpp
			// proxy, so every size this server has ever reported was 0x0 -- including the ones the
			// mod's own "is it on screen" logs printed back at me. A measurement tool that silently
			// reads zero is worse than none: it looks like data.
			try { var rt = t.TryCast<RectTransform>(); if (rt != null) { w = rt.rect.width; h = rt.rect.height; } } catch { }
			sb.Append("{\"name\":").Append(Str(SafeName(t)))
			  .Append(",\"path\":").Append(Str(PathOf(t)))
			  .Append(",\"active\":").Append(Live(t.gameObject) ? "true" : "false")
			  .Append(",\"w\":").Append(((int)w).ToString())
			  .Append(",\"h\":").Append(((int)h).ToString())
			  .Append(",\"kids\":").Append(Kids(t))
			  .Append('}');
		}

		private static int Kids(Transform t) { try { return t.childCount; } catch { return -1; } }
		private static bool Live(GameObject g) { try { return g.activeInHierarchy; } catch { return false; } }
		private static string SafeName(Transform t) { try { return t.name ?? "?"; } catch { return "?"; } }

		private static string PathOf(Transform t)
		{
			try
			{
				string p = SafeName(t);
				for (Transform up = t.parent; up != null && p.Length < 400; up = up.parent) p = SafeName(up) + "/" + p;
				return p;
			}
			catch { return "?"; }
		}

		// The screen pixel an element sits on, shared by the click and the screenpos op.
		private static Vector2 ScreenPointOf(Transform t)
		{
			try
			{
				var rt = t.TryCast<RectTransform>();
				if (rt == null) return Vector2.zero;
				var corners = new Il2CppStructArray<Vector3>(4);
				rt.GetWorldCorners(corners);
				Vector3 world = Vector3.zero;
				for (int i = 0; i < 4; i++) world += corners[i];
				world /= 4f;

				Canvas canvas = null;
				try { canvas = t.GetComponentInParent<Canvas>(); } catch { }
				Camera cam = null;
				if (canvas != null)
				{
					try
					{
						if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
							cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
					}
					catch { }
				}
				return RectTransformUtility.WorldToScreenPoint(cam, world);
			}
			catch { return Vector2.zero; }
		}

		// WHERE A UI ELEMENT IS ON SCREEN, so a REAL mouse click can be aimed at it.
		//
		// Synthetic ExecuteEvents reached the right objects and VRChat ignored them: its buttons do not
		// listen on IPointerClickHandler the way a plain Unity button does. The one input path that
		// cannot be ignored is the operating system's own -- move the cursor here and click. This op
		// only reports the pixel; the click itself is sent from outside the game.
		//
		// Screen-space-overlay canvases put world coordinates directly in screen pixels, and VRChat's
		// menu canvas is one, so the corner average is already the answer. Reported with the screen
		// size so a caller can sanity-check the mapping instead of trusting it.
		private static string ScreenPos(string path, string text)
		{
			try
			{
				Transform t = !string.IsNullOrEmpty(path) ? ByPath(path) : ByVisibleText(text);
				if (t == null) return "{\"error\":\"cible introuvable\"}";
				var rt = t.TryCast<RectTransform>();
				if (rt == null) return "{\"error\":\"pas un RectTransform\"}";

				var corners = new Il2CppStructArray<Vector3>(4);
				rt.GetWorldCorners(corners);
				Vector3 world = Vector3.zero;
				for (int i = 0; i < 4; i++) world += corners[i];
				world /= 4f;

				// THE CANVAS DECIDES HOW WORLD MAPS TO PIXELS, and this menu is not an overlay: its
				// rect is 2900x1244 on a 1920x1080 screen, so raw world corners are not pixels. Only
				// Screen Space - Overlay lets you read them straight; every other mode needs the
				// canvas's own camera, and World Space (which VRChat uses so the menu works in VR)
				// needs the rendering camera too. Taking the corners literally reported 0,2.
				Canvas canvas = null;
				try { canvas = t.GetComponentInParent<Canvas>(); } catch { }
				string mode = "?";
				Camera cam = null;
				if (canvas != null)
				{
					try { mode = canvas.renderMode.ToString(); } catch { }
					try
					{
						if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
							cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
					}
					catch { }
				}

				Vector2 sp;
				try { sp = RectTransformUtility.WorldToScreenPoint(cam, world); }
				catch { sp = new Vector2(world.x, world.y); }

				bool vis = false;
				try { vis = t.gameObject.activeInHierarchy; } catch { }
				return "{\"path\":" + Str(PathOf(t))
					+ ",\"x\":" + ((int)sp.x) + ",\"y\":" + ((int)sp.y)
					+ ",\"screenW\":" + Screen.width + ",\"screenH\":" + Screen.height
					+ ",\"canvas\":" + Str(mode) + ",\"cam\":" + Str(cam == null ? "(aucune)" : cam.name)
					+ ",\"active\":" + (vis ? "true" : "false") + "}";
			}
			catch (Exception e) { return "{\"error\":" + Str(e.Message) + "}"; }
		}

		// DRIVING THE MENU, THE WAY A REAL CLICK DOES.
		//
		// The owner works remotely and cannot always be at the machine to click a card, and some facts
		// about this build can only be learned while the menu is in a particular state. So the socket
		// can dispatch a pointer click -- through Unity's OWN EventSystem, which is the exact path a
		// real click takes: ExecuteEvents hands the event to whatever IPointerClickHandler components
		// sit on the object, VRChat's obfuscated ones included. Nothing of theirs is called directly,
		// no method is resolved, no proxy is touched. If a real click is safe, this is safe.
		//
		// Matched by TEXT as well as by path, because what the user reads ("avatars1", "Uploaded") is
		// the stable thing on an obfuscated build -- object names and paths rot between versions.
		private static string Click(string path, string text)
		{
			try
			{
				Transform target = null;
				if (!string.IsNullOrEmpty(path)) target = ByPath(path);
				else if (!string.IsNullOrEmpty(text)) target = ByVisibleText(text);
				if (target == null) return "{\"error\":\"cible introuvable\"}";

				string where = PathOf(target);
				var es = UnityEngine.EventSystems.EventSystem.current;
				if (es == null) return "{\"error\":\"aucun EventSystem\"}";

				// A REAL CLICK CARRIES A RAYCAST, and the first attempt did not.
				//
				// Dispatching a bare PointerEventData reached VRChat's handlers and they ignored it --
				// correctly, because a click with no position, no pressPosition and no raycast result is
				// not a click, it is an empty struct. Unity's own input module fills those in before it
				// dispatches, so this does the same: project the element to screen space, raycast the UI
				// there through the EventSystem, and hand the handlers the result they expect.
				Vector2 screen = ScreenPointOf(target);
				var data = new UnityEngine.EventSystems.PointerEventData(es)
				{
					button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
					clickCount = 1,
					clickTime = VaClock.Now,
					position = screen,
					pressPosition = screen,
					eligibleForClick = true,
					useDragThreshold = true,
				};

				// What the UI would actually hit at that pixel. If the raycast lands on our target (or a
				// child of it) the click is genuine; if it lands elsewhere, something is covering it and
				// saying so is more useful than pretending the click worked.
				var hits = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
				try { es.RaycastAll(data, hits); } catch { }
				GameObject hit = null;
				if (hits.Count > 0)
				{
					var top = hits[0];
					data.pointerCurrentRaycast = top;
					data.pointerPressRaycast = top;
					hit = top.gameObject;
				}

				var start = hit != null ? hit : target.gameObject;
				var handler = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(start);
				if (handler == null)
					return "{\"error\":\"aucun gestionnaire de clic\",\"asked\":" + Str(where)
						+ ",\"raycast\":" + Str(hit == null ? "(rien)" : hit.name) + "}";

				data.pointerPress = handler;
				data.rawPointerPress = start;
				data.pointerEnter = start;

				UnityEngine.EventSystems.ExecuteEvents.Execute(start, data, UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
				UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
				UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
				UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
				// Some UIs listen on submit rather than click; both are harmless to send.
				UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data, UnityEngine.EventSystems.ExecuteEvents.submitHandler);

				return "{\"clicked\":" + Str(PathOf(handler.transform)) + ",\"asked\":" + Str(where)
					+ ",\"raycast\":" + Str(hit == null ? "(rien)" : hit.name)
					+ ",\"at\":\"" + ((int)screen.x) + "," + ((int)screen.y) + "\",\"hits\":" + hits.Count + "}";
			}
			catch (Exception e) { return "{\"error\":" + Str(e.Message) + "}"; }
		}

		// The first ACTIVE label reading exactly this, anywhere in the menus. Text is what the user
		// sees and the only thing that reliably survives a build.
		private static Transform ByVisibleText(string want)
		{
			try
			{
				foreach (var root in Roots())
				{
					if (root == null) continue;
					foreach (var tx in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
					{
						if (tx == null) continue;
						string v; try { v = (tx.text ?? "").Trim(); } catch { continue; }
						if (!string.Equals(v, want, StringComparison.OrdinalIgnoreCase)) continue;
						try { if (!tx.gameObject.activeInHierarchy) continue; } catch { continue; }
						return tx.transform;
					}
				}
			}
			catch { }
			return null;
		}

		// THE LIVE FIELD NAMES OF EVERY COMPONENT ON AN OBJECT.
		//
		// An obfuscated class still keeps its UNITY-SERIALIZED field names in clear -- a prefab has
		// to reference them by name to deserialize. So "_selectedAvatarPanel" or "_avatarBounds"
		// identifies a renamed class BY NAME, which beats every shape heuristic: shape can tie,
		// a name cannot. Whether those names survive on a given build is not something metadata
		// can answer -- only the running game can, which is what this asks it.
		private static string Fields(string path)
		{
			var t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder();
			sb.Append("{\"path\":").Append(Str(PathOf(t))).Append(",\"components\":[");
			bool firstComp = true;
			try
			{
				foreach (var c in t.GetComponents<Component>())
				{
					if (c == null) continue;
					string cn = "?";
					try { cn = MenuCard.Il2CppNameOf(c); } catch { }
					if (!firstComp) sb.Append(",");
					firstComp = false;
					sb.Append("{\"type\":").Append(Str(cn.Length > 20 ? "<obf>" : cn)).Append(",\"fields\":[");

					bool firstF = true;
					try
					{
						IntPtr ptr = IntPtr.Zero;
						try { ptr = c.Pointer; } catch { }
						if (ptr != IntPtr.Zero && NativeGuard.IsLiveObject(ptr))
						{
							IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(ptr);
							if (klass != IntPtr.Zero)
							{
								var fs = MemberAlign.LiveFields(klass);
								int n = 0;
								foreach (var f in fs)
								{
									if (n++ >= 40) break;
									string fn = null;
									try { fn = MemberAlign.LiveFieldName(f); } catch { }
									if (string.IsNullOrEmpty(fn)) continue;
									// An obfuscated field name is a run of unreadable glyphs; only the
									// readable ones identify anything, so say which is which.
									bool plain = true;
									foreach (char ch in fn) { if (ch > 126) { plain = false; break; } }
									if (!firstF) sb.Append(",");
									firstF = false;
									sb.Append(Str(plain ? fn : "<obf>"));
								}
							}
						}
					}
					catch { }
					sb.Append("]}");
				}
			}
			catch { }
			sb.Append("]}");
			return sb.ToString();
		}

		// RAW FIELD DUMP — every field of one component with its OFFSET, TYPE and LIVE VALUE.
		//
		// This is the offset-hunting tool over HTTP: instead of adding diagnostic logging to a module and
		// relaunching, ask the running game "what does component #n hold, right now". Floats, ints, bools
		// and enums are read and shown; reference fields show their pointer (or null). Obfuscated field
		// names read <obf> — the offset and value are what actually identify a timer, a flag, a counter.
		// Pointers-only, guarded like the rest of this server; comp defaults to every component (capped).
		private static unsafe string Raw(string path, int comp)
		{
			var t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder();
			sb.Append("{\"path\":").Append(Str(PathOf(t))).Append(",\"components\":[");
			bool firstComp = true;
			try
			{
				var comps = t.GetComponents<Component>();
				for (int ci = 0; ci < comps.Length; ci++)
				{
					if (comp >= 0 && ci != comp) continue;
					var c = comps[ci];
					if (c == null) continue;
					IntPtr ptr = IntPtr.Zero;
					try { ptr = c.Pointer; } catch { }
					if (ptr == IntPtr.Zero || !NativeGuard.IsLiveObject(ptr)) continue;
					IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(ptr);
					if (klass == IntPtr.Zero) continue;

					int size = 0;
					try { size = (int)Il2CppInterop.Runtime.Runtime.UnityVersionHandler
						.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppClass*)klass).InstanceSize; }
					catch { size = 0; }
					if (size <= 0 || size > 0x20000) size = 0x400;
					if (!NativeGuard.IsReadable(ptr, size)) continue;

					string cn = "?";
					try { cn = MenuCard.Il2CppNameOf(c); } catch { }
					if (!firstComp) sb.Append(",");
					firstComp = false;
					sb.Append("{\"i\":").Append(ci).Append(",\"type\":")
						.Append(Str(cn.Length > 20 ? "<obf>" : cn)).Append(",\"size\":").Append(size)
						.Append(",\"fields\":[");

					bool firstF = true; int nf = 0;
					foreach (var f in MemberAlign.LiveFields(klass))
					{
						if (nf++ >= 80) break;
						int flags; try { flags = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_flags(f); } catch { continue; }
						if ((flags & 0x0010) != 0) continue;   // skip static: its offset is not in the instance
						uint off; try { off = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
						if (off < 0x8 || off + 1 > size) continue;

						string tn = FieldTypeName(f);
						string fn = null; try { fn = MemberAlign.LiveFieldName(f); } catch { }
						bool plain = !string.IsNullOrEmpty(fn);
						if (plain) foreach (char ch in fn) if (ch > 126) { plain = false; break; }

						string val = ReadValue(ptr, (int)off, tn, size);
						if (!firstF) sb.Append(",");
						firstF = false;
						sb.Append("{\"o\":\"0x").Append(off.ToString("X")).Append("\",\"n\":")
							.Append(Str(plain ? fn : "<obf>")).Append(",\"t\":").Append(Str(tn ?? "?"))
							.Append(",\"v\":").Append(Str(val)).Append("}");
					}
					sb.Append("]}");
				}
			}
			catch (Exception e) { return "{\"error\":" + Str(e.Message) + "}"; }
			sb.Append("]}");
			return sb.ToString();
		}

		private static string FieldTypeName(IntPtr f)
		{
			try
			{
				IntPtr tp = MemberAlign.FieldTypePtr(f);
				if (tp == IntPtr.Zero) return null;
				IntPtr fk = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(tp);
				if (fk == IntPtr.Zero) return null;
				IntPtr np = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(fk);
				return np == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(np);
			}
			catch { return null; }
		}

		private static string ReadValue(IntPtr obj, int off, string tn, int size)
		{
			try
			{
				switch (tn)
				{
					case "Single":  return System.BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(obj, off)).ToString("0.###");
					case "Double":  return System.BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(obj, off)).ToString("0.###");
					case "Boolean": return System.Runtime.InteropServices.Marshal.ReadByte(obj, off) != 0 ? "true" : "false";
					case "Byte":    return System.Runtime.InteropServices.Marshal.ReadByte(obj, off).ToString();
					case "SByte":   return ((sbyte)System.Runtime.InteropServices.Marshal.ReadByte(obj, off)).ToString();
					case "Int16":   return System.Runtime.InteropServices.Marshal.ReadInt16(obj, off).ToString();
					case "UInt16":  return ((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(obj, off)).ToString();
					case "Int32":   return System.Runtime.InteropServices.Marshal.ReadInt32(obj, off).ToString();
					case "UInt32":  return ((uint)System.Runtime.InteropServices.Marshal.ReadInt32(obj, off)).ToString();
					case "Int64":   return System.Runtime.InteropServices.Marshal.ReadInt64(obj, off).ToString();
					case "UInt64":  return ((ulong)System.Runtime.InteropServices.Marshal.ReadInt64(obj, off)).ToString();
					default:
						// reference / struct: show the pointer (or null) — enough to tell "set" from "empty".
						if (off + 8 > size) return "?";
						IntPtr p = System.Runtime.InteropServices.Marshal.ReadIntPtr(obj, off);
						return p == IntPtr.Zero ? "null" : ("0x" + ((long)p).ToString("X"));
				}
			}
			catch { return "?"; }
		}

		// EVERYTHING THAT DECIDES HOW BIG A THING IS, in one answer.
		//
		// "The cards are squashed" and "there is a gap at the top" are layout questions, and a
		// hierarchy dump cannot answer either: the numbers live on the RectTransform and on the
		// layout drivers beside it. Reported for the object AND its parent, because a child in a
		// layout group has no say in its own size -- the parent decides.
		private static string Rect(string path)
		{
			var t = ByPath(path);
			if (t == null) return "{\"error\":\"chemin introuvable\"}";
			var sb = new StringBuilder();
			sb.Append("{\"path\":").Append(Str(PathOf(t)));
			sb.Append(",\"self\":").Append(RectOf(t));
			try { if (t.parent != null) sb.Append(",\"parent\":").Append(RectOf(t.parent)); } catch { }
			sb.Append("}");
			return sb.ToString();
		}

		private static string RectOf(Transform t)
		{
			var sb = new StringBuilder("{");
			try { sb.Append("\"name\":").Append(Str(t.name)); } catch { }
			try { sb.Append(",\"active\":").Append(t.gameObject.activeInHierarchy ? "true" : "false"); } catch { }
			try { sb.Append(",\"children\":").Append(t.childCount.ToString()); } catch { }
			try
			{
				var rt = t.TryCast<RectTransform>();
				if (rt != null)
				{
					sb.Append(",\"rect\":").Append(Str(F(rt.rect.width) + "x" + F(rt.rect.height)));
					sb.Append(",\"sizeDelta\":").Append(Str(F(rt.sizeDelta.x) + "x" + F(rt.sizeDelta.y)));
					sb.Append(",\"anchors\":").Append(Str(F(rt.anchorMin.x) + "," + F(rt.anchorMin.y) + " - "
						+ F(rt.anchorMax.x) + "," + F(rt.anchorMax.y)));
					sb.Append(",\"pivot\":").Append(Str(F(rt.pivot.x) + "," + F(rt.pivot.y)));
					sb.Append(",\"pos\":").Append(Str(F(rt.anchoredPosition.x) + "," + F(rt.anchoredPosition.y)));
				}
			}
			catch { }
			try
			{
				var le = t.GetComponent<UnityEngine.UI.LayoutElement>();
				if (le != null) sb.Append(",\"layoutElement\":").Append(Str("min " + F(le.minWidth) + "x" + F(le.minHeight)
					+ " pref " + F(le.preferredWidth) + "x" + F(le.preferredHeight)
					+ " flex " + F(le.flexibleWidth) + "x" + F(le.flexibleHeight)
					+ (le.ignoreLayout ? " IGNORED" : "")));
			}
			catch { }
			try
			{
				var g = t.GetComponent<UnityEngine.UI.GridLayoutGroup>();
				if (g != null) sb.Append(",\"grid\":").Append(Str("cell " + F(g.cellSize.x) + "x" + F(g.cellSize.y)
					+ " spacing " + F(g.spacing.x) + "," + F(g.spacing.y)
					+ " padding L" + g.padding.left + " R" + g.padding.right + " T" + g.padding.top + " B" + g.padding.bottom
					+ " constraint " + g.constraint.ToString() + " " + g.constraintCount));
			}
			catch { }
			try
			{
				var v = t.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
				if (v != null) sb.Append(",\"vlayout\":").Append(Str("spacing " + F(v.spacing)
					+ " padding L" + v.padding.left + " R" + v.padding.right + " T" + v.padding.top + " B" + v.padding.bottom
					+ " ctrlH " + v.childControlHeight + " forceH " + v.childForceExpandHeight));
			}
			catch { }
			try
			{
				var f = t.GetComponent<UnityEngine.UI.ContentSizeFitter>();
				if (f != null) sb.Append(",\"fitter\":").Append(Str("h " + f.horizontalFit.ToString() + " v " + f.verticalFit.ToString()));
			}
			catch { }
			try
			{
				var cg = t.GetComponent<CanvasGroup>();
				if (cg != null) sb.Append(",\"canvasGroup\":").Append(Str("alpha " + F(cg.alpha)
					+ (cg.interactable ? "" : " NOT-INTERACTABLE")));
			}
			catch { }
			sb.Append("}");
			return sb.ToString();
		}

		private static string F(float v)
		{
			try { return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
			catch { return "?"; }
		}

		// A path is matched from the END: "Avatars Container/Cell_MM_SidebarListItem prefab(Clone)"
		// finds that object wherever it lives, so a query does not have to spell the whole canvas.
		//
		// THE FIRST VERSION COULD NOT ANSWER AT ALL ONCE A WORLD WAS LOADED.
		//
		// It walked Resources.FindObjectsOfTypeAll<Transform>() -- every Transform the PROCESS has
		// loaded, assets and prefabs included, which is hundreds of thousands in a populated
		// instance -- and for each one paid a NativeGuard.Alive (a VirtualQuery) and a full
		// PathOf() that rebuilds the whole ancestor chain into a string. That is minutes of work,
		// so every query died on its own deadline and reported "the main thread did not answer"
		// about a main thread that was answering, very slowly. The socket was unusable exactly
		// when it was needed: in game, with the menu open.
		//
		// Two orders of magnitude come from doing the obvious thing instead:
		//   1. DESCEND. A multi-segment path is walked from a root, one Find() per level, which is
		//      a handful of name compares per step rather than a sweep of the process.
		//   2. When a sweep is unavoidable, compare the cheap thing FIRST -- the object's own name
		//      against the LAST segment -- and only build a path for the few that match.
		private static Transform ByPath(string path)
		{
			if (string.IsNullOrEmpty(path)) return null;
			try
			{
				string[] seg = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
				if (seg.Length == 0) return null;

				// ---- 1. descend from a root -------------------------------------------------
				foreach (var root in Roots())
				{
					var hit = Descend(root, seg);
					if (hit != null) return hit;
				}

				// ---- 2. bounded sweep, name first -------------------------------------------
				string last = seg[seg.Length - 1];
				Transform best = null;
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null) continue;
					string n; try { n = t.name; } catch { continue; }
					if (n != last) continue;                       // the cheap test, done first
					if (!NativeGuard.Alive(t)) continue;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					string full = PathOf(t);
					if (full == path) return t;
					if (best == null && full.EndsWith(path, StringComparison.Ordinal)) best = t;
				}
				return best;
			}
			catch { return null; }
		}

		// Walk the segments down from `from`, allowing the FIRST segment to be `from` itself so a
		// caller can spell the path either way. Children are matched by name only -- no path is
		// built and nothing outside this branch is touched.
		private static Transform Descend(Transform from, string[] seg)
		{
			try
			{
				if (from == null || seg.Length == 0) return null;
				int i = 0;
				string rn; try { rn = from.name; } catch { return null; }
				if (rn == seg[0]) i = 1;

				Transform cur = from;
				for (; i < seg.Length; i++)
				{
					Transform next = null;
					int kids; try { kids = cur.childCount; } catch { return null; }
					for (int k = 0; k < kids; k++)
					{
						Transform c; try { c = cur.GetChild(k); } catch { continue; }
						if (c == null) continue;
						string cn; try { cn = c.name; } catch { continue; }
						if (cn == seg[i]) { next = c; break; }
					}

					// Not a direct child: allow ONE level of slack so a caller can leave out a
					// wrapper object, which is how these paths get written from a screenshot.
					if (next == null)
					{
						for (int k = 0; k < kids && next == null; k++)
						{
							Transform c; try { c = cur.GetChild(k); } catch { continue; }
							if (c == null) continue;
							int gk; try { gk = c.childCount; } catch { continue; }
							for (int g = 0; g < gk; g++)
							{
								Transform gc; try { gc = c.GetChild(g); } catch { continue; }
								if (gc == null) continue;
								string gn; try { gn = gc.name; } catch { continue; }
								if (gn == seg[i]) { next = gc; break; }
							}
						}
					}
					if (next == null) return null;
					cur = next;
				}
				return cur;
			}
			catch { return null; }
		}

		// Where a descent may start: the menus first (that is what these queries are almost always
		// about), then the active scene's own roots. Cheap -- no process-wide sweep.
		private static List<Transform> Roots()
		{
			var outp = new List<Transform>();
			try { var m = QuickMenu.Main(); if (m != null) outp.Add(m); } catch { }
			try { var r = QuickMenu.Root(); if (r != null) outp.Add(r); } catch { }
			try
			{
				var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
				var gos = sc.GetRootGameObjects();
				if (gos != null)
					foreach (var g in gos)
					{
						if (g == null) continue;
						try { outp.Add(g.transform); } catch { }
					}
			}
			catch { }
			return outp;
		}

		private static Dictionary<string, string> Parse(string query)
		{
			var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				int q = query.IndexOf('?');
				if (q < 0) return d;
				foreach (string kv in query.Substring(q + 1).Split('&'))
				{
					int e = kv.IndexOf('=');
					if (e <= 0) continue;
					d[kv.Substring(0, e)] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
				}
			}
			catch { }
			return d;
		}

		private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out string v) ? v : "";
		private static int Int(Dictionary<string, string> d, string k, int dflt)
			=> d.TryGetValue(k, out string v) && int.TryParse(v, out int n) ? n : dflt;

		private static string Str(string s) => "\"" + Esc(s) + "\"";
		private static string Esc(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			var sb = new StringBuilder(s.Length + 8);
			foreach (char c in s)
			{
				if (c == '"' || c == '\\') sb.Append('\\').Append(c);
				else if (c < 32) sb.Append(' ');
				else sb.Append(c);
			}
			return sb.ToString();
		}
	}
}
